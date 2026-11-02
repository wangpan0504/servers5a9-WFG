using DfoServer.Game.Strongway2019;
using DfoServer.Game.Party;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using System.Runtime.CompilerServices;

namespace DfoServer.Network.Projection;

internal sealed class Strongway2019Projection(Strongway2019RewardProjection? rewards = null,
    Strongway2019MatchingService? matching = null, TimeProvider? clock = null, Strongway2019Catalog? activity = null)
{
    internal TimeProvider Clock => clock ?? TimeProvider.System;
    internal Strongway2019Catalog? Activity => activity;
    internal Task RecoverRewardsAsync(GameConnectionContext context) => rewards?.RecoverAsync(context) ?? Task.CompletedTask;
    internal async Task<bool> RecoverReadyRewardsAsync(GameConnectionContext context, ushort character)
    {
        // 后台只零等待取得本人原命令门；忙时保留原match，下轮维护继续，不要求玩家重选角。
        if (!context.CommandGate.Wait(0)) return false;
        try
        {
            if (!context.Session.IsReadyCharacter(character)) return true;
            long epoch = context.Session.SelectionEpoch;
            bool Current() => context.Owner.IsCurrent && context.Session.Ready &&
                context.Session.SelectionEpoch == epoch && context.Session.Selected?.Id == character;
            // 复用原有界发送泵；后台恢复不持命令门等待socket。
            using var publication = context.Sender.QueuePublication(Current);
            await RecoverRewardsAsync(context);
            return true;
        }
        catch (InvalidOperationException) when (!context.Owner.IsCurrent) { return true; }
        finally { context.CommandGate.Release(); }
    }

    internal Func<GameConnectionContext, Func<SemaphoreSlim, bool>, Task>? Depart { get; set; }
    sealed class Delivery
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool TerminalSent { get; set; }
    }
    sealed class MatchDisplay
    {
        public Dictionary<ushort, Delivery> Recipients { get; } = [];
        public long? FirstTerminalTimestamp { get; set; }
    }
    // 仅保存已发送视图，不复制比赛裁决或奖励事实。
    readonly ConditionalWeakTable<Strongway2019Match, MatchDisplay> deliveries = new();

    // 正常结算会更新Selected；先完成原Run冻结的奖励，存储失败仍保留原准备供下一包重试。
    internal async Task BeforeSettlementAsync(GameConnectionContext context)
    {
        if (rewards?.Commit(context) is { } receipt) await rewards.CommittedAsync(context, receipt);
    }

    // 必须在原Session释放Formal/选角之前调用，避免无个人clear的匹配胜者丢失原奖励身份。
    public async Task BeforeDepartureAsync(GameConnectionContext context)
    {
        var current = context.Session.CurrentStrongway2019Match;
        var match = current ?? matching?.MatchFor(context.Session);
        if (match is not null)
        {
            if (ReferenceEquals(current, match) && context.Session.PartyDungeon is { } instance &&
                context.Session.CaptureStrongway2019Run(instance) is { } run)
                match.Competition.Exit(run, run.Scene.Identity);
            await TerminalAsync(match);
        }
        else if (context.Owner.IsCurrent && rewards?.Commit(context) is { } receipt) await rewards.CommittedAsync(context, receipt);
        matching?.Detach(context.Session);
    }

    public async Task DeliverAsync(GameConnectionContext context, Strongway2019Notice notice)
    {
        bool Current() => ReferenceEquals(context.Session, notice.Recipient.Actor.Session) && notice.Recipient.Actor.IsCurrent() &&
            ReferenceEquals(context.Session.CurrentStrongway2019Match, notice.Match);
        if (!Current()) return;
        if (notice.Kind == Strongway2019NoticeKind.Waiting)
        {
            var opponents = notice.Side == 0 ? notice.Match.Pair.Second.Party : notice.Match.Pair.First.Party;
            await SendAsync(context, () => context.Notify(NotiPacketType.DPL_E_DUNGEON_START_WAITING,
                Strongway2019Packets.Waiting(notice.Value, 0, checked((byte)notice.Side),
                    opponents.Members.Select(member => member.Actor.Character.Id).ToArray()), "strongway_matching_waiting", Current));
        }
        else if (notice.Kind == Strongway2019NoticeKind.Symbol)
            await SendAsync(context, () => context.Reply(CmdPacketType.DPL_E_DUNGEON_SYMBOL_SYNC,
                Strongway2019Packets.Symbol(notice.Side, notice.SymbolKind, notice.Value), "strongway_symbol", Current));
        else if (notice.Kind == Strongway2019NoticeKind.Terminal && notice.Match.Competition.Snapshot.Terminal is { } terminal)
        {
            var display = deliveries.GetValue(notice.Match, _ => new());
            var recipients = display.Recipients;
            Delivery delivery;
            lock (recipients)
            {
                if (!recipients.TryGetValue(notice.Recipient.Actor.Character.Id, out delivery!))
                    recipients.Add(notice.Recipient.Actor.Character.Id, delivery = new());
            }
            await delivery.Gate.WaitAsync();
            try
            {
                if (!Current() || delivery.TerminalSent) return;
                // 原Session缓存提交回执；重复终局投影可重试失败提交，不重复发奖。
                var receipt = rewards?.Commit(context);
                // 胜者尚未取得原Session提交回执时保留发布位，不能将暂不可提交误记为终局已完成。
                if (rewards is not null && terminal.WinnerSide == (int)notice.Side && receipt is null) return;
                if (receipt is not null) await rewards!.CommittedAsync(context, receipt);
                if (delivery.TerminalSent || !Current()) return;
                byte winningSide = terminal.WinnerSide is { } side ? checked((byte)side) : byte.MaxValue;
                // condition仅被原生存入结果，实际展示update不消费；不虚构原因枚举。
                await SendAsync(context, () => context.Notify(NotiPacketType.DPL_E_DUNGEON_RESULT,
                    Strongway2019Packets.Result(winningSide, 0), "strongway_result", Current));
                lock (recipients)
                {
                    delivery.TerminalSent = true;
                    display.FirstTerminalTimestamp ??= Clock.GetTimestamp();
                }
            }
            finally { delivery.Gate.Release(); }
        }
        else if (notice.Kind == Strongway2019NoticeKind.Departure)
            await DepartAsync(context, notice, Current);
    }

    async Task DepartAsync(GameConnectionContext context, Strongway2019Notice notice, Func<bool> current)
    {
        var depart = Depart;
        var instance = notice.Side == 0 ? notice.Match.First : notice.Match.Second;
        var party = context.Session.CurrentParty?.Invoke();
        if (depart is null || party is null || !RewardsReady(notice.Match) ||
            !ReferenceEquals(context.Session.PartyDungeon, instance) ||
            context.Session.CaptureStrongway2019Run(instance) is not { } run ||
            !notice.Match.Competition.Participants.Any(row => row.Side == (int)notice.Side && ReferenceEquals(row.Run, run))) return;
        // 后台不占用Dispatcher身份；只传本次实际零等待取得的门，原退场跳过这些已持连接门。
        var gates = new[] { party.Commands }.Concat(party.Members.Concat(instance.Members)
            .OrderBy(member => member.Actor.Owner.Account, StringComparer.Ordinal)
            .Select(member => member.Actor.Session.CommandGate)).Append(context.CommandGate).Distinct();
        var held = new List<SemaphoreSlim>();
        try
        {
            foreach (var gate in gates)
            {
                if (!gate.Wait(0)) return;
                held.Add(gate);
            }
            if (!current() || !ReferenceEquals(context.Session.CurrentParty?.Invoke(), party) ||
                !ReferenceEquals(context.Session.PartyDungeon, instance) ||
                !ReferenceEquals(context.Session.CaptureStrongway2019Run(instance), run)) return;
            // 不持终局投影的Gate调用：原Dungeon会再次BeforeDeparture/Terminal，允许原重试路径重入。
            await depart(context, held.Contains);
        }
        finally { for (int i = held.Count - 1; i >= 0; i--) held[i].Release(); }
    }

    bool RewardsReady(Strongway2019Match match)
    {
        if (match.Competition.Snapshot.Terminal is not { } terminal || !deliveries.TryGetValue(match, out var display)) return false;
        lock (display.Recipients)
            return Members(match).Where(row => terminal.WinnerSide == (int)row.Side && row.Member.Actor.IsCurrent() &&
                ReferenceEquals(row.Member.Actor.Session.CurrentStrongway2019Match, match)).All(row =>
                display.Recipients.TryGetValue(row.Member.Actor.Character.Id, out var delivery) && delivery.TerminalSent);
    }

    public bool IsReturnDue(Strongway2019Match match, int seconds)
    {
        if (match.Competition.Snapshot.Terminal is null || !deliveries.TryGetValue(match, out var display)) return false;
        lock (display.Recipients)
            return display.FirstTerminalTimestamp is { } started && Clock.GetElapsedTime(started, Clock.GetTimestamp()) >= TimeSpan.FromSeconds(seconds);
    }

    public async Task ReturnAsync(Strongway2019Match match)
    {
        await TerminalAsync(match);
        if (!RewardsReady(match)) return;
        // 逐个使用当前原队伍快照；先退者会更新Party，后续成员不能复用冻结的旧Commands。
        foreach (var row in Members(match))
            await (row.Member.Actor.Session.Strongway2019NoticeArrived?.Invoke(
                new(Strongway2019NoticeKind.Departure, match, row.Member, row.Side)) ?? Task.CompletedTask);
    }

    static async Task SendAsync(GameConnectionContext context, Func<Task> send)
    {
        if (context.Stop.IsCancellationRequested) return;
        try { await send(); }
        // 只隔离该连接的网络发送；奖励提交在此边界之外，存储异常必须保留原身份供重试。
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    static IEnumerable<(PartyMember Member, uint Side)> Members(Strongway2019Match match) =>
        match.Pair.First.Party.Members.Select(member => (member, 0u)).Concat(
            match.Pair.Second.Party.Members.Select(member => (member, 1u)));

    static Task PublishAsync(IEnumerable<Strongway2019Notice> notices) => Task.WhenAll(notices.Select(notice =>
        notice.Recipient.Actor.Session.Strongway2019NoticeArrived?.Invoke(notice) ?? Task.CompletedTask));

    public Task WaitingAsync(Strongway2019Match match, uint deadline) => PublishAsync(Members(match).Select(row =>
        new Strongway2019Notice(Strongway2019NoticeKind.Waiting, match, row.Member, row.Side, Value: deadline)));
    public Task SymbolAsync(Strongway2019Match match, uint side, uint kind, uint value) => PublishAsync(Members(match).Select(row =>
        new Strongway2019Notice(Strongway2019NoticeKind.Symbol, match, row.Member, side, kind, value)));
    public async Task LoadedAsync(IEnumerable<PartyDungeonNotice> notices)
    {
        foreach (var group in OriginalNotices(notices, PartyDungeonNoticeKind.Loaded)
            .Where(row => !row.Notice.Scene!.NavigationReplay && row.Member.Run.Phase == SinglePlayerDungeonPhase.Dungeon)
            .GroupBy(row => (row.Match, row.Member.Side)))
        {
            var rows = group.ToArray();
            if (!CompleteOriginalTeam(rows)) continue;
            var scene = rows[0].Notice.Scene!;
            var maps = rows[0].Member.Run.Entry.Maze.MapSpecification;
            if (maps is null) continue;
            int ordinal = -1;
            for (int i = 0; i < maps.Count; i++)
                if (maps[i].Id == scene.Snapshot.CurrentMap.Id && maps[i].Position == scene.Snapshot.CurrentMap.Position &&
                    maps[i].Kind == scene.Snapshot.CurrentMap.Kind) { ordinal = i; break; }
            if (ordinal < 0) continue;
            // current是map-spec行索引；DungeonMaze.SourceOrdinal选择整个迷宫，不能用作路线进度。
            await SymbolAsync(group.Key.Match, checked((uint)group.Key.Side), 5, checked((uint)ordinal));
            if (activity is not null && activity.MapMonsters.TryGetValue(scene.Snapshot.CurrentMap.Id, out int target) &&
                rows.All(row => row.Notice.Scene!.Monsters.Monsters.Any(monster => monster.MonsterId == target &&
                    row.Notice.Scene.Snapshot.LivingMonsters.Contains(monster.ObjectKey))))
                await SymbolAsync(group.Key.Match, checked((uint)group.Key.Side), 6, 100);
        }
    }

    public async Task DeathAsync(IEnumerable<PartyDungeonNotice> notices)
    {
        if (activity is null) return;
        foreach (var group in OriginalNotices(notices, PartyDungeonNoticeKind.Death)
            .Where(row => row.Notice.Death is { Outcome: DungeonRunOutcome.Accepted, ReportedTarget: not null })
            .GroupBy(row => (row.Match, row.Member.Side)))
        {
            var rows = group.ToArray();
            if (!CompleteOriginalTeam(rows)) continue;
            var scene = rows[0].Notice.Scene!;
            if (!activity.MapMonsters.TryGetValue(scene.Snapshot.CurrentMap.Id, out int target)) continue;
            ushort key = rows[0].Notice.Death!.MonsterKey;
            if (rows.All(row => row.Notice.Death is { ReportedTarget: { } monster } death &&
                death.MonsterKey == key && monster.ObjectKey == key && monster.MonsterId == target &&
                row.Member.Run.CurrentMonsterPlan.Monsters.Contains(monster) &&
                !row.Notice.Scene!.Snapshot.LivingMonsters.Contains(key)))
                // 来自原共享死亡事务的已提交通知；客户端kind6=0不具有此权限。
                await SymbolAsync(group.Key.Match, checked((uint)group.Key.Side), 6, 0);
        }
    }

    readonly record struct OriginalNotice(PartyDungeonNotice Notice, Strongway2019Match Match, Strongway2019Participant Member);

    static IEnumerable<OriginalNotice> OriginalNotices(IEnumerable<PartyDungeonNotice> notices, PartyDungeonNoticeKind kind)
    {
        foreach (var notice in notices)
        {
            var session = notice.Recipient.Actor.Session;
            if (notice.Kind != kind || notice.Scene is not { Entry.DungeonId: 5360 } scene ||
                !notice.Recipient.Actor.IsCurrent() || !ReferenceEquals(session.Selected, scene.Character) ||
                session.CurrentStrongway2019Match is not { } match || session.PartyDungeon is not { } instance ||
                session.CaptureStrongway2019Run(instance) is not { } run || run.Scene.Identity != scene.Snapshot.Identity ||
                !ReferenceEquals(instance, match.First) && !ReferenceEquals(instance, match.Second)) continue;
            var member = match.Competition.Participants.SingleOrDefault(row => ReferenceEquals(row.Run, run));
            if (member is null || member.Identity.CharacterId != run.Scene.Identity.CharacterId ||
                member.Identity.InstanceId != run.Scene.Identity.InstanceId || member.Identity.TaskAttempt != run.Scene.Identity.TaskAttempt ||
                member.Identity.QuestId != run.Scene.Identity.QuestId ||
                !(member.Side == 0 ? match.Pair.First.Party : match.Pair.Second.Party).Members.Contains(notice.Recipient)) continue;
            yield return new(notice, match, member);
        }
    }

    static bool CompleteOriginalTeam(OriginalNotice[] rows)
    {
        if (rows.Length == 0) return false;
        var first = rows[0]; var scene = first.Notice.Scene!;
        var party = first.Member.Side == 0 ? first.Match.Pair.First.Party : first.Match.Pair.Second.Party;
        return Strongway2019EntryPolicy.AllowsMemberCount(party.Members.Length) && rows.Length == party.Members.Length &&
            party.Members.All(member => rows.Count(row => ReferenceEquals(row.Notice.Recipient.Actor.Session, member.Actor.Session)) == 1) &&
            rows.All(row => row.Notice.Scene!.Snapshot.CurrentMap.Id == scene.Snapshot.CurrentMap.Id &&
                row.Notice.Scene.Snapshot.CurrentMap.Position == scene.Snapshot.CurrentMap.Position);
    }
    public async Task TerminalAsync(Strongway2019Match match)
    {
        if (match.Competition.Snapshot.Terminal is null) return;
        if (rewards is not null && (!rewards.FreezeMatch(match) || !await rewards.RecoverReadyRecipientsAsync(match))) return;
        await PublishAsync(Members(match).Select(row => new Strongway2019Notice(Strongway2019NoticeKind.Terminal, match, row.Member, row.Side)));
    }
}
