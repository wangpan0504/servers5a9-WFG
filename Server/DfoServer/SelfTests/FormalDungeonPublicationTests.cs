using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer;

internal static class FormalDungeonPublicationTests
{
    public static async Task RunAsync(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items)
    {
        int before = TestCheck.Count;
        var computer = new CharacterStatComputer(characters);
        var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf);
        var kills = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf);
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var quests = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        await LevelRefreshAsync();
        await EntryRejectionAsync();
        await StorageFailureAsync(false);
        await StorageFailureAsync(true);
        await MaxLevelExperienceNoticeAsync();
        foreach (int packet in new[] { 1, 2 }) await SendFailureAsync(packet);
        await ReselectDuringSendAsync();
        await ReturnDuringSendAsync();
        await PickupLostResultAsync();
        await CollectionPublicationAsync();
        await DiscardPublicationAsync(false);
        await DiscardPublicationAsync(true);
        await ClearedQuestSendFailureAsync();
        await ClearNoticeFailureAsync();
        Console.WriteLine($"PASS {TestCheck.Count - before} 正式Handler真实文件提交、结果丢失、发送故障及重选隔离断言（合成死亡，非实机）");


        GameCommandRegistry Registry(Fixture f, TownPresence? presence = null,
            Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? prepareDrops = null,
            InventoryService? inventoryActions = null)
        {
            var commands = new GameCommandRegistry();
            new DungeonHandler(f.Log, catalog, rooms, growth, kills,
                new TownProjection(towns, computer, presence, items.EpicPieces), new CharacterLevelProjection(computer),
                global::DfoServer.Testing.SelfTestCatalogs.MonsterDrops(pvf), items.Items, items.EpicPieces, items.GoldLimits,
                prepareDrops: prepareDrops).Register(commands);
            if (inventoryActions is not null)
                new InventoryHandler(inventoryActions, new InventoryProjection(computer,
                    new TownProjection(towns, computer, presence, items.EpicPieces), items.EpicPieces)).Register(commands);
            commands.Seal();
            return commands;
        }
        (FormalDungeonReceipt Entry, Packet Death) Enter(Fixture f)
        {
            var entry = f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms)
                ?? throw new InvalidOperationException("FAIL 正式通知fixture准入失败");
            f.Session.CompleteFormalDungeonLoading();
            var monster = entry.Monsters.Monsters[0];
            return (entry, new(1, 39, Death(monster.ObjectKey, entry.Character.Id, checked((byte)monster.CandidateLevel))));
        }
        async Task LevelRefreshAsync()
        {
            using var f = new Fixture(towns, characters, learning, quests, false);
            var selected = f.Session.Selected!;
            // 只验证投影合同：模拟等级变化但技能绑定完全相同的已提交快照，不运行成长或写库。
            var committed = selected with { Level = 2, Learning = selected.Learning with { Experience = growth.GetLevelStartExperience(2) } };
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput();
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(83, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            var projection = new CharacterLevelProjection(computer);
            await projection.RefreshAsync(context, committed, selected.Level, "level_refresh", () => true);
            Check(!SkillPackets.BindingsChanged(selected.Learning, committed.Learning) &&
                stream.Opcodes.SequenceEqual(new ushort[] { 2, 2, 19 }) &&
                stream.Frames[0][16] == 0 && stream.Frames[1][16] == 1,
                "绑定不变的升级仍在Actor及Player后重装一次完整技能，不依赖技能等级变化");
            Check(SkillPacketsTests.DecodeInitialized(stream.Frames[2].AsSpan(16)).Length == 2 && Same(selected, f.Read()),
                "重装投影保留完整双树且不改数据库或派生成长奖励");
            await projection.RefreshAsync(context, committed, committed.Level, "unchanged", () => true);
            await projection.RefreshAsync(context, committed, selected.Level, "stale", () => false);
            Check(stream.Frames.Count == 3, "同等级经验和失效回执不追加Player或被动重装");

            CharacterLearningState baseline = new(0, [
                new(0, 130, 2, [new(54, 31, 1), new(103, 174, 15), new(10, 200, 1)]),
                new(1, 100, 3, [new(54, 31, 1), new(103, 174, 15)])]);
            var updated = committed with { Learning = baseline with { Trees = baseline.Trees.Select(tree => tree with
            {
                Sp = checked((ushort)(tree.Sp + 20)),
                Skills = tree.Skills.Select(skill => skill.Id == 174 ? skill with { Level = 16 } : skill).ToArray()
            }).ToArray() } };
            await SkillProjection.PublishGrowthAsync(context, baseline, updated, "growth", "combos", () => true);
            var deltas = stream.Frames.Skip(3).Select(frame => SkillPacketsTests.DecodeSuccess(frame.AsSpan(16))).ToArray();
            Check(stream.Frames.Skip(3).All(frame => frame[0] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 29) &&
                deltas.Select(delta => delta.Context).SequenceEqual(new byte[] { 1, 0 }) &&
                deltas.All(delta => delta.Mode == 0 && delta.Skills is [{ Id: 174, Level: 16, Slot: 103 }]),
                "升级只更新两树实际变化的固定技能，不重发技能31或清理无变化运行对象");
            Check(deltas.All(delta => delta.Sp == updated.Learning.Trees.Single(tree => tree.Context == delta.Context).Sp) &&
                Same(selected, f.Read()), "成长差量使用真实绝对点数且投影不改库");

            var alternateOnly = updated with { Learning = baseline with { Trees = [baseline.Trees[0], updated.Learning.Trees[1]] } };
            await SkillProjection.PublishGrowthAsync(context, baseline, alternateOnly, "alternate", "combos", () => true);
            var restored = SkillPacketsTests.DecodeSuccess(stream.Frames[^1].AsSpan(16), allowEmpty: true);
            Check(stream.Frames.Count == 7 && restored.Context == 0 && restored.Skills.Length == 0 && restored.Sp == baseline.Trees[0].Sp,
                "仅备用树变化后用原生零项回复恢复主树，不重复更新未变技能");

            var replacement = updated with { Learning = baseline with { Trees = [baseline.Trees[0] with
            {
                Skills = [new(54, 31, 1), new(103, 174, 15), new(10, 511, 1) { Command = "030304" }]
            }, baseline.Trees[1]] } };
            await SkillProjection.PublishGrowthAsync(context, baseline, replacement, "replacement", "combos", () => true);
            var removed = SkillPacketsTests.DecodeSuccess(stream.Frames[^2].AsSpan(16));
            var added = SkillPacketsTests.DecodeSuccess(stream.Frames[^1].AsSpan(16));
            Check(stream.Frames.Count == 9 && removed.Skills is [{ Id: 200, Level: 0, Slot: 10 }] &&
                added.Skills is [{ Id: 511, Level: 1, Slot: 10, Command: "030304" }],
                "回落删除与新赠技复用槽位先删后增，保留高编号及指令");
            await SkillProjection.PublishGrowthAsync(context, baseline, committed with { Learning = baseline }, "same", "combos", () => true);
            await SkillProjection.PublishGrowthAsync(context, baseline, updated, "stale", "combos", () => false);
            Check(stream.Frames.Count == 9, "绑定未变及过期成长差量不发送技能包");
            var selectedSecond = baseline with { SelectedTree = 1 };
            var firstOnly = committed with { Learning = selectedSecond with { Trees = [updated.Learning.Trees[0], baseline.Trees[1]] } };
            await SkillProjection.PublishGrowthAsync(context, selectedSecond, firstOnly, "second_page", "combos", () => true);
            var secondDeltas = stream.Frames.Skip(9).Select(frame => SkillPacketsTests.DecodeSuccess(frame.AsSpan(16), allowEmpty: true)).ToArray();
            Check(secondDeltas.Length == 2 && secondDeltas[0].Context == 0 && secondDeltas[1].Context == 1 &&
                secondDeltas[1].Skills.Length == 0 && secondDeltas[1].Sp == baseline.Trees[1].Sp,
                "当前第二树时备用树成长差量最后恢复第二方案，不强制回第一页");
        }
        async Task EntryRejectionAsync()
        {
            using var f = new Fixture(towns, characters, learning, quests, false);
            f.Session.BeginDungeonSelection();
            var previous = f.Session.Selected!;
            var selection = f.Session.CurrentDungeonSelection;
            long epoch = f.Session.SelectionEpoch;
            string persisted = JsonSerializer.Serialize(f.Read());
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput();
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(6, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            var request = new Packet(1, 16, Convert.FromHexString("030000000100000000FFFF0000000000490C00000000000000"));
            Check(await Registry(f).DispatchAsync(context, request) == CommandResult.Handled &&
                stream.Frames is [{ Length: 20 } frame] && frame[0] == 1 &&
                BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 16 &&
                frame.AsSpan(16).SequenceEqual(new byte[] { 0, 9, 0, 0 }),
                "入场拒绝仅发送完整静默ACK16，不伪造初始化或自动回城通知");
            Check(f.Session.Phase == SessionPhase.MapSelect && ReferenceEquals(f.Session.Selected, previous) &&
                ReferenceEquals(f.Session.CurrentDungeonSelection, selection) &&
                JsonSerializer.Serialize(f.Read()) == persisted &&
                f.Session.CanPublishFormalDungeonRejection(previous, epoch, SessionPhase.MapSelect, selection),
                "拒绝不预留副本身份，不改变角色与原选图返回锚点");
            Check(f.Session.ReturnFromDungeonSelection() is not null &&
                !f.Session.CanPublishFormalDungeonRejection(previous, epoch, SessionPhase.MapSelect, selection),
                "回城后原入场拒绝回执不能继续发布");
            f.Session.BeginDungeonSelection();
            Check(!f.Session.CanPublishFormalDungeonRejection(previous, epoch, SessionPhase.MapSelect, selection),
                "新选择身份不接受上一次拒绝回执");
            f.Session.ReturnToRoster();
            f.Session.Select(f.Created.Slot);
            f.Session.MarkReady();
            f.Session.BeginDungeonSelection();
            Check(!f.Session.CanPublishFormalDungeonRejection(f.Session.Selected!, epoch, SessionPhase.MapSelect,
                    f.Session.CurrentDungeonSelection) &&
                f.Session.CanPublishFormalDungeonRejection(f.Session.Selected!, f.Session.SelectionEpoch,
                    SessionPhase.MapSelect, f.Session.CurrentDungeonSelection),
                "阶段和当前选择均匹配时，新选角仍不接受旧epoch拒绝回执");
        }
        async Task StorageFailureAsync(bool lostResult)
        {
            using var f = new Fixture(towns, characters, learning, quests, lostResult);
            var (entry, death) = Enter(f);
            var previous = f.Session.Selected!;
            var commands = Registry(f);
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput();
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            if (!lostResult)
                f.Sql("CREATE TRIGGER publication_fail AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'formal publication rollback'); END");
            await RejectAsync(lostResult ? typeof(IOException) : typeof(SqliteException),
                () => commands.DispatchAsync(context, death), "存储故障不伪装Handler成功");
            Check(stream.Frames.Count == 0 && !stop.IsCancellationRequested && ReferenceEquals(f.Session.Selected, previous) &&
                f.Session.DungeonSnapshot!.LivingMonsters.Contains(entry.Monsters.Monsters[0].ObjectKey),
                "存储返回前失败不发死亡/经验，也不提前确认内存死亡");
            if (lostResult)
                Check(f.Count() == 1 && f.Read().Learning.Experience == 104 && f.LostResult!.Calls == 1,
                    "模拟SQL已提交但返回结果丢失，持久角色与receipt真实存在");
            else
            {
                Check(f.Count() == 0 && Same(previous, f.Read()), "真实SQL晚期失败回滚角色和receipt");
                f.Sql("DROP TRIGGER publication_fail");
            }
            Check(await commands.DispatchAsync(context, death) == CommandResult.Handled, "原正文重试恢复原待提交事实");
            Check(f.Count() == 1 && Same(f.Session.Selected!, f.Read()) && f.Read().Revision == previous.Revision + 1 &&
                f.Read().Learning.Experience == 104, "重试不重复经验revision，只安装实际持久角色");
            Check(stream.Opcodes.SequenceEqual(new ushort[] { 38, 37 }) && stream.Frames[1].Length == 16 + 91,
                "明确提交/确认后仅发一次完整死亡与经验通知");
            if (lostResult)
                Check(f.LostResult!.Calls == 2 && f.LostResult.LastDuplicate && f.LostResult.SamePrepared,
                    "结果不明恢复使用同一候选，命中持久receipt只读重放");
            await commands.DispatchAsync(context, death);
            Check(f.Count() == 1 && f.Read().Learning.Experience == 104 && stream.Frames.Count == 2,
                "确认后再次重试只审计忽略，不发第二份经验");
        }

        async Task MaxLevelExperienceNoticeAsync()
        {
            var maximum = CharacterGrowthCatalog.MaximumSupportedLevel;
            var creation = CharacterCreation.Prepare("FormalMax", characters.Get(0), towns.Room, towns.InitialReturn, learning.Get)
                with
                {
                    ActiveMainQuests = [new(0, 3145, 1)],
                    Level = checked((byte)maximum),
                    InitialLearning = new CharacterLearningState(growth.GetLevelStartExperience(maximum),
                        Array.AsReadOnly(new[] { new CharacterSkillTree(0, 3, 4, []), new CharacterSkillTree(1, 8, 5, []) }))
                };
            using var f = new Fixture(towns, characters, learning, quests, false, creation: creation);
            var (_, death) = Enter(f);
            var before = f.Read();
            var commands = Registry(f);
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput();
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(84, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            Check(await commands.DispatchAsync(context, death) == CommandResult.Handled, "满级击杀正常提交");
            var committed = f.Read();
            Check(stream.Opcodes.SequenceEqual(new ushort[] { 38 }),
                "满级击杀只发死亡通知，不再发NOTI37");
            Check(committed.Level == maximum && committed.Learning.Experience == before.Learning.Experience &&
                committed.DungeonState!.ProficiencyExperience > before.DungeonState!.ProficiencyExperience,
                "停发通知不影响满级经验钳制与熟练度实际增长");
        }

        (FormalDungeonReceipt Receipt, DungeonPlannedMonster Boss) PrepareBoss(Fixture f)
        {
            f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms);
            var targets = new (byte X, byte Y)[] { (1, 1), (2, 1), (2, 0), (3, 0), (4, 0) };
            for (int room = 0; room < 6; room++)
            {
                var receipt = f.Session.CompleteFormalDungeonLoading()!;
                if (room == 5)
                    return (receipt, receipt.Monsters.Monsters.Single(monster => monster.MonsterId == 107000903));
                foreach (var monster in receipt.Monsters.Monsters.Where(monster => monster.IsBlocking))
                {
                    var observation = new NativeDungeonDeathObservation(monster.ObjectKey, receipt.Character.Id,
                        0x111, receipt.Character.Id, 600, 1, 0, checked((uint)monster.CandidateLevel), 504, 650, 1,
                        true, new string('C', 64));
                    Check(f.Session.ObserveFormalDungeonDeath(observation, kills, growth).Outcome == DungeonRunOutcome.Accepted,
                        "清图发送故障fixture仅由正式敌方统计/SQL完成死亡（合成输入）");
                }
                Check(f.Session.MoveFormalDungeon(targets[room].X, targets[room].Y) is not null,
                    "清图fixture经实际连续过房资格");
            }
            throw new InvalidOperationException("清图fixture未到Boss房");
        }

        async Task ClearNoticeFailureAsync()
        {
            using var f = new Fixture(towns, characters, learning, quests, false);
            var (receipt, boss) = PrepareBoss(f);
            var selected = f.Session.Selected!;
            var commands = Registry(f);
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput { FailOnOpcode = 31 };
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(82, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            var death = new Packet(1, 39, Death(boss.ObjectKey, receipt.Character.Id, checked((byte)boss.CandidateLevel)));
            await RejectAsync(typeof(IOException), () => commands.DispatchAsync(context, death), "末次清图NOTI31写入失败传播");
            var committed = f.Read();
            var expectedOpcodes = new List<ushort> { 38, 37, 574 };
            if (committed.Level != selected.Level) expectedOpcodes.AddRange([2, 19]);
            else if (SkillPackets.BindingsChanged(selected.Learning, committed.Learning)) expectedOpcodes.Add(19);
            expectedOpcodes.Add(115);
            Check(stop.IsCancellationRequested && f.Count() == 28 && committed.Revision == selected.Revision + 1 &&
                committed.ActiveMainQuests.Single().ConditionState == 0 && Same(committed, f.Session.Selected!) &&
                f.Session.Phase == SessionPhase.Settlement && stream.Opcodes.SequenceEqual(expectedOpcodes) &&
                sender.PendingBytes == 0, "31失败保留同事务成长与任务，只写已成功死亡、经验和Boss帧并释放发送预算");
            await RejectAsync(typeof(TaskCanceledException), () => commands.DispatchAsync(context, death),
                "清图通知失败后取消连接不再执行原报告");
            Check(f.Count() == 28 &&
                stream.Frames.Count == expectedOpcodes.Count && Same(committed, f.Read()), "31通知失败不重跑Boss经验、任务清图或追加奖励");
        }

        async Task ClearedQuestSendFailureAsync()
        {
            using var f = new Fixture(towns, characters, learning, quests, false);
            var (receipt, boss) = PrepareBoss(f);
            var observation = new NativeDungeonDeathObservation(boss.ObjectKey, receipt.Character.Id,
                0x111, receipt.Character.Id, 600, 1, 0, checked((uint)boss.CandidateLevel), 504, 650, 1,
                true, new string('C', 64));
            Check(f.Session.ObserveFormalDungeonDeath(observation, kills, growth).Outcome == DungeonRunOutcome.Accepted,
                "任务发送fixture明确提交最后Boss死亡");
            var completedCondition = f.Read();
            var commands = new GameCommandRegistry();
            new QuestConditionHandler(f.Log).Register(commands);
            commands.Seal();
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput { FailOn = 1 };
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(80, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            var packet = new Packet(1, 33, Convert.FromHexString("2100490C0000"));
            await RejectAsync(typeof(IOException), () => commands.DispatchAsync(context, packet), "已持久清图ACK33发送故障传播");
            Check(stop.IsCancellationRequested && Same(completedCondition, f.Read()) && f.Count() == 28,
                "任务通知失败不重发经验、清图或奖品，不撤销已达成状态");
            f.Session.ReturnToRoster();
            f.Session.Select(f.Created.Slot);
            f.Session.MarkReady();
            var reselected = f.Read();
            using var healthyStop = new CancellationTokenSource();
            using var healthyOutput = new ControlledOutput();
            await using var healthySender = new GameConnectionSender(f.Owner, healthyOutput, healthyStop, 0, (_, _, _, _, _) => { });
            var healthy = new GameConnectionContext(new(81, IPAddress.Loopback, healthyOutput, healthyStop), f.Owner, f.Session, healthySender);
            Check(await commands.DispatchAsync(healthy, packet) == CommandResult.Handled && !healthyStop.IsCancellationRequested &&
                healthyOutput.Frames.Single().AsSpan(16).SequenceEqual(Convert.FromHexString("01490C00000000")) && Same(reselected, f.Read()),
                "重选读取同接取持久事实恢复合法7B任务投影，仍不写库或重复发奖");
        }
        async Task SendFailureAsync(int failedPacket)
        {
            using var f = new Fixture(towns, characters, learning, quests, false);
            var (entry, death) = Enter(f);
            var previous = f.Session.Selected!;
            var commands = Registry(f);
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput { FailOn = failedPacket };
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(2, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            await RejectAsync(typeof(IOException), () => commands.DispatchAsync(context, death), "首/次通知写入失败向上传递");
            Check(stop.IsCancellationRequested && f.Count() == 1 && f.Read().Revision == previous.Revision + 1 &&
                Same(f.Read(), f.Session.Selected!) && !f.Session.DungeonSnapshot!.LivingMonsters.Contains(entry.Monsters.Monsters[0].ObjectKey),
                "发送失败结束连接，已提交经验与死亡确认保持一致");
            Check(stream.Frames.Count == failedPacket - 1 && sender.PendingBytes == 0,
                "部分发送故障只保留已写帧，释放发送预算");
            await RejectAsync(typeof(TaskCanceledException), () => commands.DispatchAsync(context, death),
                "已经取消的连接不再取得执行门禁或处理下一条请求");
            Check(f.Count() == 1 && stream.Frames.Count == failedPacket - 1, "失败后原报告不能再次发奖或补发成功");
            f.Session.End();
            f.Session.Select(f.Created.Slot);
            f.Session.MarkReady();
            Check(Same(f.Read(), f.Session.Selected!) && f.Session.DungeonSnapshot is null &&
                f.Session.Selected!.DungeonState == entry.Character.DungeonState,
                "重登恢复已提交经验和预留身份，丢弃断线实例");
        }
        async Task ReselectDuringSendAsync()
        {
            using var f = new Fixture(towns, characters, learning, quests, false);
            var (_, death) = Enter(f);
            var commands = Registry(f);
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput { HoldOn = 1 };
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(3, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            var publication = commands.DispatchAsync(context, death);
            try
            {
                await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Count() == 1 && Same(f.Read(), f.Session.Selected!) && !publication.IsCompleted,
                    "网络等待期间死亡与经验已提交，不占账号owner锁");
                f.Session.ReturnToRoster();
                f.Session.Select(f.Created.Slot);
                f.Session.MarkReady();
            }
            finally { stream.Release.TrySetResult(); }
            Check(await publication.WaitAsync(TimeSpan.FromSeconds(5)) == CommandResult.Handled &&
                stream.Opcodes.SequenceEqual(new ushort[] { 38 }) && !stop.IsCancellationRequested,
                "重选后允许已经在途的首帧完成，拦截旧epoch后续经验通知");
            Check(f.Session.Phase == SessionPhase.Town && f.Session.DungeonSnapshot is null && f.Count() == 1 && Same(f.Read(), f.Session.Selected!),
                "旧通知收尾不恢复原run、不覆盖新选角或再次保存经验");
        }
        async Task ReturnDuringSendAsync()
        {
            using var f = new Fixture(towns, characters, learning, quests, false);
            var (first, _) = Enter(f);
            var presence = new TownPresence(towns);
            var commands = Registry(f, presence);
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput { HoldOn = 1 };
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(4, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            var publication = commands.DispatchAsync(context, new(1, 42, []));
            FormalDungeonReceipt? second = null;
            try
            {
                await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(f.Session.Phase == SessionPhase.Town && f.Session.DungeonSnapshot is null && !publication.IsCompleted,
                    "正式退出先结束原实例，再等待返回通知");
                f.Session.ReturnToRoster();
                f.Session.Select(f.Created.Slot);
                f.Session.MarkReady();
                Check(f.Session.LeaveRoom() && f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                    "重选后从合法城镇入口再次准入，不能沿用旧副本位置");
                second = f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms);
                Check(second is not null && second.Snapshot.Identity.InstanceId != first.Snapshot.Identity.InstanceId,
                    "旧返回通知等待时新选角已建立另一副本");
            }
            finally { stream.Release.TrySetResult(); }
            Check(await publication.WaitAsync(TimeSpan.FromSeconds(5)) == CommandResult.Handled &&
                stream.Opcodes.SequenceEqual(new ushort[] { 42 }) && !stop.IsCancellationRequested,
                "旧退出仅允许在途ACK结束，禁止向新副本发旧城镇切图");
            Check(presence.Count == 0 && f.Session.Phase == SessionPhase.Loading &&
                f.Session.CanPublishFormalDungeon(second!) && f.Session.Selected!.DungeonState!.LastAttempt == 2,
                "迟到退出不把新副本角色加入城镇同屏，不破坏新run或身份预留");
        }
        async Task CollectionPublicationAsync()
        {
            using var f = new Fixture(towns, characters, learning, quests, false);
            // 明示必命中概率与零波动金币验证生产调用，不改变Runtime资源掉率。
            var pool = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(items.Items.Get(1000), 50) });
            var generation = new DungeonDropGenerationPlan("明确合成生成规则/实际资源模板", [
                new(10000, pool, 3, 3), new(10000, null, 1, 1, new(1, 17, 0))]);
            f.Session.BeginDungeonSelection();
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput();
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(99, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            int prepared = 0;
            var commands = Registry(f, prepareDrops: route => monster =>
            {
                Check(route.Difficulty == 0 && monster.CandidateLevel > 0, "生产选图Handler传递实际逐怪规则输入");
                prepared++;
                return generation;
            });
            byte[] selection = Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000");
            await commands.DispatchAsync(context, new(1, 16, selection));
            Check(prepared > 0 && f.Session.Phase == SessionPhase.Loading, "生产选图Handler装配逐怪规则并建立原正式Run");
            var entry = f.Session.CompleteFormalDungeonLoading()!;
            stream.Frames.Clear();
            await commands.DispatchAsync(context, new(1, 39, Death(entry.Monsters.Monsters[0].ObjectKey, entry.Character.Id, 3)));
            var body = stream.Frames[0].AsSpan(16).ToArray();
            Check(body.Length == 271 && BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2)) == 2 &&
                BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(10)) == 1000 && BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(14)) == 3 &&
                BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(141)) == 0 && BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(145)) == 17 &&
                body.AsSpan(262, 4).SequenceEqual(new byte[4]),
                "生产死亡Handler发布完整物品和金币两行，不截成首件或将位置写入上下文");
            uint itemKey = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
            uint goldKey = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(135));
            var sourceIdentity = f.Session.DungeonSnapshot!.Identity;
            foreach (var monster in entry.Monsters.Monsters.Skip(1).Where(monster => monster.CanReportDeath))
                await commands.DispatchAsync(context, new(1, 39, Death(monster.ObjectKey, entry.Character.Id, checked((byte)monster.CandidateLevel))));
            byte[] move = new byte[100];
            move[0] = 1; move[1] = 1;
            await commands.DispatchAsync(context, new(1, 45, move));
            await RejectPickupAsync(itemKey);
            var adjacent = f.Session.CompleteFormalDungeonLoading()!;
            await RejectPickupAsync(itemKey);
            await RejectPickupAsync(goldKey);
            foreach (var monster in adjacent.Monsters.Monsters.Where(monster => monster.CanReportDeath))
                await commands.DispatchAsync(context, new(1, 39, Death(monster.ObjectKey, entry.Character.Id, checked((byte)monster.CandidateLevel))));
            move[0] = checked((byte)entry.Snapshot.CurrentMap.Position.X);
            move[1] = checked((byte)entry.Snapshot.CurrentMap.Position.Y);
            await commands.DispatchAsync(context, new(1, 45, move));
            Check(stream.Frames[^1].AsSpan(16).Length == 16 && stream.Frames[^1][29] == 0,
                "原物品及金币回访使用缓存 MAP，不重放死亡与掉落创建通知");
            f.Session.CompleteFormalDungeonLoading();
            Check(f.Session.DungeonSnapshot!.Identity.InstanceId == sourceIdentity.InstanceId &&
                f.Session.DungeonSnapshot.Identity.Epoch > sourceIdentity.Epoch,
                "回访仍归原实例，但拾取使用当前加载epoch，不改写原死亡来源");
            int before = stream.Frames.Count;
            byte[] get = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(get, goldKey);
            var currency = new CurrencyService(items.GoldLimits);
            int capacity = currency.CarryLimit(f.Session.Selected!);
            Check(f.Session.MutateInventory(character => currency.Grant(character, CurrencyKind.Gold, capacity), requireTown: false).Accepted,
                "沿正式钱包提交填满真实携带上限，不伪造失败结果");
            var full = f.Read();
            await commands.DispatchAsync(context, new(1, 43, get));
            Check(stream.Frames.Count == before + 1 && stream.Frames[^1].AsSpan(16).SequenceEqual(new byte[] { 0, 22, 0 }) &&
                Same(full, f.Read()), "真实携带上限不足才报22，失败不消费回访金币或写入拾取回执");
            Check(f.Session.MutateInventory(character => CurrencyService.Spend(character.Inventory, CurrencyKind.Gold, capacity), requireTown: false).Accepted,
                "正式恢复携带空间后仍可拾取原冻结金币");
            before = stream.Frames.Count;
            await commands.DispatchAsync(context, new(1, 43, get));
            Check(stream.Opcodes.Skip(before).SequenceEqual(new ushort[] { 39, 14, 43 }) &&
                stream.Frames[before].Length == 71 && stream.Frames[before + 1].Length == 142 &&
                stream.Frames[before + 2].AsSpan(16).SequenceEqual(new byte[] { 1 }),
                "金币生产包序为55B拾取表现通知、实际余额差量与成功ACK");
            var notice = stream.Frames[before].AsSpan(16).ToArray();
            var delta = stream.Frames[before + 1].AsSpan(16).ToArray();
            Check(notice.Length == 55 && BinaryPrimitives.ReadUInt32LittleEndian(notice.AsSpan(7)) == 17 &&
                notice[6] == 1 && notice[11] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(notice.AsSpan(12)) == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(notice.AsSpan(16)) == 0 && notice.AsSpan(20).ToArray().All(value => value == 0) &&
                BinaryPrimitives.ReadUInt16LittleEndian(delta.AsSpan(3)) == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(delta.AsSpan(9)) == f.Read().Inventory.Gold && f.Read().Inventory.Gold == 17,
                "金币增量和虚拟槽0的绝对余额来自同一实际提交");
            BinaryPrimitives.WriteUInt32LittleEndian(get, itemKey);
            await commands.DispatchAsync(context, new(1, 43, get));
            Check(f.Read().Inventory.Items.Single(item => item.Core.ItemId == 1000).Core.Count == 3 &&
                f.Read().Inventory.Gold == 17 && f.Session.Selected!.Inventory.Gold == 17,
                "同死亡第二个key独立拾取三件原物品，不重复授予已取金币");
            Check(f.Read().Inventory.Find(new(InventoryListType.Main, 65))?.Core.ItemId == 1000 &&
                BinaryPrimitives.ReadUInt16LittleEndian(stream.Frames[^3].AsSpan(32)) == 65 &&
                BinaryPrimitives.ReadUInt16LittleEndian(stream.Frames[^2].AsSpan(19)) == 65,
                "集合拾取的NOTI39和库存差量均使用已提交背包槽65");
            var actual = f.Read();
            int sent = stream.Frames.Count;
            await commands.DispatchAsync(context, new(1, 43, get));
            Check(stream.Frames.Count == sent + 1 && stream.Frames[^1].AsSpan(16).SequenceEqual(new byte[] { 0, 19, 0 }) && Same(actual, f.Read()),
                "集合生产重复拾取只拒绝一次，实际库存与金币保持不变");

            async Task RejectPickupAsync(uint key)
            {
                var old = f.Read();
                int count = stream.Frames.Count;
                byte[] request = new byte[20];
                BinaryPrimitives.WriteUInt32LittleEndian(request, key);
                await commands.DispatchAsync(context, new(1, 43, request));
                Check(stream.Frames.Count == count + 1 && stream.Frames[^1].AsSpan(16).SequenceEqual(new byte[] { 0, 19, 0 }) &&
                    Same(old, f.Read()), "加载或其他房间拾取仅静默收尾，不报背包满，不消费旧房金币或物品");
            }
        }

        async Task DiscardPublicationAsync(bool loseResult)
        {
            using var f = new Fixture(towns, characters, learning, quests, loseResult, losePickup: loseResult, loseDiscard: loseResult);
            var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
            Check(f.Session.MutateInventory(character => inventory.Grant(character, [new(1000, 8)])).Accepted,
                "丢弃测试只由正式发放创建完整库存实例");
            var source = f.Session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 1000);
            var (entry, _) = Enter(f);
            var before = f.Session.Selected!;
            long beforeAudits = f.Count("inventory_audit_log");
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput();
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(101, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            var commands = Registry(f, inventoryActions: inventory);
            byte[] discard = new byte[12];
            BinaryPrimitives.WriteUInt16LittleEndian(discard, 1453);
            BinaryPrimitives.WriteUInt16LittleEndian(discard.AsSpan(2), 266);
            BinaryPrimitives.WriteUInt16LittleEndian(discard.AsSpan(5), checked((ushort)source.Position.Index));
            BinaryPrimitives.WriteUInt32LittleEndian(discard.AsSpan(7), 3);
            if (loseResult)
                await RejectAsync(typeof(IOException), () => commands.DispatchAsync(context, new(1, 47, discard)),
                    "丢弃已提交但结果丢失，不提前发布成功或创建通知");
            else
            {
                f.Sql("CREATE TRIGGER discard_final_fail AFTER UPDATE OF next_object_key ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'discard rollback'); END");
                await RejectAsync(typeof(SqliteException), () => commands.DispatchAsync(context, new(1, 47, discard)),
                    "丢弃末段SQL失败回滚扣物、已有key高水位和库存审计");
                Check(Same(before, f.Read()) && f.Count("inventory_audit_log") == beforeAudits,
                    "SQL失败不留下半份库存或地面来源");
                f.Sql("DROP TRIGGER discard_final_fail");
            }
            Check(ReferenceEquals(before, f.Session.Selected) && stream.Frames.Count == 0 &&
                f.Session.ReturnFromFormalDungeon() is null && f.Session.MoveFormalDungeon(1, 1) is null &&
                !f.Session.MutateInventory(character => inventory.Grant(character, [new(1000, 1)]), requireTown: false).Accepted,
                "结果未确定时保留原准备，不允许其他业务覆盖库存或退出实例");
            await commands.DispatchAsync(context, new(1, 47, discard));
            var ack = stream.Frames[0].AsSpan(16).ToArray();
            var notice = stream.Frames[1].AsSpan(16).ToArray();
            Check(stream.Opcodes.Take(3).SequenceEqual(new ushort[] { 47, 40, 14 }) && ack.Length == 8 &&
                ack[0] == 1 && ack[1] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(2)) == source.Position.Index &&
                BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4)) == 3 && notice.Length == 136 &&
                BinaryPrimitives.ReadUInt16LittleEndian(notice) == before.Id &&
                BinaryPrimitives.ReadUInt16LittleEndian(notice.AsSpan(2)) == 1453 &&
                BinaryPrimitives.ReadUInt16LittleEndian(notice.AsSpan(4)) == 266 &&
                BinaryPrimitives.ReadInt32LittleEndian(notice.AsSpan(16)) == 3,
                "生产47成功按8B实际扣量、136B完整地面通知、源库存差量顺序发布");
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(notice.AsSpan(6));
            Check(f.Read().Inventory.Find(source.Position) == (source with { Core = source.Core with { Value = 5 } }) &&
                f.Read().DungeonState!.NextObjectKey == key + 1 && f.Count("inventory_audit_log") == beforeAudits + 1 && f.Count() == 0,
                "部分丢弃保留源UID和完整核心，地面单独预留key，不伪造怪物死亡、经验或掉落父账本");
            foreach (var monster in entry.Monsters.Monsters.Where(monster => monster.CanReportDeath))
                await commands.DispatchAsync(context, new(1, 39, Death(monster.ObjectKey, entry.Character.Id, checked((byte)monster.CandidateLevel))));
            byte[] move = new byte[100];
            move[0] = 1; move[1] = 1;
            await commands.DispatchAsync(context, new(1, 45, move));
            byte[] get = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(get, key);
            await commands.DispatchAsync(context, new(1, 43, get));
            Check(stream.Frames[^1].AsSpan(16).SequenceEqual(new byte[] { 0, 19, 0 }),
                "丢弃物的加载中拾取静默收尾，不误报满包或跨房授予");
            var adjacent = f.Session.CompleteFormalDungeonLoading()!;
            foreach (var monster in adjacent.Monsters.Monsters.Where(monster => monster.CanReportDeath))
                await commands.DispatchAsync(context, new(1, 39, Death(monster.ObjectKey, entry.Character.Id, checked((byte)monster.CandidateLevel))));
            move[0] = checked((byte)entry.Snapshot.CurrentMap.Position.X);
            move[1] = checked((byte)entry.Snapshot.CurrentMap.Position.Y);
            await commands.DispatchAsync(context, new(1, 45, move));
            Check(stream.Frames[^1].AsSpan(16).Length == 16 && stream.Frames[^1][29] == 0,
                "丢弃后回访原房只恢复MAP缓存，不重新下发40或丢弃源");
            f.Session.CompleteFormalDungeonLoading();
            var beforePickup = f.Session.Selected!;
            long beforePickupAudits = f.Count("inventory_audit_log");
            int sent = stream.Frames.Count;
            f.Sql("CREATE TRIGGER discard_pickup_fail AFTER INSERT ON inventory_audit_log WHEN NEW.action_name LIKE 'dungeon_inventory_pickup:%' BEGIN SELECT RAISE(ABORT,'pickup rollback'); END");
            await RejectAsync(typeof(SqliteException), () => commands.DispatchAsync(context, new(1, 43, get)),
                "回访丢弃拾取的末段回执失败回滚库存和真实UID");
            Check(Same(beforePickup, f.Read()) && ReferenceEquals(beforePickup, f.Session.Selected) && stream.Frames.Count == sent &&
                f.Count("inventory_audit_log") == beforePickupAudits, "拾取SQL失败仍保留原地面准备，不发布半份成功");
            f.Sql("DROP TRIGGER discard_pickup_fail");
            if (loseResult)
            {
                await RejectAsync(typeof(IOException), () => commands.DispatchAsync(context, new(1, 43, get)),
                    "丢弃拾取已提交但返回丢失，既有审计可恢复原准备");
                Check(ReferenceEquals(beforePickup, f.Session.Selected) && stream.Frames.Count == sent &&
                    f.Read().Inventory.Find(source.Position) == source, "结果不明不提前确认内存拾取或发送成功");
            }
            await commands.DispatchAsync(context, new(1, 43, get));
            Check(f.Read().Inventory.Find(source.Position) == source && f.Count("inventory_audit_log") == beforePickupAudits + 1 &&
                stream.Opcodes.Skip(sent).SequenceEqual(new ushort[] { 39, 14, 43 }),
                "当前epoch拾取原丢弃核心，合并回原UID且数量8，已有审计和发布顺序正确");
            var actual = f.Read();
            await commands.DispatchAsync(context, new(1, 43, get));
            Check(Same(actual, f.Read()) && stream.Frames[^1].AsSpan(16).SequenceEqual(new byte[] { 0, 19, 0 }),
                "重复丢弃拾取不增加物品或写入第二份回执");
            BinaryPrimitives.WriteUInt32LittleEndian(discard.AsSpan(7), 8);
            sent = stream.Frames.Count;
            await commands.DispatchAsync(context, new(1, 47, discard));
            uint wholeKey = BinaryPrimitives.ReadUInt32LittleEndian(stream.Frames[sent + 1].AsSpan(22));
            Check(f.Read().Inventory.Find(source.Position) is null && wholeKey != key && f.Count("inventory_audit_log") == beforePickupAudits + 2,
                "整堆丢弃清源槽并分配新key，不把合法第二次操作当重放");
            BinaryPrimitives.WriteUInt32LittleEndian(get, wholeKey);
            await commands.DispatchAsync(context, new(1, 43, get));
            var restored = f.Read().Inventory.Items.Single(item => item.Core.ItemId == 1000);
            Check(restored.Uid != source.Uid && restored.Core == source.Core && restored.Position == new InventorySlot(InventoryListType.Main, 65) &&
                BinaryPrimitives.ReadUInt16LittleEndian(stream.Frames[^3].AsSpan(32)) == 65 &&
                BinaryPrimitives.ReadUInt16LittleEndian(stream.Frames[^2].AsSpan(19)) == 65 && f.Count("inventory_audit_log") == beforePickupAudits + 3,
                "源整堆已删除时，拾取分配新持久UID但完整核心、期限、属性与数量不重生成");
            f.Session.ReturnToRoster();
            f.Session.Select(f.Created.Slot);
            Check(f.Session.Selected!.Inventory.Find(restored.Position) == restored,
                "重选及仓储重开保留已拾取丢弃物实际UID和全部资产属性");
        }

        async Task PickupLostResultAsync()
        {
            using var f = new Fixture(towns, characters, learning, quests, true, losePickup: true);
            bool dropping = false;
            // 本例精确核对普通单件掉落封包，入场不提升精英；随后保留原可控命中。
            var entry = f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms, global::DfoServer.Testing.SelfTestCatalogs.MonsterDrops(pvf), items.Items,
                bound => dropping ? 0 : bound - 1)!;
            dropping = true;
            f.Session.CompleteFormalDungeonLoading();
            var commands = Registry(f);
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput();
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(5, IPAddress.Loopback, stream, stop), f.Owner, f.Session, sender);
            await commands.DispatchAsync(context, new(1, 39, Death(entry.Monsters.Monsters[0].ObjectKey, entry.Character.Id, 3)));
            Check(stream.Frames[0].Length == 156 && stream.Frames[0].AsSpan(147, 4).SequenceEqual(new byte[4]) &&
                entry.Monsters.Monsters[0].Source!.RawValues[4] != 0,
                "真实死亡Handler的可控随机命中投影零创建上下文，不将非零资源Y写成客户端可见性过滤");
            var old = f.Session.Selected!;
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(stream.Frames[0].AsSpan(20));
            byte[] get = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(get, key);
            int sentBefore = stream.Frames.Count;
            await RejectAsync(typeof(IOException), () => commands.DispatchAsync(context, new(1, 43, get)),
                "拾取真实SQL已提交但返回值丢失，Handler不伪成功");
            Check(ReferenceEquals(old, f.Session.Selected) && f.Read().Inventory.Items.Single(item => item.Core.ItemId == 1000).Core.Count == 1 &&
                stream.Frames.Count == sentBefore && f.Session.ReturnFromFormalDungeon() is null,
                "未知拾取结果保留原准备和在线旧快照，已持久物品存在但不提前通知");
            Check(await commands.DispatchAsync(context, new(1, 43, get)) == CommandResult.Handled && f.LostResult!.PickupCalls == 2 && f.LostResult.PickupDuplicate &&
                Same(f.Read(), f.Session.Selected!) && f.Read().Inventory.Items.Single(item => item.Core.ItemId == 1000).Core.Count == 1,
                "原key恢复命中持久receipt，只安装实际角色，不加第二件");
            Check(f.Read().Inventory.Find(new(InventoryListType.Main, 65)) is { Uid: > 0, Core.ItemId: 1000, Core.Count: 1 } &&
                BinaryPrimitives.ReadUInt16LittleEndian(stream.Frames[sentBefore].AsSpan(32)) == 65 &&
                BinaryPrimitives.ReadUInt16LittleEndian(stream.Frames[sentBefore + 1].AsSpan(19)) == 65,
                "普通单件拾取丢失结果恢复后，真实背包槽与两种通知目标一致");
            Check(stream.Opcodes.Skip(sentBefore).SequenceEqual(new ushort[] { 39, 14, 43 }),
                "原地面转移通知先于最终库存快照，成功ACK只收尾一次");
        }
    }

    // 独立编码本版88B合成统计，故障测试通过正式Parser/Handler进入业务。
    static byte[] Death(ushort key, ushort reporter, byte level)
    {
        byte[] bytes = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, key);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), reporter);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(18), 650);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(26), 1);
        bytes[30] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(31), 0x111);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(33), reporter);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(35), 600);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(43), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), level);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(60), 504);
        byte[] digest = MD5.HashData(bytes.AsSpan(0, 84));
        bytes[84] = (byte)(digest[0] ^ digest[13] ^ digest[8] ^ digest[10] ^ 0x81);
        bytes[85] = (byte)(digest[1] ^ digest[9] ^ digest[5] ^ digest[12] ^ 0x78);
        bytes[86] = (byte)(digest[2] ^ digest[6] ^ digest[14] ^ digest[4] ^ 0x1A);
        bytes[87] = (byte)(digest[3] ^ digest[15] ^ digest[11] ^ digest[7] ^ 0xBF);
        return bytes;
    }
    static void Check(bool condition, string name) => TestCheck.That(condition, name);
    static bool Same(Character first, Character second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    static async Task RejectAsync(Type expected, Func<Task> action, string name)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (expected.IsInstanceOfType(ex)) { Check(true, name); return; }
        throw new InvalidOperationException("FAIL 未遇到预期故障: " + name);
    }
    sealed class ControlledOutput : MemoryStream
    {
        public int FailOn { get; init; }
        public ushort? FailOnOpcode { get; init; }
        public int HoldOn { get; init; }
        int writes;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<byte[]> Frames { get; } = [];
        public IEnumerable<ushort> Opcodes => Frames.Select(frame => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)));
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int number = ++writes;
            if (number == HoldOn)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            if (number == FailOn || BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[1..]) == FailOnOpcode)
                throw new IOException("合成正式副本通知写入故障");
            Frames.Add(buffer.ToArray());
        }
    }
    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-formal-publication-" + Guid.NewGuid().ToString("N"));
        readonly string path;
        readonly CharacterRepository repository;
        public AccountOwners.Lease Owner { get; }
        public GameSession Session { get; }
        public Character Created { get; }
        public ServerLog Log { get; }
        public LostResultRepository? LostResult { get; }
        public Fixture(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog learning, DfoServer.GameWorld.Quests.QuestCatalog quests, bool lostResult,
            bool losePickup = false, bool loseDiscard = false, NewCharacter? creation = null)
        {
            path = Path.Combine(root, "game.db");
            repository = new(path, quests);
            Owner = new AccountOwners().Acquire("formal-publication", () => { });
            LostResult = lostResult ? new(repository, loseGrowth: !losePickup && !loseDiscard, losePickup: losePickup, loseDiscard: loseDiscard) : null;
            Session = new(LostResult is null ? repository : LostResult, towns, Owner);
            Created = repository.Create(Owner, creation ?? CharacterCreation.Prepare("FormalSend", characters.Get(0), towns.Room, towns.InitialReturn, learning.Get)
                with { ActiveMainQuests = [new(0,3145,1)] });
            Session.Select(Created.Slot);
            Session.MarkReady();
            Check(Session.LeaveRoom() && Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "通知fixture由合法城镇入口准备");
            Log = new(Path.Combine(root, "logs"));
        }
        public Character Read() => repository.LoadCharacter(Owner, Created.Slot);
        public long Count(string table = "character_dungeon_growth_receipts")
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT count(*) FROM " + table;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        public void Sql(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            Log.Dispose();
            Owner.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
    // 故障注入只丢弃真实仓储第一次提交后的返回值，不伪造SQL结果或领域确认。
    internal sealed class LostResultRepository(ICharacterRepository inner, bool loseGrowth = true, bool losePickup = false,
        bool loseSettlement = false,bool losePaid = false,bool loseDiscard = false,bool loseFree = false) : ICharacterRepository
    {
        public Character ResetDailyFatigue(AccountOwners.Lease owner, Character expected, int day, DateTimeOffset now) =>
            inner.ResetDailyFatigue(owner, expected, day, now);
        public System.Collections.Immutable.ImmutableDictionary<int,AccountItemRefillState> ReadAccountItemRefills(AccountOwners.Lease owner) => inner.ReadAccountItemRefills(owner);
        public ServerItemCycleCommit CommitServerItemCycle(AccountOwners.Lease owner, Character expected, InventoryPlan plan) => inner.CommitServerItemCycle(owner, expected, plan);
        public System.Collections.Immutable.ImmutableArray<StoredDungeonIncount> LoadDungeonIncounts(AccountOwners.Lease owner, ushort characterId) => inner.LoadDungeonIncounts(owner, characterId);
        public Character CommitPermanentGrowth(AccountOwners.Lease owner, PermanentGrowthPlan plan, DateTimeOffset now) =>
            inner.CommitPermanentGrowth(owner, plan, now);
        public Character CommitLegacyStatusConsumable(AccountOwners.Lease owner, LegacyStatusConsumablePlan plan, DateTimeOffset now) =>
            inner.CommitLegacyStatusConsumable(owner, plan, now);
        public Character CommitFatigueConsumable(AccountOwners.Lease owner, FatigueConsumablePlan plan, DateTimeOffset now) =>
            inner.CommitFatigueConsumable(owner, plan, now);
        public Character CommitExperienceConsumable(AccountOwners.Lease owner, ExperienceConsumablePlan plan, DateTimeOffset now) =>
            inner.CommitExperienceConsumable(owner, plan, now);
        public string ReadAdventureName(AccountOwners.Lease owner) => inner.ReadAdventureName(owner);
        public bool AdventureNameExists(AccountOwners.Lease owner, string name) => inner.AdventureNameExists(owner, name);
        public DfoServer.Game.Adventure.AdventureNameCommit CommitAdventureName(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureNamePlan plan,DfoServer.GameWorld.Adventure.AdventureCatalog catalog,DateTimeOffset now) =>
            inner.CommitAdventureName(owner,plan,catalog,now);
        public DfoServer.Game.Adventure.AdventureSnapshot CommitAdventureExpedition(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureExpeditionPlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.CommitAdventureExpedition(owner, plan, catalog, now);
        public DfoServer.Game.Adventure.AdventureShopCommit CommitAdventureShop(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureShopPlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.CommitAdventureShop(owner, plan, catalog, now);
        public DfoServer.Game.Adventure.AdventureCapsuleCommit CommitAdventureCapsule(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureCapsulePlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.CommitAdventureCapsule(owner, plan, catalog, now);
        public DfoServer.Game.Adventure.AdventureSnapshot CommitAdventureRepresentative(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureRepresentativePlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.CommitAdventureRepresentative(owner, plan, catalog, now);
        public DfoServer.Game.Adventure.AdventureSnapshot ReadAdventure(AccountOwners.Lease owner, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.ReadAdventure(owner, catalog, now);
        public int ReadAdventureLevel(AccountOwners.Lease owner) => inner.ReadAdventureLevel(owner);
        public SelectionContentsState ReadSelectionContents(AccountOwners.Lease owner, CharacterRosterEntry? expected, DateTimeOffset now) => inner.ReadSelectionContents(owner, expected, now);
        public System.Collections.Immutable.ImmutableArray<DfoServer.Game.Adventure.AdventureWeeklyCharacter> ReadAdventureWeeklyRoster(
            AccountOwners.Lease owner, int week) => inner.ReadAdventureWeeklyRoster(owner, week);
        public System.Collections.Immutable.ImmutableArray<byte> ReadAdventureWeeklyConfig(AccountOwners.Lease owner) => inner.ReadAdventureWeeklyConfig(owner);
        public void WriteAdventureWeeklyConfig(AccountOwners.Lease owner, System.Collections.Immutable.ImmutableArray<byte> slots) => inner.WriteAdventureWeeklyConfig(owner, slots);
        public void InitializeAdventure(AccountOwners.Lease owner, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.InitializeAdventure(owner, catalog, now);
        public DfoServer.Game.Tutorials.TutorialCheckpointResult CommitTutorialCheckpoint(AccountOwners.Lease owner,
            DfoServer.Game.Tutorials.TutorialCheckpointPlan plan) => inner.CommitTutorialCheckpoint(owner, plan);
        public Character CommitTitleAchievement(AccountOwners.Lease owner, DfoServer.Game.Titles.TitleAchievementPlan plan) => inner.CommitTitleAchievement(owner, plan);
        public InventorySnapshot ReadPublicTitleBook(AccountOwners.Lease owner, ushort characterId) => inner.ReadPublicTitleBook(owner, characterId);
        public System.Collections.Immutable.ImmutableArray<uint> CommitQuestEligibilityMail(AccountOwners.Lease owner, QuestEligibilityMailPlan plan) => inner.CommitQuestEligibilityMail(owner,plan);
        public Character AdvanceQuestCycle(AccountOwners.Lease owner, Character expected, int day, QuestAutoDailyPlan? daily = null) => inner.AdvanceQuestCycle(owner, expected, day, daily);
        public CardMembership LoadCardMembership(AccountOwners.Lease owner,ushort id,DateTimeOffset now) => inner.LoadCardMembership(owner,id,now);
        public CardMembership SetCardMembership(AccountOwners.Lease owner,string name,CardMembershipKind kind,long expires) => inner.SetCardMembership(owner,name,kind,expires);
        public Character GrantBlackDiamondFatigue(AccountOwners.Lease owner, Character expected, DateTimeOffset now) => inner.GrantBlackDiamondFatigue(owner, expected, now);
        public DungeonPaidCardCommit CommitPaidDungeonCard(AccountOwners.Lease owner,Character expected,PreparedDungeonSettlement prepared,DungeonCardDelivery delivery,DateTimeOffset now)
        {
            var result = inner.CommitPaidDungeonCard(owner,expected,prepared,delivery,now);
            if (result.Accepted && ++paidCalls == 1 && losePaid) throw new IOException("合成付费牌已提交但结果返回丢失");
            return result;
        }
        public DungeonFreeCardCommit CommitFreeDungeonCard(AccountOwners.Lease owner, Character expected,
            PreparedDungeonSettlement prepared, DungeonCardDelivery delivery)
        {
            var result = inner.CommitFreeDungeonCard(owner, expected, prepared, delivery);
            if (++freeCalls == 1 && loseFree) throw new IOException("合成免费牌已提交但结果返回丢失");
            return result;
        }
        public OperationHopeRewardCommit CommitOperationHopeReward(AccountOwners.Lease owner, OperationHopeRewardPlan plan) => inner.CommitOperationHopeReward(owner,plan);
        public Character ClearOperationHopeAcceptances(AccountOwners.Lease owner, Character expected,
            DfoServer.GameWorld.Dungeons.OperationHopeCatalog catalog, IReadOnlyList<ActiveMainQuest> removals) => inner.ClearOperationHopeAcceptances(owner,expected,catalog,removals);
        public Character ConsumeOperationHopeAreaFatigue(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run) => inner.ConsumeOperationHopeAreaFatigue(owner,expected,run);
        public Character ConsumeDungeonRoomFatigue(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run) =>
            inner.ConsumeDungeonRoomFatigue(owner, expected, run);
        PreparedDungeonDeath? first;
        public int Calls { get; private set; }
        int settlementCalls;
        int paidCalls;
        int freeCalls;
        public bool LastDuplicate { get; private set; }
        public bool SamePrepared { get; private set; }
        public int PickupCalls { get; private set; }
        public int DiscardCalls { get; private set; }
        public Character CommitDungeonDiscard(AccountOwners.Lease owner, DungeonInventoryDiscardPreparation prepared)
        {
            var result = inner.CommitDungeonDiscard(owner, prepared);
            if (++DiscardCalls == 1 && loseDiscard) throw new IOException("合成丢弃已提交但返回结果丢失");
            return result;
        }
        public bool PickupDuplicate { get; private set; }
        public DungeonGrowthCommit CommitDungeonGrowth(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run,
            PreparedDungeonDeath prepared, DungeonExperienceReward reward, CharacterGrowthCatalog catalog, FrozenDungeonGroundDeath? loot = null, DfoServer.Game.Quests.QuestDropPlan? questDrops = null, DfoServer.Game.Inventory.Services.EquipmentGrowthPlan? equipmentGrowth = null)
        {
            Calls++;
            first ??= prepared;
            SamePrepared = ReferenceEquals(first, prepared);
            var result = inner.CommitDungeonGrowth(owner, expected, run, prepared, reward, catalog, loot, questDrops, equipmentGrowth);
            LastDuplicate = result.Duplicate;
            if (Calls == 1 && loseGrowth) throw new IOException("合成SQL已提交但结果丢失");
            return result;
        }
        public AccountWallet LoadAccountWallet(AccountOwners.Lease owner) => inner.LoadAccountWallet(owner);
        public CharacterRosterEntry[] ListRoster(AccountOwners.Lease owner) => inner.ListRoster(owner);
        public DfoServer.Game.Quests.QuestStageState LoadQuestStageState(AccountOwners.Lease owner, Character expected) => inner.LoadQuestStageState(owner, expected);
        public Character LoadCharacter(AccountOwners.Lease owner, ushort slot) => inner.LoadCharacter(owner, slot);
        public Character[] List(AccountOwners.Lease owner) => inner.List(owner);
        public bool NameExists(string name) => inner.NameExists(name);
        public bool CanCreatePvpPrivate(AccountOwners.Lease owner, byte job, byte grow, DfoServer.Game.Pvp.PvpPrivateCharacterCreation creation) => inner.CanCreatePvpPrivate(owner,job,grow,creation);
        public Character Create(AccountOwners.Lease owner, NewCharacter character) => inner.Create(owner, character);
        public CharacterDeletion LoadDeletionTarget(AccountOwners.Lease owner, ushort slot) => inner.LoadDeletionTarget(owner, slot);
        public CharacterDeletion Delete(AccountOwners.Lease owner, CharacterDeletion expected) => inner.Delete(owner, expected);
        public void SwapSlots(AccountOwners.Lease owner, ushort slotA, ushort slotB) => inner.SwapSlots(owner, slotA, slotB);
        internal Action? BeforeSave { get; set; }
        public Character Save(AccountOwners.Lease owner, Character expected, Location position, Location? back, InventoryPlan? inventory = null)
        { BeforeSave?.Invoke(); return inner.Save(owner, expected, position, back, inventory); }
        public Character CommitTeleport(AccountOwners.Lease owner, DfoServer.Game.Town.TeleportPlan plan) => inner.CommitTeleport(owner, plan);
        public DfoServer.Game.Town.GuildMapTeleportCommit CommitGuildMapTeleport(AccountOwners.Lease owner,
            DfoServer.Game.Town.TeleportPlan plan, string requestId) => inner.CommitGuildMapTeleport(owner, plan, requestId);
        public DfoServer.Game.Dungeons.AssaultDailyCounts LoadAssaultDaily(AccountOwners.Lease owner, ushort characterId, int day) => inner.LoadAssaultDaily(owner, characterId, day);
        public DfoServer.Game.Dungeons.AssaultDailyCommit CommitAssaultClear(AccountOwners.Lease owner, Character expected, int day, bool hidden, DfoServer.Game.Inventory.Models.InventoryPlan? reward) => inner.CommitAssaultClear(owner, expected, day, hidden, reward);
        public DfoServer.Game.Dungeons.CrackDailyState LoadCrackDaily(AccountOwners.Lease owner, ushort characterId, int day) => inner.LoadCrackDaily(owner, characterId, day);
        public DfoServer.Game.Dungeons.CrackDailyCommit CommitCrackClear(AccountOwners.Lease owner, Character expected, int day, DfoServer.Game.Dungeons.CrackDailyState state, DfoServer.Game.Inventory.Models.InventoryPlan? reward) => inner.CommitCrackClear(owner, expected, day, state, reward);
        public Character CommitLearning(AccountOwners.Lease owner, Character expected, CharacterLearningState learning, int? totalSp = null, int? totalTp = null) => inner.CommitLearning(owner, expected, learning, totalSp, totalTp);
        public Character CommitSkillLayout(AccountOwners.Lease owner, Character expected, CharacterLearningState learning) => inner.CommitSkillLayout(owner, expected, learning);
        public Character UpdateProgress(AccountOwners.Lease owner, CharacterProgressEdit edit) => inner.UpdateProgress(owner, edit);
        public Character AdvanceAcceptedStoryLevel(AccountOwners.Lease owner, Character expected) => inner.AdvanceAcceptedStoryLevel(owner, expected);
        public Character ResetProgress(AccountOwners.Lease owner, CharacterProgressResetPlan plan) => inner.ResetProgress(owner, plan);
        public Character CommitClassChange(AccountOwners.Lease owner, ClassChangePlan plan) => inner.CommitClassChange(owner, plan);
        public int LoadGrowUpChangeCount(AccountOwners.Lease owner, ushort characterId) => inner.LoadGrowUpChangeCount(owner, characterId);
        public GrowUpChangeCommit CommitGrowUpChange(AccountOwners.Lease owner, GrowUpChangePlan plan) => inner.CommitGrowUpChange(owner, plan);
        public Character CommitExpertJobLearning(AccountOwners.Lease owner, DfoServer.Game.ExpertJob.ExpertJobLearningPlan plan) => inner.CommitExpertJobLearning(owner, plan);
        public DfoServer.Game.ExpertJob.ExpertJobTradeCommit CommitExpertJobTrade(AccountOwners.Lease customerLease, AccountOwners.Lease ownerLease, DfoServer.Game.ExpertJob.ExpertJobTradePlan plan) => inner.CommitExpertJobTrade(customerLease, ownerLease, plan);
        public (Character First, Character Second) CommitPlayerTrade(AccountOwners.Lease firstOwner, AccountOwners.Lease secondOwner, Character first, Character second, DfoServer.Game.Trade.TradeTicket ticket, DfoServer.Game.Trade.TradeSettlementPlan plan) => inner.CommitPlayerTrade(firstOwner, secondOwner, first, second, ticket, plan);
        public Character CommitExpertJobAction(AccountOwners.Lease owner, DfoServer.Game.ExpertJob.ExpertJobActionPlan plan) => inner.CommitExpertJobAction(owner, plan);
        public Character CommitExpertJobClear(AccountOwners.Lease owner, Character expected) => inner.CommitExpertJobClear(owner, expected);
        public Character CommitExpertJobCraft(AccountOwners.Lease owner, Character expected, InventoryPlan inventory, int experience, QuestLifecyclePlan? tutorial = null) => inner.CommitExpertJobCraft(owner, expected, inventory, experience, tutorial);
        public Character CommitExpertJobExperience(AccountOwners.Lease owner, Character expected, int experience) => inner.CommitExpertJobExperience(owner, expected, experience);
        public Character CommitExpertJobGiveUp(AccountOwners.Lease owner, Character expected, InventoryPlan wallet, int giveUpState, CharacterLearningState learning,
            IReadOnlyList<int>? clearQuests = null) => inner.CommitExpertJobGiveUp(owner, expected, wallet, giveUpState, learning, clearQuests);
        public Character CommitExpertJobMachine(AccountOwners.Lease owner, Character expected, InventoryPlan wallet, int grade, int endurance) => inner.CommitExpertJobMachine(owner, expected, wallet, grade, endurance);
        public Character ReserveDungeonAttempt(AccountOwners.Lease owner, Character expected, uint nextObjectKey,
            DfoServer.Game.Dungeons.WeeklyDungeonEntry? weekly = null) => inner.ReserveDungeonAttempt(owner, expected, nextObjectKey, weekly);
        public DfoServer.Game.Dungeons.DungeonEntryResetResult ResetDungeonEntries(AccountOwners.Lease owner) =>
            inner.ResetDungeonEntries(owner);
        public DfoServer.Game.Dungeons.DungeonEntryCommit ReserveDungeonEntry(AccountOwners.Lease owner, Character expected, uint nextObjectKey,
            DfoServer.Game.Dungeons.WeeklyDungeonEntry? weekly = null) => inner.ReserveDungeonEntry(owner, expected, nextObjectKey, weekly);
        public DfoServer.Game.Dungeons.DungeonEntryCommit CommitDungeonEntry(AccountOwners.Lease owner, Character expected, uint nextObjectKey, DfoServer.Game.Dungeons.DungeonEntryCharge charge) => inner.CommitDungeonEntry(owner, expected, nextObjectKey, charge);
        public DfoServer.Game.Guild.GuildDungeonAdmission? PrepareGuildDungeonEntry(AccountOwners.Lease owner, ushort characterId,
            uint dungeonId, DfoServer.GameWorld.Guilds.GuildDungeonCatalog catalog) => inner.PrepareGuildDungeonEntry(owner, characterId, dungeonId, catalog);
        public int ReadGuildDungeonPartyCount(AccountOwners.Lease owner, ushort characterId,
            IReadOnlyList<DfoServer.Game.Guild.GuildDungeonMembership> frozen, IReadOnlyCollection<ushort> active) =>
            inner.ReadGuildDungeonPartyCount(owner, characterId, frozen, active);
        public Guid? LoadOrdinaryLukeDropEvent(AccountOwners.Lease owner, ushort characterId, int day, int sourceMonsterId) => inner.LoadOrdinaryLukeDropEvent(owner, characterId, day, sourceMonsterId);
        public DfoServer.Game.Dungeons.EkernCycleProgress LoadEkernCycle(AccountOwners.Lease owner, ushort characterId, int day) => inner.LoadEkernCycle(owner,characterId,day);
        public DfoServer.Game.Dungeons.OrdinaryLukeProgress LoadOrdinaryLukeProgress(AccountOwners.Lease owner, ushort characterId) => inner.LoadOrdinaryLukeProgress(owner, characterId);
        public DfoServer.Game.Dungeons.PreyNormalProgress LoadPreyNormalProgress(AccountOwners.Lease owner, ushort characterId) => inner.LoadPreyNormalProgress(owner, characterId);
        public DfoServer.Game.Dungeons.DazzlementTowerState LoadDazzlementTower(AccountOwners.Lease owner) => inner.LoadDazzlementTower(owner);
        public DfoServer.Game.Dungeons.GriefTowerProgress ReadGriefTower(AccountOwners.Lease owner, ushort characterId) => inner.ReadGriefTower(owner, characterId);
        public DfoServer.Game.Dungeons.DungeonSpecialEntryUsage LoadSpecialEntryUsage(AccountOwners.Lease owner, Character expected, int day, DfoServer.Game.Dungeons.DungeonSpecialEntryKind kind) => inner.LoadSpecialEntryUsage(owner, expected, day, kind);
        public DfoServer.Game.Dungeons.DungeonDespairProgress LoadDespairProgress(AccountOwners.Lease owner, Character expected) => inner.LoadDespairProgress(owner, expected);
        public DfoServer.Game.Dungeons.DungeonDespairClearCommit CommitDungeonDespairClear(AccountOwners.Lease owner, DfoServer.Game.Dungeons.SinglePlayerDungeonRun run,
            DfoServer.Game.Dungeons.PreparedDungeonDespairClear prepared) => inner.CommitDungeonDespairClear(owner, run, prepared);
        public DungeonAltarSettlementCommit CommitDungeonAltarSettlement(AccountOwners.Lease owner, SinglePlayerDungeonRun run,
            PreparedDungeonAltarSettlement prepared, CharacterGrowthCatalog growth) => inner.CommitDungeonAltarSettlement(owner, run, prepared, growth);
        public QuestClearReceipt? LoadQuestClear(AccountOwners.Lease owner, Character expected, ushort questId) => inner.LoadQuestClear(owner, expected, questId);
        public QuestCompletionCommit CommitQuestCompletion(AccountOwners.Lease owner, QuestCompletionPlan plan) => inner.CommitQuestCompletion(owner, plan);
        public QuestLifecycleCommit CommitQuestLifecycle(AccountOwners.Lease owner, QuestLifecyclePlan plan) => inner.CommitQuestLifecycle(owner, plan);
        public QuestQuickClearCommit CommitQuestQuickClear(AccountOwners.Lease owner, QuestQuickClearPlan plan) => inner.CommitQuestQuickClear(owner, plan);

        public DungeonSettlementCommit CommitDungeonSettlement(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run,
            PreparedDungeonSettlement prepared, CharacterGrowthCatalog catalog, InventoryPlan? cardInventory = null, bool mailCardItem = false,DungeonCardDelivery? premiumDelivery = null,
            bool grantLuckyStar = false, int luckyStarLimit = 0, bool recommendedClear = false)
        {
            var result = inner.CommitDungeonSettlement(owner, expected, run, prepared, catalog, cardInventory, mailCardItem,premiumDelivery, grantLuckyStar, luckyStarLimit, recommendedClear);
            if (++settlementCalls == 1 && loseSettlement) throw new IOException("合成翻牌及经验已提交但返回结果丢失");
            return result;
        }
        internal Action<InventoryPlan>? BeforeInventoryCommit { get; set; }
        public Character CommitInventory(AccountOwners.Lease owner, Character expected, InventoryPlan plan)
        { BeforeInventoryCommit?.Invoke(plan); return inner.CommitInventory(owner, expected, plan); }
        public Character CommitSkillSwitchInventory(AccountOwners.Lease owner, SkillSwitchInventoryPlan plan) =>
            inner.CommitSkillSwitchInventory(owner, plan);
        public Character CommitPvpTrainingSkills(AccountOwners.Lease owner,DfoServer.Game.Pvp.PvpTrainingSkillPlan plan) => inner.CommitPvpTrainingSkills(owner,plan);
        public Character CommitEpicProduction(AccountOwners.Lease owner,DfoServer.Game.EpicProduction.EpicProductionPlan plan) =>
            inner.CommitEpicProduction(owner,plan);
        public Character CommitCollectBox(AccountOwners.Lease owner, DfoServer.Game.CollectBox.CollectBoxPlan plan) =>
            inner.CommitCollectBox(owner, plan);
        public DungeonPickupCommit CommitDungeonPickup(AccountOwners.Lease owner, Character expected,
            DungeonGroundPickupPreparation prepared, InventoryPlan plan, EpicPieceCatalog? pieces = null)
        {
            var result = inner.CommitDungeonPickup(owner, expected, prepared, plan, pieces);
            PickupCalls++;
            PickupDuplicate = result.Duplicate;
            if (PickupCalls == 1 && losePickup) throw new IOException("合成拾取已提交但返回结果丢失");
            return result;
        }
    }
}
