using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 合成城镇和真实 SQLite。不加载 PVF。覆盖引导终局回候战的失败、重试、660 重放和断线放弃。
internal static class PreyGuideWaitingReturnTests
{
    public static async Task RunCoreAsync()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-prey-guide-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool passed = false;
        var sessions = new List<GameSession>();
        var owners = new List<AccountOwners.Lease>();
        var senders = new List<GameConnectionSender>();
        var stops = new List<CancellationTokenSource>();
        try
        {
            var door = new Location(59, 0, 15, 15);
            var map = new TownMap("synthetic/prey-guide.map", [new(new(0, 0, 30, 30), [], [])], [], false, []);
            var room = map with { Path = "synthetic/prey-door.map", IsRoom = true };
            var towns = new TownCatalog(new()
            {
                [(59, 0)] = new(59, 0, 1, [], [], [], door, room, [], []),
                [(59, 2)] = new(59, 2, 1, [], [], [], null, map, [], []),
                [(59, 3)] = new(59, 3, 1, [], [], [], null, map, [], [])
            }, door, door, new(0, [], []), [], new Dictionary<uint, uint[]>());
            string database = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(database);
            var ownership = new AccountOwners();
            var pieces = new EpicPieceCatalog("[piece list]\n9001 9002\n[/piece list]");
            var stats = new CharacterStatComputer(new CharacterCatalog([]));
            var town = new TownProjection(towns, stats, null, pieces);
            var script = new RaidScript("synthetic/prey-guide-home.etc", new(59, 2), null, null,
                // ID 0 是 0237F210 清表哨兵。快照拒绝 Id<=0，这里用与团本夹具相同的正 ID。
                ImmutableDictionary<string, int>.Empty.Add("[prepare raid]", 10),
                [new("phase0", [], [], []), new("phase1", [], [], [])],
                [new("[start delay time]", 0, [new RaidValue(0, 1, null)]), new("[movie end time]", 0, [new RaidValue(0, 1, null)]),
                    new("[max hp]", 0, [new RaidValue(0, 100, null)])], []);
            ImmutableArray<RaidPhaseRewardPolicy> policies =
            [
                new(PreyGuideRewards.Source, [new(0, 0, [new InventoryReward(1, 1)], [])], new(0, 0, 1, 0, 0, 0)),
                new(PreyGuideRewards.Source, [new(0, 0, [new InventoryReward(1, 1)], [])], new(0, 1, 1, 0, 0, 0))
            ];

            void Sql(string text, params (string Name, object Value)[] args)
            {
                using var db = new SqliteConnection($"Data Source={database};Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = text;
                foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value);
                command.ExecuteNonQuery();
            }
            long Scalar(string text, params (string Name, object Value)[] args)
            {
                using var db = new SqliteConnection($"Data Source={database};Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = text;
                foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value);
                return Convert.ToInt64(command.ExecuteScalar());
            }
            bool At(ushort id, int town, int area) =>
                Scalar("SELECT town FROM characters WHERE id=$id", ("$id", id)) == town &&
                Scalar("SELECT area FROM characters WHERE id=$id", ("$id", id)) == area;
            long Receipts(ushort id) => Scalar(
                "SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$id AND raid_type=3", ("$id", id));
            void ArmFailure(ushort id) => Sql(
                $"CREATE TRIGGER fail_guide_home_{id} BEFORE UPDATE ON characters WHEN NEW.id={id} AND NEW.area=2 BEGIN SELECT RAISE(ABORT,'guide-home'); END;");
            void ClearFailure(ushort id) => Sql($"DROP TRIGGER IF EXISTS fail_guide_home_{id};");

            async Task<Seat> Open(string account, string name)
            {
                var lease = ownership.Acquire(account, () => { });
                owners.Add(lease);
                repository.Create(lease, new(name, door, door) { Level = 95 });
                var session = new GameSession(repository, towns, lease, entry: new(new(2, 42), 50, "[prey]"));
                sessions.Add(session);
                session.Select(0);
                session.MarkReady();
                var bird = towns.Spawn(new(59, 3, 0, 0), null)!;
                var parked = repository.Save(lease, session.Selected!, bird, session.Selected!.Return);
                typeof(GameSession).GetProperty("Selected")!.SetValue(session, parked);
                var actor = session.CaptureRaidActor(new RaidRegistry())!;
                var battle = new AntonRaidBattle(script, kind: RaidKind.Prey, preyGuide: true);
                That(session.AttachRaidCombat(actor, battle, policies), "引导夹具绑定两阶段战场");
                var stop = new CancellationTokenSource();
                stops.Add(stop);
                var stream = new MemoryStream();
                var frames = new List<(ushort Opcode, byte[] Body)>();
                var sender = new GameConnectionSender(lease, stream, stop, 0,
                    (_, opcode, _, body, _) => frames.Add((opcode, body.ToArray())));
                senders.Add(sender);
                var context = new GameConnectionContext(new(1222, IPAddress.Loopback, stream, stop), lease, session, sender);
                var faults = new List<string>();
                context.PreyGuideWaiting.Bind(stop.Token);
                context.PreyGuideWaiting.Diagnostic = detail => { lock (faults) faults.Add(detail); };
                session.PreyGuideWaitingReleased = context.PreyGuideWaiting.Cancel;
                return new(session, actor, battle, context, frames, faults);
            }
            void ShowFinal(Seat seat)
            {
                var next = seat.Battle.View with
                {
                    Phase = 1, State = AntonRaidBattleState.Reward, PhaseExpired = false, ClockFaulted = false
                };
                typeof(AntonRaidBattle).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(seat.Battle, next);
                seat.Context.RaidMovieCards = RaidCombatDisplayIdentity.Capture(seat.Actor, seat.Battle.View);
            }
            async Task OpenWindow(Seat seat)
            {
                seat.Context.Sender.Post(0, (ushort)NotiPacketType.RAID_STATE, [4, 1], "guide_result_open");
                await seat.Context.Sender.DrainAsync();
            }
            void RememberReceipt(Seat seat)
            {
                Sql("""
                    INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type)
                    VALUES($id,$account,$instance,1,0,0,1,1,'{}','[]',1,3)
                    """, ("$id", seat.Actor.Character), ("$account", seat.Actor.Owner.Account),
                    ("$instance", seat.Battle.Instance.ToString("N")));
            }
            RaidRewardReceipt Card(Seat seat)
            {
                var prepared = new PreparedRaidReward(seat.Battle.Instance, 1, 0, 0, seat.Actor.Character, PreyGuideRewards.Source,
                    [new InventoryReward(1, 1)], true, 1) { RaidType = RaidKind.Prey };
                return new(prepared, new(0, 0, [new InventoryReward(1, 1)], []),
                    new(seat.Session.Selected!, false, []), new InventoryResult(false, 0),
                    seat.Actor.Owner.Generation, seat.Session.SelectionEpoch);
            }
            async Task Claim(Seat seat)
            {
                var receipt = Card(seat);
                await RaidCombatProjection.PublishPreyGuideResultsAsync(seat.Context, receipt);
                await RaidCombatProjection.ReturnCompletedPreyGuideWaitingAsync(seat.Context, town, receipt);
                await seat.Context.Sender.DrainAsync();
            }
            int Homes(Seat seat) => seat.Frames.Count(row => row.Opcode == (ushort)NotiPacketType.AREA_USERS &&
                row.Body is [59, 2, ..]);
            int Opens(Seat seat) => seat.Frames.Count(row => row.Opcode == (ushort)NotiPacketType.RAID_STATE &&
                row.Body is [4, 1]);

            var manual = await Open("guide-home", "GwHome");
            var early = manual.Battle.View with { State = AntonRaidBattleState.Reward, PhaseExpired = false, ClockFaulted = false };
            typeof(AntonRaidBattle).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manual.Battle, early);
            That(manual.Session.ReturnPreyGuideCompletedWaiting().Status == GameSession.PreyGuideWaitingStatus.None &&
                !manual.Session.PreyGuideWaitingPending &&
                At(manual.Actor.Character, 59, 3), "一阶段奖励态不建立回候战义务，仍停鸟背");
            ShowFinal(manual);
            await OpenWindow(manual);
            RememberReceipt(manual);
            ArmFailure(manual.Actor.Character);
            await Claim(manual);
            await Claim(manual);
            RaidCombatProjection.SchedulePreyGuideWaitingRetry(manual.Context, town);
            await manual.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            That(manual.Session.PreyGuideWaitingPending && Homes(manual) == 0 && Opens(manual) == 1 &&
                Receipts(manual.Actor.Character) == 1 && At(manual.Actor.Character, 59, 3),
                "SQLite 拒绝候战保存时，660 与场景就绪都不抛、不搬、不重开窗、不新增收据");
            That(manual.Context.PreyGuideWaiting.Attempts == 0 && !manual.Context.PreyGuideWaiting.Exhausted &&
                manual.Faults.Exists(line => line.Contains("sqlite=19", StringComparison.Ordinal)),
                "不可重试的约束错误留下义务、不进入主动重试，并记下 sqlite=19");
            ClearFailure(manual.Actor.Character);
            await Claim(manual);
            That(!manual.Session.PreyGuideWaitingPending && Homes(manual) == 1 && Opens(manual) == 1 &&
                Receipts(manual.Actor.Character) == 1 && At(manual.Actor.Character, 59, 2),
                "保存恢复后的手动 660 只迁一次，587 仍是原先那一扇窗");
            int homes = Homes(manual);
            int opens = Opens(manual);
            await Claim(manual);
            RaidCombatProjection.SchedulePreyGuideWaitingRetry(manual.Context, town);
            await manual.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            That(Homes(manual) == homes && Opens(manual) == opens && Receipts(manual.Actor.Character) == 1 &&
                At(manual.Actor.Character, 59, 2) && !manual.Session.PreyGuideWaitingPending,
                "重复 660 和场景就绪只回放，不再搬迁、开窗或发奖");

