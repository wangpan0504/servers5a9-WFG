using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PreyTeamRejoinTests
{
    internal static void Run(string root, TcpTestResources resources)
    {
        int before = Count;
        var definition = RaidCatalog.Load(resources.Archive).Find(3)!;
        var loaded = RaidResourceLoader.Load(resources.Archive, definition,
            SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items);
        var team = loaded.PreyTeam!;
        That(team is { ResourcesReady: true, EntryCatalog.Ready: true }, "重返用例使用可执行的原普通资源");
        // 仅缩短合成用例的资源窗口，证明实现读取声明而非固定五分钟；实际PVF保持原值。
        var script = team.Script with
        {
            Headers = team.Script.Headers.Select(row => row.Tag == "[disconn member wait time]"
                ? row with { Values = [new(0, 7, null)] } : row).ToImmutableArray()
        };
        int missingMap = team.Dungeons.SelectMany(row => row.MapIds).First();
        var partial = team with { Script = script, MissingFormalMaps = [missingMap],
            EntryCatalog = new(script, ImmutableDictionary<int, DfoServer.GameWorld.Dungeons.DungeonDefinition>.Empty,
                [missingMap], new Dictionary<int, string>(), _ => false, 3) };
        definition = definition with { Resources = loaded with { PreyTeam = partial } };
        That(partial.PreyTeamStartReady && !partial.EntryCatalog!.Ready && partial.EntryCatalog.Find(script.Phases[0].Dungeons[0].Id) is null,
            "显式缺图夹具保留诊断与逐图拒绝，但普通开团基础条件可用，不修改原PVF");
        var clock = new LukeRaidClockTests.Clock(DateTimeOffset.UtcNow);
        string databasePath = Path.Combine(root, "prey-team-rejoin", "game.db");
        var repository = new CharacterRepository(databasePath);
        var owners = new AccountOwners();
        var records = new AntonRaidRejoin(false, lukeCandidatesEnabled: true, clock: clock);
        var raids = new RaidRegistry(clock);
        raids.BindRejoin(records);
        var parties = new PartyService(repository);
        var recovery = new LukeRaidRejoin(raids, parties, records);
        var rewards = new PreyGuideRewards(script, resources.ItemResources.Items, resources.ItemResources.Boosters);
        var start = new AntonRaidStart(raids, parties, repository, resources.Towns, resources.ItemResources.EpicPieces,
            clock, preyTeamRewards: rewards);
        var leases = new List<AccountOwners.Lease>();
        var sessions = new List<GameSession>();
        RaidActor Login(int index, bool create)
        {
            var owner = owners.Acquire("prey-team-rejoin-" + index, static () => { });
            leases.Add(owner);
            if (create)
            {
                var point = new Location(59, 0, 445, 377);
                var character = repository.Create(owner, new("PreyRej" + index, point, point)
                {
                    Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [8935],
                    Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0))
                });
                var itemResources = resources.ItemResources;
                var inventory = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
                InventoryService.Commit(repository, owner, character, inventory.Grant(character, [new(script.EntryCost!.ItemId, 2)]));
            }
            var session = new GameSession(repository, resources.Towns, owner,
                entry: new(new(2, 42), 50, "[prey]"), timeProvider: clock);
            sessions.Add(session);
            session.CurrentParty = () => parties.Current(session);
            session.Select(0);
            session.MarkReady();
            return session.CaptureRaidActor(raids)!;
        }
        LukeRaidRejoinResult Restore(RaidActor actor, uint key)
        {
            actor.Session.CommandGate.Wait();
            try { return recovery.Restore(actor, key, callerCommandHeld: true); }
            finally { actor.Session.CommandGate.Release(); }
        }
        AntonRaidDisconnectFact Disconnect(RaidActor actor)
        {
            var fact = recovery.TryCaptureForDisconnect(actor.Session, out var reason);
            That(fact is not null && reason == "captured", "断线前按本人当前owner和原聚合冻结原身份");
            actor.Session.End();
            parties.Detach(actor.Session);
            actor.Owner.Dispose();
            That(recovery.TryRecordDisconnected(fact!, out reason) && reason == "recorded", "原owner释放后登记同一预约");
            return fact!;
        }
        async Task StartPartialAsync(RaidActor actor)
        {
            using var stream = new MemoryStream();
            using var stop = new CancellationTokenSource();
            using var log = new ServerLog(Path.Combine(root, "prey-team-rejoin", "logs"));
            byte[]? acknowledged = null;
            await using var sender = new GameConnectionSender(actor.Owner, stream, stop, 0, (kind, opcode, _, body, _) =>
            { if (kind == 1 && opcode == 666) acknowledged = body.ToArray(); });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), actor.Owner, actor.Session, sender);
            var commands = new GameCommandRegistry();
            new RaidHandler(log, new RaidCatalog(ImmutableDictionary<byte, RaidDefinition>.Empty.Add(3, definition)), raids,
                start: start, parties: parties).Register(commands);
            commands.Seal();
            That(await commands.DispatchAsync(context, new Packet(1, 666, [])) == CommandResult.Handled,
                "缺图普通开团沿原666分派，使用内存传输及真实SQLite，不冒称socket或客户端验收");
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Bytes(acknowledged ?? [], [1], "缺图不再被请求层全目录门禁挡住，实际写出成功ACK666");
        }
        try
        {
            var actors = Enumerable.Range(0, 12).Select(index => Login(index, true)).ToArray();
            var leader = actors[0];
            That(raids.Create(leader, 3, "PreyRejoin", "PreyRejoin"u8.ToArray(), 12, 1) is not null, "真实目录创建十二人普通团");
            foreach (var actor in actors.Skip(1))
            {
                raids.Invite(leader, actor, 0);
                That(raids.Respond(actor, leader.Character, true).Accepted, "原目录邀请加入普通团");
            }
            for (int index = 0; index < actors.Length; index++)
                That(raids.AssignPrey(leader, actors[index].Character, checked((byte)((index / 4 + 1) * 10 + index % 4 + 1))) is not null,
                    "三队四人沿原生座次编队");
            StartPartialAsync(leader).GetAwaiter().GetResult();
            var battle = leader.Session.CurrentRaidBattle(leader)!;
            That(actors.All(actor => ReferenceEquals(actor.Session.CurrentRaidBattle(actor), battle) &&
                actor.Session.Selected!.Inventory.Items.Where(item => item.Core.ItemId == script.EntryCost!.ItemId).Sum(item => item.Core.Count) == 1),
                "缺图普通开团仍为十二名成员分别扣原门票并绑定同一真实Battle");
            using (var db = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False"))
            {
                db.Open(); using var count = db.CreateCommand();
                count.CommandText = "SELECT count(*) FROM character_raid_runs WHERE raid_type=3 AND instance_id=$instance";
                count.Parameters.AddWithValue("$instance", battle.Instance.ToString("N"));
                That(Convert.ToInt32(count.ExecuteScalar()) == 12, "十二份费用与同实例生命周期已提交，非仅提前成功回复");
            }
            PreyTeamPhaseReturnTests.RunMembers(script, actors, rewards.CreateTeam(false, false, _ => 0));
            var original = raids.Find(leader)!;
            That(actors.All(actor => parties.Current(actor.Session) is null), "开团保留全局编队标记，不据此建立实际Party");
            That(battle is { Kind: RaidKind.Prey, PreyGuide: false, SupportsLukeRejoin: true } && battle.LukeRejoinWindow == TimeSpan.FromSeconds(7),
                "普通重返使用当前资源窗口且战场模式明确");
            Reject<InvalidOperationException>(() => AccountOwners.Lease.TryExecuteLukeDisconnect(
                actors.Select(actor => (actor.Owner, actor.Character)).ToArray(), static () => true, out _),
                "普通普雷十二人支持不扩大原卢克八人入口");
            string originalInventory = JsonSerializer.Serialize(repository.LoadCharacter(actors[11].Owner, 0)!.Inventory);
            AntonRaidDisconnectFact fact;
            using (var held = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
            {
                var blocker = Task.Run(() => actors[1].Owner.Execute(() =>
                { held.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("普雷队友owner占用夹具超时"); return true; }));
                Task<AntonRaidDisconnectFact>? cleanup = null;
                try
                {
                    That(held.Wait(TimeSpan.FromSeconds(5)), "真实队友线程已持owner，通过同步信号确认占用");
                    cleanup = Task.Run(() => Disconnect(actors[11]));
                    That(cleanup.Wait(TimeSpan.FromSeconds(5)) && !release.IsSet && !actors[11].Owner.IsCurrent,
                        "队友owner未释放时仍完成捕获、本人Session清理及原预约登记");
                    fact = cleanup.GetAwaiter().GetResult();
                }
                finally
                {
                    release.Set();
                    Task.WhenAll(new Task?[] { blocker, cleanup }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
            }
            That(fact.Slot == 11 && fact.Squad == 3 && fact.Luke is { PreyPosition: 34, Window.TotalSeconds: 7 },
                "最高成员槽和第三队第四座精确冻结");
            var fresh = Login(11, false);
            string saved = JsonSerializer.Serialize(repository.LoadCharacter(fresh.Owner, 0));
            var selected = fresh.Session.Selected;
            That(recovery.CanNotice(fresh) && !Restore(fresh, original.Key + 1).Accepted &&
                !Restore(fresh with { Channel = new(2, 43) }, original.Key).Accepted,
                "仅原key及原频道可恢复，原生通知资格由同一候选提供");
            var restored = Restore(fresh, original.Key);
            That(restored.Accepted && restored.Raid!.Members.Single(member => member.Actor == fresh) is
                { Slot: 11, Squad: 3, PreyPosition: 34 } && restored.Party is null && parties.Current(fresh.Session) is null,
                "无实际Party也可重返同团并保留原编队标记和座次，不自动创建队伍");
            That(ReferenceEquals(fresh.Session.CurrentRaidBattle(fresh), battle) &&
                ReferenceEquals(fresh.Session.Selected, selected) && JsonSerializer.Serialize(repository.LoadCharacter(fresh.Owner, 0)) == saved,
                "恢复同一Battle，不写票、角色资产或新实例");
            That(JsonSerializer.Serialize(repository.LoadCharacter(fresh.Owner, 0)!.Inventory) == originalInventory,
                "队友owner忙时的断线、重登及重返仍保持开团后的原门票和完整库存");
            That(!records.HasLukeParty(original.Key), "已绑定普通成员不再持离线预约，主动离团不能永久保留空团");
            long revision = battle.View.Revision;
            That(Restore(fresh, original.Key).Reason == "already-bound" && battle.View.Revision == revision,
                "重复重返不更改战况或重新扣费");
            raids.RemoveSession(actors[11].Session, actors[11].Epoch);
            That(raids.Find(fresh)?.Members.Length == 12, "旧连接迟到收尾不能移除重返后的新成员");
            actors[11] = fresh;
            foreach (var actor in actors.Reverse()) Disconnect(actor);
            That(raids.Find(original.Key, original.Channel) is { Members.IsEmpty: true }, "全团离线保留原普通团和Battle");
            clock.Advance(6);
            var returning = Login(0, false);
            That(Restore(returning, original.Key).Accepted && ReferenceEquals(returning.Session.CurrentRaidBattle(returning), battle),
                "资源窗口截止前可从空团恢复同一实例");
            clock.Advance(1);
            That(records.Peek(returning.Character) is { LukeDeadlineAnchor: null } && Restore(returning, original.Key).Reason == "already-bound" &&
                records.Peek(actors[1].Character) is null,
                "原截止到达时已绑定成员保留，其余过期候选被裁剪");
            var final = Disconnect(returning);
            clock.Advance(7);
            That(records.Peek(final.Character) is null && raids.Find(original.Key, original.Channel) is null,
                "再次断线按新窗口计时，截止后空团不能借旧key复活");
            var guide = new AntonRaidBattle(loaded.Script, kind: RaidKind.Prey);
            That(!guide.SupportsLukeRejoin, "普通重返能力不扩展现有单人引导");
            // 复用已释放的六个原角色及剩余门票；独立新团不改变上方重返窗口和旧实例断言。
            var waitingActors = Enumerable.Range(0, 6).Select(index => Login(index, false)).ToArray();
            var waitingLeader = waitingActors[0];
            That(raids.Create(waitingLeader, 3, "PreyWaiting", "PreyWaiting"u8.ToArray(), 12, 1) is not null,
                "终局等待区回归复用原SQLite角色创建独立普通团");
            foreach (var actor in waitingActors.Skip(1))
            {
                raids.Invite(waitingLeader, actor, 0);
                That(raids.Respond(actor, waitingLeader.Character, true).Accepted, "终局等待区回归登记原角色");
            }
            for (int index = 0; index < waitingActors.Length; index++)
                That(raids.AssignPrey(waitingLeader, waitingActors[index].Character,
                    checked((byte)((index / 4 + 1) * 10 + index % 4 + 1))) is not null, "终局等待区回归冻结合法整局标记");
            StartPartialAsync(waitingLeader).GetAwaiter().GetResult();
            PreyTeamPhaseReturnTests.RunWaiting(script, waitingActors, waitingLeader.Session.CurrentRaidBattle(waitingLeader)!,
                start, raids, parties, repository, resources.Towns, databasePath);
            Console.WriteLine($"PASS {Count - before} 普雷普通重返领域与真实开团事务断言");
        }
        finally
        {
            foreach (var session in sessions) session.End();
            foreach (var lease in leases) lease.Dispose();
        }
    }
}
