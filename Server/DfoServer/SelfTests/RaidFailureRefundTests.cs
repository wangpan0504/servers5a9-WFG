using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Channels;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class RaidFailureRefundTests
{
    // 真实开战扣费、原时钟及SQLite；漩涡的团队/引导和主动退出由既有TCP覆盖。
    internal static async Task RunAsync(string root, TcpTestResources resources)
    {
        int before = Count;
        var items = resources.ItemResources;
        var catalog = new RaidStartSettings { StartMinimumOverride = 1 }.Apply(RaidCatalog.Load(resources.Archive))
            .PrepareResources(resources.Archive, SelfTestCatalogs.Dungeons(resources.Archive), items.Items,
                SelfTestCatalogs.Rooms(resources.Archive));
        foreach (var kind in new[] { RaidKind.Anton, RaidKind.Luke, RaidKind.Prey })
        {
            var definition = catalog.Find((byte)kind)!;
            LukeRaidStartPolicy? lukePolicy = null;
            if (kind == RaidKind.Luke)
            {
                lukePolicy = LukeProductionPolicy.Create(definition.Resources!, items.Items, items.Boosters);
                var entry = definition.Resources!.EntryCatalog!;
                var rooms = SelfTestCatalogs.Rooms(resources.Archive);
                definition = definition with { Resources = definition.Resources with
                {
                    LukeObjects = LukeRaidObjectCatalog.Load(resources.Archive, entry, rooms, lukePolicy.Run!.ObjectChoices(entry)),
                    LukeCardSet = LukeCardSetCatalog.Load(resources.Archive, entry, rooms)
                } };
                lukePolicy = LukeSanctumRoute.Resolve(lukePolicy with { Cards = definition.Resources.LukeCardSet }, definition)!;
            }
            var clock = new LukeRaidClockTests.Clock();
            string path = Path.Combine(root, "refund-" + kind, "game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("refund-" + kind, () => { });
            var registry = new RaidRegistry(clock);
            var parties = new PartyService(repository);
            var character = repository.Create(owner, CharacterCreation.Prepare("Refund" + kind, resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get) with
                { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [12829, 3779, 2903, 4872, 8935] });
            int ticket = (kind == RaidKind.Prey ? definition.Resources!.PreyTeam!.Script : definition.Resources!.Script).EntryCost!.ItemId;
            InventoryService.Commit(repository, owner, character, InventoryRewardGrantService.TryGrantBatch(character,
                [new(ticket, 3)], items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow()));
            var channel = kind switch
            {
                RaidKind.Anton => new ChannelEntry(new(2, 19), 23, "[raid]"),
                RaidKind.Luke => new ChannelEntry(new(2, 20), 32, "[raid]"),
                _ => new ChannelEntry(new(2, 42), 50, "[prey]")
            };
            var session = new GameSession(repository, resources.Towns, owner, entry: channel, timeProvider: clock);
            try
            {
                session.CurrentParty = () => parties.Current(session);
                session.Select(0); session.MarkReady();
                var actor = session.CaptureRaidActor(registry)!;
                registry.Create(actor, (byte)kind, "Refund", "Refund"u8.ToArray(), definition.MemberMax, 1);
                if (kind == RaidKind.Prey) registry.AssignPrey(actor, actor.Character, 11);
                else registry.Assign(actor, actor.Character, 1);
                var store = new Store(repository);
                await using var timers = new LukeRaidClocks(clock, store, (_, _) => { });
                var started = kind == RaidKind.Luke
                    ? new LukeRaidStart(registry, parties, repository, resources.Towns, items.EpicPieces,
                        lukePolicy!, clock, rewardItems: items.Items, timers: timers)
                        .Start(actor, registry.Find(actor)!, definition, [], () => true)
                    : new AntonRaidStart(registry, parties, repository, resources.Towns, items.EpicPieces, clock,
                        phaseProgress: true, rewards: kind == RaidKind.Anton ? new(definition.Resources.Script, items.Items, items.Boosters) : null,
                        timers: timers, preyTeamRewards: kind == RaidKind.Prey ? new(definition.Resources.PreyTeam!.Script, items.Items, items.Boosters) : null)
                        .Start(actor, registry.Find(actor)!, definition);
                That(started.Receipt is not null, "退票回归正式开战：" + kind + ":" + started.Reason);
                var battle = session.CurrentRaidBattle(actor)!;
                That(Scalar("SELECT count(*) FROM mailbox_messages") == 0 && battle.PendingRaidFailureRefund() is null,
                    "正常开战不退票：" + kind);
                Sql("CREATE TRIGGER refund_rollback BEFORE UPDATE OF state ON character_raid_runs WHEN NEW.state=1 BEGIN SELECT RAISE(ABORT,'refund rollback'); END");
                int seconds = kind switch
                {
                    RaidKind.Luke => LukeRaidPhaseTimeoutPolicy.Shared95.Seconds[0],
                    RaidKind.Prey => definition.Resources.PreyTeam!.Script.Headers.Single(row => row.Tag == "[phase total time over]").Values.Single().Number,
                    _ => definition.Resources.Script.Headers.Single(row => row.Tag == "[phase time over]").Values[0].Number
                };
                clock.Advance(seconds);
                That(Scalar("SELECT count(*) FROM mailbox_messages") == 0, "失败事务异常不留下半份退票邮件：" + kind);
                Sql("DROP TRIGGER refund_rollback");
                clock.Advance(30); clock.FireQueued();
                That(battle.View.PhaseExpired && !battle.View.ClockFaulted && Scalar("SELECT count(*) FROM mailbox_messages") == 1,
                    "失败事务恢复后退实际门票一次：" + kind);
                var refund = store.Candidate!;
                That(refund.Kind == kind && refund.Members.Single().Items.All(item => item.Core.ItemId == ticket) &&
                    refund.Members.Single().Items.Sum(item => item.Core.Count) == started.Receipt!.Cost.Count,
                    "退票保留本次实际支付票种及数量：" + kind);
                repository.CommitRaidFailureRefund(refund); repository.CommitRaidFailureRefund(refund);
                clock.Advance(30); clock.FireQueued();
                That(Scalar("SELECT count(*) FROM mailbox_messages") == 1 && Scalar("SELECT state FROM character_raid_runs") == 1 &&
                    Scalar("SELECT count(*) FROM character_raid_runs WHERE clear_week IS NOT NULL") == 0,
                    "重复提交及旧时钟不重复退票、不伪造通关：" + kind);

                long Scalar(string sql)
                {
                    using var db = new SqliteConnection("Data Source=" + path); db.Open();
                    using var command = db.CreateCommand(); command.CommandText = sql;
                    return Convert.ToInt64(command.ExecuteScalar());
                }
                void Sql(string sql)
                {
                    using var db = new SqliteConnection("Data Source=" + path); db.Open();
                    using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
                }
            }
            finally { session.End(); }
        }
        Console.WriteLine($"PASS {Count - before} 全团本原票失败返还、SQLite回滚与幂等断言");
    }

    sealed class Store(CharacterRepository repository) : ILukeRaidLifecycleStore, IAntonRaidLifecycleStore, IRaidFailureRefundStore
    {
        internal AntonRaidBattle.RaidFailureRefund? Candidate;
        public bool IsRetryable(Exception error) => error is SqliteException sqlite && sqlite.Message.Contains("refund rollback", StringComparison.Ordinal) ||
            ((ILukeRaidLifecycleStore)repository).IsRetryable(error);
        public LukePhaseCommit CommitLukePhase(AntonRaidBattle.LukePhaseSettlement candidate) => repository.CommitLukePhase(candidate);
        public System.Collections.Immutable.ImmutableArray<RaidPhaseEligibility> CommitAntonPhase(AntonRaidBattle.AntonPhaseSettlement candidate) => repository.CommitAntonPhase(candidate);
        public void CommitRaidFailureRefund(AntonRaidBattle.RaidFailureRefund candidate)
        { Candidate = candidate; repository.CommitRaidFailureRefund(candidate); }
    }
}