            var notice = await Open("guide-note", "GwNote");
            ShowFinal(notice);
            await OpenWindow(notice);
            RememberReceipt(notice);
            ArmFailure(notice.Actor.Character);
            await Claim(notice);
            That(notice.Session.PreyGuideWaitingPending && At(notice.Actor.Character, 59, 3), "Notice 重试前先留下失败义务");
            ClearFailure(notice.Actor.Character);
            notice.Session.RaidBattleNoticeArrived = (view, actor) =>
            {
                RaidCombatProjection.Notice(notice.Context, view, actor);
                RaidCombatProjection.SchedulePreyGuideWaitingRetry(notice.Context, town);
            };
            notice.Battle.Publish(notice.Battle.View);
            await notice.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            await notice.Context.Sender.DrainAsync();
            That(!notice.Session.PreyGuideWaitingPending && Homes(notice) == 1 && Opens(notice) == 1 &&
                Receipts(notice.Actor.Character) == 1 && At(notice.Actor.Character, 59, 2),
                "下一次攻坚 Notice 重试候战，不重开 587，不新增收据");
            notice.Session.RaidBattleNoticeArrived = null;

            var dropped = await Open("guide-drop", "GwDrop");
            ShowFinal(dropped);
            await OpenWindow(dropped);
            RememberReceipt(dropped);
            ArmFailure(dropped.Actor.Character);
            await Claim(dropped);
            That(dropped.Session.PreyGuideWaitingPending && At(dropped.Actor.Character, 59, 3) && Opens(dropped) == 1,
                "断线前失败义务还在，角色仍停鸟背");
            dropped.Session.End();
            sessions.Remove(dropped.Session);
            That(!dropped.Session.PreyGuideWaitingPending && At(dropped.Actor.Character, 59, 3) &&
                Receipts(dropped.Actor.Character) == 1,
                "会话结束放弃候战义务，不把鸟背改写成候战区，也不补发奖");

            var revised = await Open("guide-rev", "GwRev");
            ShowFinal(revised);
            await OpenWindow(revised);
            RememberReceipt(revised);
            Sql("UPDATE characters SET revision=revision+1 WHERE id=$id", ("$id", revised.Actor.Character));
            await Claim(revised);
            That(!revised.Session.PreyGuideWaitingPending && Homes(revised) == 1 && Opens(revised) == 1 &&
                Receipts(revised.Actor.Character) == 1 && At(revised.Actor.Character, 59, 2),
                "revision 不匹配时重读同一角色后再迁一次，不重开窗");

