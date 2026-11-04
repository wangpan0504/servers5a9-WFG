using System.Collections.Immutable;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 与TCP共用同一资源；用正式666/阶段SQL和真实发送泵覆盖失败结果的写前、写后边界。
internal static class AntonFailureResultTests
{
    internal static void Run(TcpTestResources resources) => RunAsync(resources).GetAwaiter().GetResult();

    static async Task RunAsync(TcpTestResources resources)
    {
        int checks = Count;
        var items = resources.ItemResources;
        var definition = new RaidStartSettings { StartMinimumOverride = 1 }.Apply(RaidCatalog.Load(resources.Archive))
            .PrepareResources(resources.Archive, SelfTestCatalogs.Dungeons(resources.Archive), items.Items).Find(0)!;
        foreach (string scenario in new[] { "written", "old-epoch", "write-failed" })
        {
            string root = Path.Combine(Path.GetTempPath(), "s5a9-anton-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using var owner = new AccountOwners().Acquire("anton-failure-" + scenario, static () => { });
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var clock = new LukeRaidClockTests.Clock();
            var registry = new RaidRegistry(clock);
            var parties = new PartyService(repository);
            var character = repository.Create(owner, CharacterCreation.Prepare("Fail601", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get) with
                { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [12829, 3779] });
            character = InventoryService.Commit(repository, owner, character, InventoryRewardGrantService.TryGrantBatch(character,
                [new(definition.Resources!.Script.EntryCost!.ItemId, 2)], items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow()));
            var session = new GameSession(repository, resources.Towns, owner, entry: new(new(2, 19), 23, "[raid]"), timeProvider: clock);
            try
            {
                session.CurrentParty = () => parties.Current(session);
                session.Select(character.Slot); session.MarkReady();
                var actor = session.CaptureRaidActor(registry)!;
                registry.Create(actor, 0, "Fail601", "Fail601"u8.ToArray(), 20, 1);
                registry.Assign(actor, actor.Character, 1);
                var store = new Store(repository);
                await using var clocks = new LukeRaidClocks(clock, store, static (_, _) => { });
                var start = new AntonRaidStart(registry, parties, repository, resources.Towns, items.EpicPieces, clock,
                    phaseProgress: true, rewards: new(definition.Resources.Script, items.Items, items.Boosters), timers: clocks);
                That(start.Start(actor, registry.Find(actor)!, definition).Error == 0,
                    "失败601夹具通过真实666扣票并绑定原生命周期：" + scenario);
                var battle = session.CurrentRaidBattle(actor)!;
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var stream = new GameSenderBudgetTests.HeldStream { FailWrite = scenario == "write-failed" };
                var frames = new List<(ushort Opcode, byte[] Body)>();
                await using var sender = new GameConnectionSender(owner, stream, stop, 0,
                    (_, opcode, _, body, _) => frames.Add((opcode, body.ToArray())));
                var context = new GameConnectionContext(new(83, IPAddress.Loopback, stream, stop), owner, session, sender);
                Task? held = scenario == "write-failed" ? null : sender.Send(1, 1347, [1], "anton_failure_held");
                if (held is not null) await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                clock.Advance(2400);
                That(battle.PendingAntonSettlement() is { TimedOut: true } && !battle.View.PhaseExpired &&
                    session.AntonFailureResult(actor, battle.Instance, 0) is null &&
                    !context.AntonFailureResult.Post(context, actor, battle.View),
                    "阶段SQL忙时不发失败601、不把候选当已提交失败：" + scenario);
                clock.Advance(1);
                var view = battle.View;
                var result = session.AntonFailureResult(actor, battle.Instance, 0);
                That(view.PhaseExpired && result is { Result: 1, Phase: 0, Seconds: 2400, RawValue: 0, Rank: 2, RawFlag: 0 } &&
                    Scalar("SELECT count(*) FROM character_raid_runs WHERE state=1 AND json_extract(phase0_receipt,'$.Reason')='phase-timeout'") == 1,
                    "失败601只读取原2400秒封存统计和实际已提交不合格阶段回执：" + scenario);
                That(!context.AntonFailureResult.Post(context, actor, view) &&
                    !context.AntonFailureResult.Post(context, actor, view), "重复排队不能提前标记601已写出：" + scenario);
                if (held is null) await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (scenario == "old-epoch") session.ReturnToRoster();
                stream.Release.TrySetResult();
                if (scenario == "write-failed")
                {
                    bool rejected = false;
                    try { await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                    catch (Exception error) when (error is IOException or OperationCanceledException) { rejected = true; }
                    That(rejected && frames.Count == 0 && !context.AntonFailureResult.Post(context, actor, view) &&
                        stop.IsCancellationRequested, "601真实写失败不标written、不启动退场并关闭原发送链");
                }
                else
                {
                    await Task.WhenAll(held!, sender.DrainAsync()).WaitAsync(TimeSpan.FromSeconds(3));
                    if (scenario == "written")
                    {
                        That(frames.Count == 2 && frames[0].Opcode == 1347 && frames[1].Opcode == 601,
                            "慢写恢复后重复Notice只实际写一次601");
                        Bytes(frames[1].Body, [1, 0, 0x60, 9, 0, 0, 0, 0, 2, 0],
                            "失败601独立精确10B：A1、原phase/2400秒/0死亡/rank2、F0");
                        That(context.AntonFailureResult.Post(context, actor, view), "601实际写出后才允许原退场执行器继续");
                        await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
                        That(frames.Count == 2, "重复失败通知不再播放601");
                    }
                    else That(frames.Count == 1 && frames[0].Opcode == 1347,
                        "换选角epoch后旧失败601静默丢弃，不发布到新角色或清新团");
                }
                That(sender.PendingBytes == 0 && store.Attempts == 2 &&
                    Scalar("SELECT count(*) FROM character_raid_reward_receipts") == 0,
                    "发送成功、旧代或失败均释放预算且不重跑阶段SQL、退款或发奖励：" + scenario);

                long Scalar(string sql)
                {
                    using var db = new SqliteGameDatabase(Path.Combine(root, "game.db")).Open();
                    using var command = db.CreateCommand(); command.CommandText = sql;
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            }
            finally { session.End(); TestFiles.DeleteDirectory(root, true); }
        }
        Console.WriteLine($"PASS {Count - checks} 安徒恩失败601真实阶段SQL、慢写、旧epoch与写失败断言");
    }

    sealed class Busy : Exception;
    sealed class Store(CharacterRepository repository) : ILukeRaidLifecycleStore, IAntonRaidLifecycleStore
    {
        internal int Attempts;
        public bool IsRetryable(Exception error) => error is Busy || ((IAntonRaidLifecycleStore)repository).IsRetryable(error);
        public LukePhaseCommit CommitLukePhase(AntonRaidBattle.LukePhaseSettlement candidate) => throw new InvalidOperationException("不是卢克夹具");
        public ImmutableArray<RaidPhaseEligibility> CommitAntonPhase(AntonRaidBattle.AntonPhaseSettlement candidate)
        {
            if (++Attempts == 1) throw new Busy();
            return repository.CommitAntonPhase(candidate);
        }
    }
}
