using System.Threading.Channels;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using DfoServer.Network.Parsers;

namespace DfoServer.Network.Projection;

// 只保存本连接成功写出的列表基线；队伍及在线身份仍由PartyService和Session持有。
internal sealed class PartyDirectoryProjection : IAsyncDisposable
{
    readonly GameConnectionContext context;
    readonly PartyService parties;
    readonly TownCatalog towns;
    readonly ServerLog log;
    readonly Channel<bool> pending = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    readonly CancellationTokenSource stop;
    readonly Task worker;
    readonly Dictionary<ushort, PartySnapshot> known = [];
    long version;
    long selectionEpoch = -1;
    sealed record QueryState(PartyDirectoryQuery Query, long Epoch);
    QueryState? queryState;

    public PartyDirectoryProjection(GameConnectionContext context, PartyService parties, TownCatalog towns, ServerLog log)
    {
        this.context = context; this.parties = parties; this.towns = towns; this.log = log;
        stop = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        parties.Changed += Request;
        worker = RunAsync();
    }
    public void Request()
    {
        Interlocked.Increment(ref version);
        pending.Writer.TryWrite(true);
    }
    public void Query(PartyDirectoryQuery query)
    {
        Volatile.Write(ref queryState, new(query, context.Session.SelectionEpoch));
        Request();
    }
    TownScene Scene(PublicCharacterSnapshot identity) => towns.Scene(identity.Character.Position,
        identity.Character.CompletedQuests.ToHashSet());