            async Task Until(Func<bool> ready, string why)
            {
                long start = Environment.TickCount64;
                while (!ready())
                {
                    if (Environment.TickCount64 - start > 20_000) throw new TimeoutException(why);
                    await Task.Delay(20);
                }
            }
            void SetBattle(AntonRaidBattle battle, string field, object? value) =>
                typeof(AntonRaidBattle).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(battle, value);
            RaidRewardOffer GuideOffer()
            {
                var grant = new InventoryReward(100, 1);
                return new(0, 0, [grant], []) { PreyDisplay = [new LukeRewardResultSpec(0, 100, 1, 0, 0, 0)] };
            }
            LukeRaidCompletionPolicy GuideCompletion()
            {
                LukePhaseCompletionPolicy Phase(byte index) => new(1, 2, new(PreyGuideRewards.Source, [GuideOffer()], new(0, index, 1, 0, 0, 0)));
                return new(PreyGuideRewards.Source, 2, new(DayOfWeek.Thursday, new TimeOnly(6, 0), TimeSpan.FromHours(8)),
                    [Phase(0), Phase(1)], LukeParticipationCutoff.TerminalCommittedFacts);
            }
            var guideItems = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
            guideItems.PrefillTemplates(new Dictionary<int, ItemTemplate>
            {
                [100] = new(100, ItemKind.Consumable, 0, 1, 0, 100, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "guide-reward")
            });
            var guideGold = new GoldLimitCatalog("[gold limit from level]\n0 1000000\n[/gold limit from level]");
            var guideInventory = new InventoryProjection(stats, town, pieces);
            Seat Seal(Seat seat)
            {
                That(seat.Session.DetachRaidCombatStart(seat.Actor, seat.Battle.Instance), "卸下合成战场后再安装带资格的终局");
                var battle = new AntonRaidBattle(script, kind: RaidKind.Prey, preyGuide: true, completion: GuideCompletion());
                var binding = seat.Session.PrepareRaidCombatStart(seat.Actor, battle);
                That(binding is not null, "终局开战绑定登记原成员");
                binding!.Install();
                var fighting = battle.View with { Phase = 1, State = AntonRaidBattleState.Fighting, PhaseExpired = false, ClockFaulted = false };
                SetBattle(battle, "current", fighting);
                SetBattle(battle, "pendingTransition", fighting with { State = AntonRaidBattleState.Reward });
                var settlement = AntonRaidBattle.LukePhaseSettlement.Capture(battle, 1);
                var committed = new LukePhaseCommit(settlement.Members.Select(member => new RaidPhaseEligibility(settlement.Instance,
                    RaidKind.Prey, settlement.Phase, settlement.Event, settlement.Anchor.ToUnixTimeSeconds(), member.Character, true, true,
                    "guide-waiting", settlement.RewardsFor(member.Character).Source, settlement.Week,
                    settlement.RewardsFor(member.Character).Offers)).ToImmutableArray());
                That(battle.AcceptLukeSettlement(settlement, committed) is not null &&
                    battle.View is { State: AntonRaidBattleState.Reward, Phase: 1 } &&
                    battle.CompletePreyMovie(seat.Actor, battle.View) && battle.PreyMovieCardsReady(seat.Actor, battle.View) &&
                    battle.LukeEligibility(seat.Actor.Character, 1) is { Eligible: true },
                    "终局资格、电影和可领取权益都落在真实最终阶段");
                return seat with { Battle = battle };
            }
            async Task<TaskCompletionSource> Gate(Seat seat)
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                seat.Context.PreyGuideWaiting.BeforeAttempt = () => { entered.TrySetResult(); return release.Task; };
                RaidCombatProjection.SchedulePreyGuideWaitingRetry(seat.Context, town);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                return release;
            }
            void RememberWindow(Seat seat) =>
                seat.Context.RaidMovieCards = RaidCombatDisplayIdentity.Capture(seat.Actor, seat.Battle.View);

            var kept = await Open("guide-keep", "GwKeep");
            ShowFinal(kept);
            await OpenWindow(kept);
            RememberReceipt(kept);
            ArmFailure(kept.Actor.Character);
            await Claim(kept);
            var keepGate = await Gate(kept);
            That(kept.Session.PreyGuideWaitingPending && At(kept.Actor.Character, 59, 3) && Homes(kept) == 0, "原义务已排队，放行前仍停鸟背");
            ClearFailure(kept.Actor.Character);
            keepGate.TrySetResult();
            await kept.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            await kept.Context.Sender.DrainAsync();
            That(!kept.Session.PreyGuideWaitingPending && Homes(kept) == 1 && Opens(kept) == 1 &&
                Receipts(kept.Actor.Character) == 1 && At(kept.Actor.Character, 59, 2),
                "排队中的原义务在保存恢复后只迁一次，不重开 587");

            var swapped = await Open("guide-swap", "GwSwap");
            ShowFinal(swapped);
            await OpenWindow(swapped);
            RememberReceipt(swapped);
            ArmFailure(swapped.Actor.Character);
            await Claim(swapped);
            var swapGate = await Gate(swapped);
            ushort previous = swapped.Actor.Character;
            var created = repository.Create(swapped.Actor.Owner, new("GwNext", door, door) { Level = 95 });
            swapped.Session.ReturnToRoster();
            swapped.Session.Select(created.Slot);
            swapped.Session.MarkReady();
            var parked = repository.Save(swapped.Actor.Owner, swapped.Session.Selected!, towns.Spawn(new(59, 3, 0, 0), null)!, swapped.Session.Selected!.Return);
            typeof(GameSession).GetProperty("Selected")!.SetValue(swapped.Session, parked);
            var nextActor = swapped.Session.CaptureRaidActor(new RaidRegistry())!;
            var nextBattle = new AntonRaidBattle(script, kind: RaidKind.Prey, preyGuide: true);
            That(swapped.Session.AttachRaidCombat(nextActor, nextBattle, policies), "换角后的新角色也处于可被误迁的终局");
            ShowFinal(swapped with { Actor = nextActor, Battle = nextBattle });
            That(At(previous, 59, 3) && At(created.Id, 59, 3) && Homes(swapped) == 0, "放行旧任务前，新旧角色都还在鸟背");
            swapGate.TrySetResult();
            await swapped.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            await swapped.Context.Sender.DrainAsync();
            That(At(previous, 59, 3) && At(created.Id, 59, 3) && Homes(swapped) == 0 && !swapped.Session.PreyGuideWaitingPending,
                "换角后放行旧任务不写新角色、不提前迁移、不发目标 24");

            var moved = await Open("guide-inst", "GwInst");
            ShowFinal(moved);
            await OpenWindow(moved);
            RememberReceipt(moved);
            ArmFailure(moved.Actor.Character);
            await Claim(moved);
            Guid oldInstance = moved.Battle.Instance;
            var moveGate = await Gate(moved);
            That(moved.Session.DetachRaidCombatStart(moved.Actor, oldInstance), "旧实例卸下后义务身份仍是排队时的那一份");
            var other = new AntonRaidBattle(script, kind: RaidKind.Prey, preyGuide: true);
            That(moved.Session.AttachRaidCombat(moved.Actor, other, policies), "同一角色绑上另一个终局实例");
            ShowFinal(moved with { Battle = other });
            ClearFailure(moved.Actor.Character);
            moveGate.TrySetResult();
            await moved.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            await moved.Context.Sender.DrainAsync();
            That(At(moved.Actor.Character, 59, 3) && Homes(moved) == 0 && !moved.Session.PreyGuideWaitingPending &&
                moved.Session.CurrentRaidBattle(moved.Actor)!.Instance != oldInstance,
                "换实例后放行旧任务不接管新义务，也不提前迁到候战区");
            Sql("UPDATE characters SET revision=revision+1 WHERE id=$id", ("$id", moved.Actor.Character));
            moved.Session.PreyGuideCharacterReload = _ => throw new SqliteException("moved-busy", 5);
            moved.Context.PreyGuideWaiting.BeforeAttempt = () => throw new InvalidOperationException("moved-stop");
            await RaidCombatProjection.ReturnCompletedPreyGuideWaitingAsync(moved.Context, town, Card(moved with { Battle = other }));
            That(moved.Context.PreyGuideWaiting.Attempts >= 1 && moved.Session.PreyGuideWaitingPending &&
                Homes(moved) == 0 && At(moved.Actor.Character, 59, 3),
                "旧任务收尾后，新实例上的可重试故障仍能登记主动重试，且不提前搬迁");
            await moved.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            moved.Session.PreyGuideCharacterReload = null;

            var broken = await Open("guide-reload", "GwReload");
            ShowFinal(broken);
            await OpenWindow(broken);
            RememberReceipt(broken);
            Sql("UPDATE characters SET revision=revision+1 WHERE id=$id", ("$id", broken.Actor.Character));
            broken.Session.PreyGuideCharacterReload = _ => throw new SqliteException("reload-broken", 1);
            await Claim(broken);
            That(broken.Session.PreyGuideWaitingPending && Homes(broken) == 0 && At(broken.Actor.Character, 59, 3) &&
                broken.Context.PreyGuideWaiting.Attempts == 0 && !broken.Context.PreyGuideWaiting.Exhausted &&
                broken.Faults.Exists(line => line.Contains("sqlite=1", StringComparison.Ordinal)),
                "revision 冲突后的重读失败留下义务、不伪装搬迁，并记下 sqlite=1");
            broken.Session.PreyGuideCharacterReload = null;
            await Claim(broken);
            That(!broken.Session.PreyGuideWaitingPending && Homes(broken) == 1 && Opens(broken) == 1 &&
                At(broken.Actor.Character, 59, 2), "重读恢复后的显式 660 仍只迁一次");

            var busyReload = await Open("guide-reload-busy", "GwBusyReload");
            ShowFinal(busyReload);
            await OpenWindow(busyReload);
            RememberReceipt(busyReload);
            Sql("UPDATE characters SET revision=revision+1 WHERE id=$id", ("$id", busyReload.Actor.Character));
            busyReload.Session.PreyGuideCharacterReload = _ => throw new SqliteException("reload-busy", 5);
            await Claim(busyReload);
            await busyReload.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            That(busyReload.Context.PreyGuideWaiting.Exhausted &&
                busyReload.Context.PreyGuideWaiting.Attempts == PreyGuideWaitingRetry.MaxAttempts &&
                busyReload.Session.PreyGuideWaitingPending && Homes(busyReload) == 0 && At(busyReload.Actor.Character, 59, 3) &&
                busyReload.Faults.Exists(line => line.Contains("exhausted", StringComparison.Ordinal)),
                "重读忙锁可重试，但次数用尽后停在义务上，不发 24");
            busyReload.Session.PreyGuideCharacterReload = null;
            int busyHomes = Homes(busyReload);
            await Claim(busyReload);
            That(!busyReload.Session.PreyGuideWaitingPending && Homes(busyReload) == busyHomes + 1 &&
                At(busyReload.Actor.Character, 59, 2), "忙锁用尽后的显式 660 仍能完成原来的那一次搬迁");

            var boom = await Open("guide-boom", "GwBoom");
            ShowFinal(boom);
            await OpenWindow(boom);
            RememberReceipt(boom);
            ArmFailure(boom.Actor.Character);
            await Claim(boom);
            boom.Context.PreyGuideWaiting.BeforeAttempt = () => throw new InvalidOperationException("drain-boom");
            RaidCombatProjection.SchedulePreyGuideWaitingRetry(boom.Context, town);
            await boom.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            That(boom.Context.PreyGuideWaiting.Completion.IsCompletedSuccessfully && boom.Session.PreyGuideWaitingPending &&
                Homes(boom) == 0 && At(boom.Actor.Character, 59, 3) &&
                boom.Faults.Exists(line => line.Contains("InvalidOperationException", StringComparison.Ordinal) && line.Contains("drain-boom", StringComparison.Ordinal)),
                "异步重试抛错会记到诊断里并结束这次等待，义务不丢");

            var card = Seal(await Open("guide-660", "GwCard"));
            card.Context.RaidDisplay = RaidCombatDisplayIdentity.Capture(card.Actor, card.Battle.View);
            var cardStore = new MemoryRewards();
            var commands = new GameCommandRegistry(guideItems);
            new RaidCombatHandler(cardStore, guideItems, guideGold, pieces, guideInventory, new ServerLog(root, fileEnabled: false), town: town).Register(commands);
            commands.Seal();
            ArmFailure(card.Actor.Character);
            That(await commands.DispatchAsync(card.Context, new Packet(1, 660, [0, 0])) == CommandResult.Handled &&
                cardStore.Commits == 1 && card.Session.PreyGuideWaitingPending && Homes(card) == 0 && Opens(card) == 1 &&
                At(card.Actor.Character, 59, 3) && card.Battle.View.State == AntonRaidBattleState.Completed,
                "真实 660 在候战保存失败时仍完成领取和开窗，义务留下且不搬迁");
            ClearFailure(card.Actor.Character);
            int cardOpens = Opens(card);
            That(await commands.DispatchAsync(card.Context, new Packet(1, 660, [0, 0])) == CommandResult.Handled &&
                cardStore.Commits == 1 && !card.Session.PreyGuideWaitingPending && Homes(card) == 1 && Opens(card) == cardOpens &&
                At(card.Actor.Character, 59, 2),
                "保存恢复后的真实 660 回放原领取，并只补一次候战 24");
            await commands.DispatchAsync(card.Context, new Packet(1, 660, [0, 0]));
            That(cardStore.Commits == 1 && Homes(card) == 1 && Opens(card) == cardOpens && At(card.Actor.Character, 59, 2),
                "再次 660 只回放，不再搬迁或重开 587");

            var cancel = await Open("guide-cancel", "GwCancel");
            ShowFinal(cancel);
            await OpenWindow(cancel);
            RememberReceipt(cancel);
            Sql("UPDATE characters SET revision=revision+1 WHERE id=$id", ("$id", cancel.Actor.Character));
            cancel.Session.PreyGuideCharacterReload = _ => throw new SqliteException("reload-busy", 5);
            var enteredCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancel.Context.PreyGuideWaiting.BeforeAttempt = () => { enteredCancel.TrySetResult(); return hold.Task; };
            await Claim(cancel);
            await enteredCancel.Task.WaitAsync(TimeSpan.FromSeconds(3));
            That(cancel.Context.PreyGuideWaiting.Attempts >= 1 && cancel.Session.PreyGuideWaitingPending, "退出前主动重试已经停在门禁上");
            cancel.Session.End();
            sessions.Remove(cancel.Session);
            hold.TrySetResult();
            await cancel.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            That(!cancel.Session.PreyGuideWaitingPending && Homes(cancel) == 0 && At(cancel.Actor.Character, 59, 3),
                "会话结束取消后续等待；已经进入门禁的那一次复核后也不发 24");

            // 上界是断言，不是把生产命令超时改短。生产 Open 仍是 30 秒；这里的 2 秒/5 秒用来证明
            // 引导保存没有进入那次忙等。若 owner 里仍走 RetryWhileBusy，首次就会超过 2 秒，五次会远超 5 秒。
            That(SqliteGameDatabase.TestCommandTimeoutSeconds is null, "忙锁回归开始前没有缩短生产命令超时");
            using (var production = new SqliteGameDatabase(database).Open())
            {
                using var busy = production.CreateCommand();
                busy.CommandText = "PRAGMA busy_timeout";
                That(Convert.ToInt64(busy.ExecuteScalar()) == 30_000, "生产 Open 的 busy_timeout 仍是 30000 毫秒");
            }
            var peer = await Open("guide-peer", "GwPeer");
            async Task<long[]> Sample(Seat same, Seat other, Func<Task> body)
            {
                int run = 1;
                var max = new long[2];
                Task Loop(int index, Seat seat) => Task.Run(async () =>
                {
                    while (Volatile.Read(ref run) == 1)
                    {
                        long started = Environment.TickCount64;
                        _ = seat.Session.CopyPreyGuideWaiting();
                        long took = Environment.TickCount64 - started;
                        while (true)
                        {
                            long prev = Volatile.Read(ref max[index]);
                            if (took <= prev || Interlocked.CompareExchange(ref max[index], took, prev) == prev) break;
                        }
                        await Task.Delay(1).ConfigureAwait(false);
                    }
                });
                var left = Loop(0, same);
                var right = Loop(1, other);
                try { await body().ConfigureAwait(false); }
                finally
                {
                    Volatile.Write(ref run, 0);
                    await left.ConfigureAwait(false);
                    await right.ConfigureAwait(false);
                }
                return max;
            }
            (SqliteConnection Holder, SqliteTransaction Lock) LockDatabase()
            {
                var holder = new SqliteConnection($"Data Source={database};Pooling=False");
                holder.Open();
                using var pragma = holder.CreateCommand();
                pragma.CommandText = "PRAGMA busy_timeout=0";
                pragma.ExecuteNonQuery();
                return (holder, holder.BeginTransaction(deferred: false));
            }
            void ParkBird(AccountOwners.Lease owner, GameSession session)
            {
                var current = session.Selected ?? throw new InvalidOperationException("候战夹具没有选中角色");
                var bird = towns.Spawn(new(59, 3, 0, 0), null)!;
                var parked = repository.Save(owner, current, bird, current.Return);
                typeof(GameSession).GetProperty("Selected")!.SetValue(session, parked);
            }
            void BusyReload(Seat seat)
            {
                Sql("UPDATE characters SET revision=revision+1 WHERE id=$id", ("$id", seat.Session.Selected!.Id));
                seat.Session.PreyGuideCharacterReload = _ => throw new SqliteException("guide-busy", 5);
            }

            long firstMs = -1, retryMs = -1, capMs = -1, cancelMs = -1, samePeak = -1, otherPeak = -1;
            var active = Seal(await Open("guide-active", "GwActive"));
            var activeStore = new MemoryRewards();
            var online = new OnlineSessionDirectory();
            online.Register(active.Actor.Owner, active.Session);
            int notices = 0;
            active.Session.RaidBattleNoticeArrived = (_, _) =>
            {
                notices++;
                RaidCombatProjection.Notice(active.Context, active.Battle.View, active.Actor);
                RaidCombatProjection.SchedulePreyGuideWaitingRetry(active.Context, town);
            };
            active.Session.AutomaticRaidRewardArrived = async receipt =>
            {
                await guideInventory.RefreshAsync(active.Context, receipt.Inventory);
                await RaidCombatProjection.PublishPreyGuideResultsAsync(active.Context, receipt);
                await RaidCombatProjection.ReturnCompletedPreyGuideWaitingAsync(active.Context, town, receipt);
            };
            var automatic = new LukeAutomaticRewards(activeStore, ownership, online, repository.LoadCharacterById,
                guideItems, guideGold, pieces, TimeProvider.System);
            That(active.Battle.DueLukeRewards().Length == 1, "真实自动结算能看到这条终局权益");
            RememberWindow(active);
            await OpenWindow(active);
            int entered = 0;
            var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var laterGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            active.Context.PreyGuideWaiting.BeforeAttempt = () =>
            {
                int turn = Interlocked.Increment(ref entered);
                return turn == 1 ? firstGate.Task : turn >= 3 ? laterGate.Task : Task.CompletedTask;
            };
            var (activeHolder, activeLock) = LockDatabase();
            SqliteTransaction? activeBusy = activeLock;
            try
            {
                long sameCall = 0, otherCall = 0;
                var seen = await Sample(active, peer, async () =>
                {
                    long started = Environment.TickCount64;
                    await automatic.SettleAsync(active.Battle);
                    firstMs = Environment.TickCount64 - started;
                    await Until(() => entered >= 1 && active.Context.PreyGuideWaiting.Attempts >= 1, "主动重试没有进入第一次排队");
                    long mark = Environment.TickCount64;
                    _ = active.Session.CopyPreyGuideWaiting();
                    sameCall = Environment.TickCount64 - mark;
                    mark = Environment.TickCount64;
                    _ = peer.Session.CopyPreyGuideWaiting();
                    otherCall = Environment.TickCount64 - mark;
                    That(SqliteGameDatabase.TestCommandTimeoutSeconds is null && firstMs >= 0 && firstMs < 3_000 &&
                        sameCall < 3_000 && otherCall < 3_000,
                        $"首次忙锁把账号占到了生产超时：first={firstMs}ms same={sameCall}ms other={otherCall}ms");
                    That(activeStore.Commits == 1 && active.Session.PreyGuideWaitingPending && Homes(active) == 0 && Opens(active) == 1 &&
                        At(active.Actor.Character, 59, 3), "首次候战保存遇忙锁后奖励已入账，24 还没发");
                    active.Battle.Publish(active.Battle.View);
                    long retryStart = Environment.TickCount64;
                    firstGate.TrySetResult();
                    await Until(() => active.Context.PreyGuideWaiting.Attempts >= 3 && entered >= 3, "Notice 重试没有在写锁下失败");
                    retryMs = Environment.TickCount64 - retryStart;
                    That(retryMs < 5_000, $"锁内重试 {retryMs}ms，后续请求仍会被 30 秒忙等拖住");
                });
                samePeak = seen[0];
                otherPeak = seen[1];
                That(samePeak < 3_000 && otherPeak < 3_000,
                    $"忙锁期间同账号探针 {samePeak}ms、其他账号探针 {otherPeak}ms");
                int activeOpens = Opens(active);
                int lists = active.Frames.Count(row => row.Opcode == (ushort)NotiPacketType.RAID_REWARD_LIST);
                activeBusy.Dispose();
                activeBusy = null;
                laterGate.TrySetResult();
                await Until(() => !active.Session.PreyGuideWaitingPending && At(active.Actor.Character, 59, 2), "写锁释放后没有自己完成候战搬迁");
                await active.Context.Sender.DrainAsync();
                That(notices >= 1 && Homes(active) == 1 && Opens(active) == activeOpens && activeStore.Commits == 1 &&
                    active.Frames.Count(row => row.Opcode == (ushort)NotiPacketType.RAID_REWARD_LIST) == lists &&
                    At(active.Actor.Character, 59, 2),
                    "不再 Publish 或领取后，数据库恢复只发一次目标 24，奖励和 587 不重复");
            }
            finally
            {
                laterGate.TrySetResult();
                activeBusy?.Dispose();
                activeHolder.Dispose();
            }

            var capped = Seal(await Open("guide-cap", "GwCap"));
            var capStore = new MemoryRewards();
            var capOnline = new OnlineSessionDirectory();
            capOnline.Register(capped.Actor.Owner, capped.Session);
            capped.Session.AutomaticRaidRewardArrived = async receipt =>
            {
                await RaidCombatProjection.PublishPreyGuideResultsAsync(capped.Context, receipt);
                await RaidCombatProjection.ReturnCompletedPreyGuideWaitingAsync(capped.Context, town, receipt);
            };
            var capAutomatic = new LukeAutomaticRewards(capStore, ownership, capOnline, repository.LoadCharacterById,
                guideItems, guideGold, pieces, TimeProvider.System);
            RememberWindow(capped);
            await OpenWindow(capped);
            var (capHolder, capLock) = LockDatabase();
            try
            {
                var capSeen = await Sample(capped, peer, async () =>
                {
                    long started = Environment.TickCount64;
                    await capAutomatic.SettleAsync(capped.Battle);
                    await Until(() => capped.Context.PreyGuideWaiting.Exhausted, "持续忙锁没有在次数上限处停住");
                    capMs = Environment.TickCount64 - started;
                });
                That(capMs >= 250 && capMs < 8_000 && capSeen[0] < 3_000 && capSeen[1] < 3_000 &&
                    SqliteGameDatabase.TestCommandTimeoutSeconds is null,
                    $"五次忙锁应落在退避附近而不是 30 秒×5：exhaust={capMs}ms same={capSeen[0]}ms other={capSeen[1]}ms");
                That(capped.Context.PreyGuideWaiting.Attempts == PreyGuideWaitingRetry.MaxAttempts &&
                    capped.Session.PreyGuideWaitingPending && Homes(capped) == 0 && capStore.Commits == 1 &&
                    At(capped.Actor.Character, 59, 3), "持续失败停在 5 次，义务还在，不发 24，奖励不重放");
                RaidCombatProjection.SchedulePreyGuideWaitingRetry(capped.Context, town);
                await Task.Delay(300);
                That(capped.Context.PreyGuideWaiting.Attempts == PreyGuideWaitingRetry.MaxAttempts && Homes(capped) == 0 &&
                    capped.Context.PreyGuideWaiting.Completion.IsCompletedSuccessfully,
                    "次数用尽后场景就绪不再拉起新的后台循环");
            }
            finally { capLock.Dispose(); capHolder.Dispose(); }

            var halt = await Open("guide-halt", "GwHalt");
            ShowFinal(halt);
            await OpenWindow(halt);
            RememberReceipt(halt);
            var (haltHolder, haltLock) = LockDatabase();
            SqliteTransaction? haltBusy = haltLock;
            try
            {
                long haltFirst = 0;
                var haltSeen = await Sample(halt, peer, async () =>
                {
                    long started = Environment.TickCount64;
                    await Claim(halt);
                    haltFirst = Environment.TickCount64 - started;
                    await Until(() => halt.Context.PreyGuideWaiting.Attempts >= 2, "重试没有在忙锁下再走一次保存");
                    long mark = Environment.TickCount64;
                    halt.Context.PreyGuideWaiting.Cancel();
                    await halt.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(2));
                    cancelMs = Environment.TickCount64 - mark;
                });
                That(haltFirst < 3_000 && cancelMs < 3_000 && haltSeen[0] < 3_000 && haltSeen[1] < 3_000 &&
                    halt.Session.PreyGuideWaitingPending && Homes(halt) == 0 && At(halt.Actor.Character, 59, 3) &&
                    Receipts(halt.Actor.Character) == 1,
                    $"取消没有打断锁内等待：first={haltFirst}ms cancel={cancelMs}ms same={haltSeen[0]}ms other={haltSeen[1]}ms");
                haltBusy.Dispose();
                haltBusy = null;
                int haltHomes = Homes(halt);
                int haltOpens = Opens(halt);
                await Claim(halt);
                That(!halt.Session.PreyGuideWaitingPending && Homes(halt) == haltHomes + 1 && Opens(halt) == haltOpens &&
                    Receipts(halt.Actor.Character) == 1 && At(halt.Actor.Character, 59, 2),
                    "锁放开后的显式领取只补一次候战 24，不重开 587、不重发奖");
            }
            finally { haltBusy?.Dispose(); haltHolder.Dispose(); }

            var renew = await Open("guide-renew", "GwRenew");
            ShowFinal(renew);
            await OpenWindow(renew);
            RememberReceipt(renew);
            var renewGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int renewTurns = 0;
            renew.Context.PreyGuideWaiting.BeforeAttempt = () =>
                Interlocked.Increment(ref renewTurns) == 1 ? renewGate.Task : Task.CompletedTask;
            var (renewHolder, renewLock) = LockDatabase();
            SqliteTransaction? renewBusy = renewLock;
            GameSession.PreyGuideWaitingDuty? finished = null;
            try
            {
                long started = Environment.TickCount64;
                await Claim(renew);
                That(Environment.TickCount64 - started < 3_000, "同一连接上新义务的首次忙锁保存超过观察上界");
                await Until(() => renewTurns >= 1 && renew.Session.CopyPreyGuideWaiting() is not null, "成功路径的第一次排队没有停下");
                finished = renew.Session.CopyPreyGuideWaiting();
                That(finished is not null && renew.Context.PreyGuideWaiting.AttemptsFor(finished) >= 1 &&
                    !renew.Context.PreyGuideWaiting.ExhaustedFor(finished),
                    "成功前这份义务已经计次，且还没耗尽");
                renewBusy.Dispose();
                renewBusy = null;
                renewGate.TrySetResult();
                await renew.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally { renewGate.TrySetResult(); renewBusy?.Dispose(); renewHolder.Dispose(); }
            int spentBefore = renew.Context.PreyGuideWaiting.AttemptsFor(finished!);
            That(!renew.Session.PreyGuideWaitingPending && Homes(renew) == 1 && At(renew.Actor.Character, 59, 2) &&
                spentBefore >= 1 && !renew.Context.PreyGuideWaiting.ExhaustedFor(finished!) && renew.Context.PreyGuideWaiting.Attempts == 0,
                "原义务成功后只迁一次；槽位清空，但这份身份的已用次数还在");
            renew.Context.PreyGuideWaiting.BeforeAttempt = null;
            ParkBird(renew.Actor.Owner, renew.Session);
            That(renew.Session.DetachRaidCombatStart(renew.Actor, renew.Battle.Instance), "卸下已经完成的实例");
            var renewBattle = new AntonRaidBattle(script, kind: RaidKind.Prey, preyGuide: true);
            That(renew.Session.AttachRaidCombat(renew.Actor, renewBattle, policies), "同一连接绑上新的引导实例");
            var renewSeat = renew with { Battle = renewBattle };
            ShowFinal(renewSeat);
            BusyReload(renewSeat);
            await RaidCombatProjection.ReturnCompletedPreyGuideWaitingAsync(renew.Context, town, Card(renewSeat));
            var renewBorn = renew.Session.CopyPreyGuideWaiting();
            That(renewBorn is not null && renewBorn != finished && renewBorn.Instance == renewBattle.Instance &&
                renew.Context.PreyGuideWaiting.Attempts == 1 && !renew.Context.PreyGuideWaiting.Exhausted &&
                renew.Context.PreyGuideWaiting.AttemptsFor(finished!) == spentBefore &&
                Homes(renew) == 1 && At(renew.Actor.Character, 59, 3),
                "成功之后的新实例从自己的第 1 次开始，不继承旧次数，也不提前搬");
            await renew.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            That(renew.Context.PreyGuideWaiting.ExhaustedFor(renewBorn) &&
                renew.Context.PreyGuideWaiting.AttemptsFor(renewBorn) == PreyGuideWaitingRetry.MaxAttempts &&
                renew.Context.PreyGuideWaiting.AttemptsFor(finished!) == spentBefore &&
                !renew.Context.PreyGuideWaiting.ExhaustedFor(finished!) &&
                Homes(renew) == 1 && At(renew.Actor.Character, 59, 3),
                "新实例可以独立用尽，旧义务停在成功时的次数");
            renew.Session.PreyGuideCharacterReload = null;

            var budget = await Open("guide-budget", "GwBudget");
            ShowFinal(budget);
            await OpenWindow(budget);
            RememberReceipt(budget);
            BusyReload(budget);
            await Claim(budget);
            await budget.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            var dead = budget.Session.CopyPreyGuideWaiting();
            That(dead is not null && budget.Context.PreyGuideWaiting.ExhaustedFor(dead) &&
                budget.Context.PreyGuideWaiting.AttemptsFor(dead) == PreyGuideWaitingRetry.MaxAttempts &&
                Homes(budget) == 0 && At(budget.Actor.Character, 59, 3),
                "原义务先用尽，仍停鸟背");
            That(budget.Session.DetachRaidCombatStart(budget.Actor, budget.Battle.Instance), "用尽后卸下旧实例");
            var budgetBattle = new AntonRaidBattle(script, kind: RaidKind.Prey, preyGuide: true);
            That(budget.Session.AttachRaidCombat(budget.Actor, budgetBattle, policies), "用尽后绑上另一个实例");
            var budgetSeat = budget with { Battle = budgetBattle };
            ShowFinal(budgetSeat);
            var instGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int instTurns = 0;
            budget.Context.PreyGuideWaiting.BeforeAttempt = () =>
                Interlocked.Increment(ref instTurns) == 1 ? instGate.Task : Task.CompletedTask;
            await RaidCombatProjection.ReturnCompletedPreyGuideWaitingAsync(budget.Context, town, Card(budgetSeat));
            var swappedDuty = budget.Session.CopyPreyGuideWaiting();
            That(swappedDuty is not null && swappedDuty != dead && swappedDuty.Instance == budgetBattle.Instance &&
                budget.Context.PreyGuideWaiting.AttemptsFor(swappedDuty) == 1 &&
                !budget.Context.PreyGuideWaiting.ExhaustedFor(swappedDuty) &&
                budget.Context.PreyGuideWaiting.AttemptsFor(dead) == PreyGuideWaitingRetry.MaxAttempts &&
                budget.Context.PreyGuideWaiting.ExhaustedFor(dead),
                "用尽后换实例，新义务从 1 开始，旧身份仍然用尽");
            instGate.TrySetResult();
            await Until(() => budget.Context.PreyGuideWaiting.ExhaustedFor(swappedDuty), "新实例没有走到自己的上限");
            That(budget.Context.PreyGuideWaiting.AttemptsFor(swappedDuty) == PreyGuideWaitingRetry.MaxAttempts &&
                budget.Context.PreyGuideWaiting.AttemptsFor(dead) == PreyGuideWaitingRetry.MaxAttempts &&
                Homes(budget) == 0 && At(budget.Actor.Character, 59, 3),
                "新实例用尽不影响旧身份，也不提前搬");
            int stray = 0;
            budget.Context.PreyGuideWaiting.BeforeAttempt = () => { Interlocked.Increment(ref stray); return Task.CompletedTask; };
            budget.Context.PreyGuideWaiting.Arm(dead!, _ => { Interlocked.Increment(ref stray); return Task.FromResult(GameSession.PreyGuideWaitingStatus.None); });
            budget.Context.PreyGuideWaiting.Schedule(dead!, _ => { Interlocked.Increment(ref stray); return Task.FromResult(GameSession.PreyGuideWaitingStatus.None); });
            await Task.Delay(150);
            That(stray == 0 && budget.Context.PreyGuideWaiting.AttemptsFor(dead) == PreyGuideWaitingRetry.MaxAttempts &&
                budget.Context.PreyGuideWaiting.ExhaustedFor(dead),
                "已用尽的旧身份不能靠 Arm 或 Schedule 重新开跑");
            ushort previousRole = budget.Actor.Character;
            var createdRole = repository.Create(budget.Actor.Owner, new("GwRole", door, door) { Level = 95 });
            budget.Session.ReturnToRoster();
            budget.Session.Select(createdRole.Slot);
            budget.Session.MarkReady();
            ParkBird(budget.Actor.Owner, budget.Session);
            var roleActor = budget.Session.CaptureRaidActor(new RaidRegistry())!;
            var roleBattle = new AntonRaidBattle(script, kind: RaidKind.Prey, preyGuide: true);
            That(budget.Session.AttachRaidCombat(roleActor, roleBattle, policies), "换角后的新角色处于可建义务的终局");
            var roleSeat = budget with { Actor = roleActor, Battle = roleBattle };
            ShowFinal(roleSeat);
            BusyReload(roleSeat);
            var roleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            budget.Context.PreyGuideWaiting.BeforeAttempt = () => roleGate.Task;
            await RaidCombatProjection.ReturnCompletedPreyGuideWaitingAsync(budget.Context, town, Card(roleSeat));
            var roleDuty = budget.Session.CopyPreyGuideWaiting();
            That(roleDuty is not null && roleDuty.Character == createdRole.Id && roleDuty != dead && roleDuty != swappedDuty &&
                budget.Context.PreyGuideWaiting.Attempts == 1 && budget.Context.PreyGuideWaiting.AttemptsFor(roleDuty) == 1 &&
                !budget.Context.PreyGuideWaiting.Exhausted &&
                budget.Context.PreyGuideWaiting.AttemptsFor(dead) == PreyGuideWaitingRetry.MaxAttempts &&
                budget.Context.PreyGuideWaiting.ExhaustedFor(dead) &&
                budget.Context.PreyGuideWaiting.AttemptsFor(swappedDuty) == PreyGuideWaitingRetry.MaxAttempts &&
                At(previousRole, 59, 3) && At(createdRole.Id, 59, 3) && Homes(budget) == 0,
                "用尽后换角，新角色从 1 开始，旧身份仍用尽，两个角色都不提前搬");
            budget.Context.PreyGuideWaiting.Cancel();
            That(budget.Context.PreyGuideWaiting.AttemptsFor(dead) == PreyGuideWaitingRetry.MaxAttempts &&
                budget.Context.PreyGuideWaiting.ExhaustedFor(dead) &&
                budget.Context.PreyGuideWaiting.AttemptsFor(swappedDuty) == PreyGuideWaitingRetry.MaxAttempts &&
                budget.Context.PreyGuideWaiting.AttemptsFor(roleDuty) == 1,
                "取消不清已用尽的旧身份，也不把新义务已用的 1 次清零");
            roleGate.TrySetResult();
            await budget.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            stray = 0;
            budget.Context.PreyGuideWaiting.BeforeAttempt = () => { Interlocked.Increment(ref stray); return Task.CompletedTask; };
            budget.Context.PreyGuideWaiting.Arm(dead!, _ => { Interlocked.Increment(ref stray); return Task.FromResult(GameSession.PreyGuideWaitingStatus.None); });
            await Task.Delay(150);
            That(stray == 0 && budget.Context.PreyGuideWaiting.ExhaustedFor(dead) &&
                budget.Context.PreyGuideWaiting.AttemptsFor(dead) == PreyGuideWaitingRetry.MaxAttempts,
                "取消之后再 Arm 用尽的旧身份仍然不跑");
            budget.Session.PreyGuideCharacterReload = null;

            var late = await Open("guide-late", "GwLate");
            ShowFinal(late);
            await OpenWindow(late);
            RememberReceipt(late);
            BusyReload(late);
            var lateGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            late.Context.PreyGuideWaiting.BeforeAttempt = () => { lateEntered.TrySetResult(); return lateGate.Task; };
            await Claim(late);
            await lateEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var lateOld = late.Session.CopyPreyGuideWaiting();
            That(lateOld is not null && late.Context.PreyGuideWaiting.AttemptsFor(lateOld) >= 1, "旧任务已经停在门禁上");
            int lateOldCount = late.Context.PreyGuideWaiting.AttemptsFor(lateOld);
            var lateNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int lateSecond = 0;
            late.Context.PreyGuideWaiting.BeforeAttempt = () => { Interlocked.Increment(ref lateSecond); return lateNext.Task; };
            That(late.Session.DetachRaidCombatStart(late.Actor, late.Battle.Instance), "旧任务还停着时卸下它的实例");
            var lateBattle = new AntonRaidBattle(script, kind: RaidKind.Prey, preyGuide: true);
            That(late.Session.AttachRaidCombat(late.Actor, lateBattle, policies), "旧任务未完成时已经有新实例");
            var lateSeat = late with { Battle = lateBattle };
            ShowFinal(lateSeat);
            await RaidCombatProjection.ReturnCompletedPreyGuideWaitingAsync(late.Context, town, Card(lateSeat));
            var lateBorn = late.Session.CopyPreyGuideWaiting();
            That(lateBorn is not null && lateBorn != lateOld && late.Context.PreyGuideWaiting.AttemptsFor(lateBorn) == 1 &&
                late.Context.PreyGuideWaiting.AttemptsFor(lateOld) == lateOldCount && !late.Context.PreyGuideWaiting.ExhaustedFor(lateBorn),
                "新义务先记上自己的 1 次，旧任务还没把它改掉");
            lateGate.TrySetResult();
            await Until(() => lateSecond >= 1, "旧任务收尾后没有留下新义务自己的下一次尝试");
            That(late.Session.CopyPreyGuideWaiting() == lateBorn && late.Session.PreyGuideWaitingPending &&
                late.Context.PreyGuideWaiting.AttemptsFor(lateBorn) == 1 &&
                late.Context.PreyGuideWaiting.AttemptsFor(lateOld) == lateOldCount &&
                !late.Context.PreyGuideWaiting.ExhaustedFor(lateBorn) &&
                Homes(late) == 0 && At(late.Actor.Character, 59, 3),
                "旧任务迟到完成时不给新义务加次，也不清掉新义务");
            lateNext.TrySetResult();
            await Until(() => late.Context.PreyGuideWaiting.AttemptsFor(lateBorn) >= 2, "新义务在旧任务收尾后不能继续重试");
            That(late.Context.PreyGuideWaiting.AttemptsFor(lateOld) == lateOldCount && late.Session.PreyGuideWaitingPending &&
                Homes(late) == 0 && At(late.Actor.Character, 59, 3),
                "新义务继续重试时，旧身份的次数保持不变");
            await late.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            late.Session.PreyGuideCharacterReload = null;

            var frozen = await Open("guide-frozen", "GwFrozen");
            ShowFinal(frozen);
            await OpenWindow(frozen);
            RememberReceipt(frozen);
            BusyReload(frozen);
            frozen.Context.PreyGuideWaiting.BeforeAttempt = () =>
            {
                frozen.Session.PreyGuideCharacterReload = null;
                frozen.Context.RaidMovieCards = null;
                return Task.CompletedTask;
            };
            await Claim(frozen);
            await frozen.Context.PreyGuideWaiting.Completion.WaitAsync(TimeSpan.FromSeconds(3));
            var frozenDuty = frozen.Session.CopyPreyGuideWaiting();
            That(frozenDuty is not null && frozen.Session.PreyGuideWaitingPending && Homes(frozen) == 0 && Opens(frozen) == 1 &&
                At(frozen.Actor.Character, 59, 3) && frozen.Context.PreyGuideWaiting.AttemptsFor(frozenDuty) >= 1,
                "结果窗在执行前被摘掉、重读故障也清掉后，带原义务的重试仍不搬走");
            frozen.Context.PreyGuideWaiting.BeforeAttempt = null;
            RememberWindow(frozen);
            int frozenHomes = Homes(frozen);
            int frozenOpens = Opens(frozen);
            await Claim(frozen);
            That(!frozen.Session.PreyGuideWaitingPending && Homes(frozen) == frozenHomes + 1 && Opens(frozen) == frozenOpens &&
                At(frozen.Actor.Character, 59, 2),
                "结果窗恢复后的显式领取只迁一次");

            That(SqliteGameDatabase.TestCommandTimeoutSeconds is null, "忙锁回归结束时仍然没有缩短生产命令超时");
            Console.WriteLine($"PREY-GUIDE-LOCK first={firstMs}ms retry={retryMs}ms probeSame={samePeak}ms probeOther={otherPeak}ms exhaust={capMs}ms cancel={cancelMs}ms");

            passed = true;
        }
        finally
        {
            SqliteGameDatabase.TestCommandTimeoutSeconds = null;
            foreach (var session in sessions)
            {
                session.PreyGuideCharacterReload = null;
                session.End();
            }
            foreach (var sender in senders) await sender.DisposeAsync();
            foreach (var owner in owners) owner.Dispose();
            foreach (var stop in stops) stop.Dispose();
            if (passed) TestFiles.DeleteDirectory(root, true);
            else Console.WriteLine("FAIL fixture: " + root);
        }
        Console.WriteLine($"PASS {Count - before} 引导终局回候战义务");
    }

    sealed record Seat(GameSession Session, RaidActor Actor, AntonRaidBattle Battle, GameConnectionContext Context,
        List<(ushort Opcode, byte[] Body)> Frames, List<string> Faults);

    // 不写 SQL、不抬 revision。外部写锁只会挡住随后的候战保存，不会把领取本身变成忙锁。
    sealed class MemoryRewards : ILukeRewardRecoveryStore
    {
        public int Commits;
        public RaidRewardCommit? CommitRaidReward(AccountOwners.Lease owner, Character expected, PreparedRaidReward prepared,
            InventoryPlan inventory, DateTimeOffset now)
        {
            Commits++;
            return new(expected, false, []);
        }
        public LukeRewardRecoveryPage ListLukeRewardRecovery(AccountOwners.Lease owner, Character character, Guid? afterInstance = null, int limit = 16) =>
            throw new NotSupportedException();
        public LukeRewardRecoverySnapshot ReadLukeRewardRecovery(AccountOwners.Lease owner, Character character, LukeRewardRecoveryTarget target) =>
            throw new NotSupportedException();
    }
}