    async Task RunAsync()
    {
        try
        {
            await foreach (bool _ in pending.Reader.ReadAllAsync(stop.Token))
            {
                long requested = Volatile.Read(ref version);
                var session = context.Session;
                var actor = session.CaptureSocialActor();
                var identity = actor is null ? null : session.PublicCharacter(actor.Character.Id);
                if (identity is null || identity.Phase != SessionPhase.Town) continue;
                if (selectionEpoch != actor!.Epoch)
                {
                    known.Clear();
                    selectionEpoch = actor.Epoch;
                }
                var scene = Scene(identity);
                long epoch = context.Sender.Epoch;
                var all = parties.Snapshot();
                var own = all.FirstOrDefault(p => p.Members.Any(m => ReferenceEquals(m.Actor.Session, session) && m.IsCurrent()));
                // 本人的名册已由PartyProjection提交；列表不能发action3误删刚加入的队伍。
                if (own is not null) known.Remove(own.Id);
                bool Guard() => actor!.IsCurrent() && session.Phase == SessionPhase.Town && context.Sender.Epoch == epoch &&
                    requested == Volatile.Read(ref version) && !stop.IsCancellationRequested;
                var active = Volatile.Read(ref queryState);
                if (active is not null && active.Epoch == actor.Epoch)
                {
                    // J面板发现范围为同频道；不能沿用城镇同屏实体的场景门禁。
                    var candidates = all.Where(p =>
                    {
                        var leader = p.Members.Single(m => m.Slot == p.Leader);
                        var target = leader.Actor.Session.PublicCharacter(leader.Actor.Character.Id);
                        return p.Settings.DirectoryVisible && target is { Phase: SessionPhase.Town } && target.Channel == identity.Channel &&
                            parties.IsCurrent(p) &&
                            // 普通频道无队伍时原生默认查询实收kind6，也应列出kind0普通队伍。
                            (p.Settings.Kind == active.Query.Kind || active.Query.Kind == 6 && p.Settings.Kind == 0) &&
                            (!active.Query.Recruiting || p.Members.Length < p.Settings.Capacity) &&
                            (active.Query.Keyword.Length == 0 || p.Settings.Title.Contains(active.Query.Keyword, StringComparison.OrdinalIgnoreCase) ||
                             target.Character.Name.Contains(active.Query.Keyword, StringComparison.OrdinalIgnoreCase));
                    }).OrderBy(p => p.Id).ToArray();
                    // 014636B0每次查询重置本地页槽为0；01463B54按包内序号绘制三页缓存。
                    var rows = candidates.Skip(active.Query.Page * 10).Take(30).ToArray();
                    var members = rows.SelectMany(p => p.Members).DistinctBy(m => m.Actor.Character.Id)
                        .Select(m => m.Actor.Session.PublicCharacter(m.Actor.Character.Id)).ToArray();
                    if (members.Any(m => m is null || m.Channel != identity.Channel)) continue;
                    bool QueryGuard() => Guard() &&
                        ReferenceEquals(active, Volatile.Read(ref queryState)) && rows.All(parties.IsCurrent) &&
                        members.All(m => m!.IsCurrent());
                    foreach (var member in members)
                        if (Scene(member!) != scene && (own is null || own.Members.All(m => m.Actor.Character.Id != member!.Character.Id)))
                            // 异区目录补齐公开资料；本队成员已安装，不能借查询重建引用。
                            await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(member!.Character, member.PvpGrade, member.Channel,
                                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), member.AdventureName,
                                member.Phase == SessionPhase.Town ? ActorSceneState.Town : ActorSceneState.Dungeon,
                                member.PremiumPcRoom, returningAdventurerType: member.ReturningAdventurerType, marriage: context.MarriageAppearance), "party_directory_actor", QueryGuard);
                    await context.Notify(NotiPacketType.PARTY_INFO, PartyPackets.Directory(identity.Channel.ServerIndex,
                        checked((byte)identity.Channel.Number), rows, candidates.Length), "party_directory_query",
                        QueryGuard);
                    continue;
                }
                var visible = new Dictionary<ushort, PartySnapshot>();
                if (own is null && !scene.IsRoom)
                    foreach (var party in all)
                    {
                        var leader = party.Members.Single(m => m.Slot == party.Leader);
                        var target = leader.Actor.Session.PublicCharacter(leader.Actor.Character.Id);
                        if (party.Settings.DirectoryVisible && target is { Phase: SessionPhase.Town } && target.Channel == identity.Channel &&
                            Scene(target) == scene && parties.IsCurrent(party)) visible.Add(party.Id, party);
                    }
                async Task<bool> Send(PartySnapshot party, bool removed)
                {
                    // 名册提交可能先于Changed通知。旧查询不能因IsCurrent过滤掉旧快照，
                    // 就把仍在当前场景的新版队伍误判成删除；删除也要核对本轮观察到的版本。
                    var captured = removed ? all.FirstOrDefault(p => p.Id == party.Id) : null;
                    if (!removed)
                        foreach (var member in party.Members)
                        {
                            var target = member.Actor.Session.PublicCharacter(member.Actor.Character.Id);
                            if (target is null) return false;
                            if (Scene(target) != scene)
                                await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(target.Character, target.PvpGrade, target.Channel,
                                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), target.AdventureName,
                                    target.Phase == SessionPhase.Town ? ActorSceneState.Town : ActorSceneState.Dungeon,
                                    target.PremiumPcRoom, returningAdventurerType: target.ReturningAdventurerType, marriage: context.MarriageAppearance), "party_directory_member_actor", () => Guard() && parties.IsCurrent(party));
                        }
                    bool writing = false;
                    await context.Notify(NotiPacketType.PARTY_INFO, PartyPackets.Information(party.Id,
                        identity.Channel.ServerIndex, checked((byte)identity.Channel.Number),
                        party.Members.Select(m => (m.Slot, m.Actor.Character.Id)).ToArray(), party.Leader, removed, party.Settings),
                        removed ? "party_directory_removed" : "party_directory_updated", () => writing = Guard() &&
                            (removed ? parties.Current(session)?.Id != party.Id && (captured is null || parties.IsCurrent(captured))
                                : parties.IsCurrent(party)));
                    return writing;
                }
                foreach (var old in known.Values.Where(p => !visible.ContainsKey(p.Id)).ToArray())
                    if (await Send(old, true)) known.Remove(old.Id);
                foreach (var party in visible.Values)
                    if ((!known.TryGetValue(party.Id, out var previous) || !ReferenceEquals(previous, party)) && await Send(party, false))
                        known[party.Id] = party;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            if (!stop.IsCancellationRequested)
            {
                log.Write("party_directory_failed", new { Connection = context.Id, Message = error.Message });
                context.Disconnect();
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        parties.Changed -= Request;
        stop.Cancel();
        pending.Writer.TryComplete();
        await worker;
        stop.Dispose();
    }
}
