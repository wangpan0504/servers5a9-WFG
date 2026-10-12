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
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
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
        var learning = SkillLearningCatalog.Load(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf);
        var kills = DungeonKillExperienceCatalog.Load(pvf);
        var catalog = DungeonCatalog.Load(pvf);
        var rooms = DungeonRoomCatalog.Load(pvf);
        var quests = DfoServer.GameWorld.Quests.QuestCatalog.Load(pvf);
        await EntryRejectionAsync();
        await StorageFailureAsync(false);
        await StorageFailureAsync(true);
        foreach (int packet in new[] { 1, 2 }) await SendFailureAsync(packet);
        await ReselectDuringSendAsync();
        await ReturnDuringSendAsync();
        await PickupLostResultAsync();
        await CollectionPublicationAsync();
        await ClearedQuestSendFailureAsync();
        await ClearNoticeFailureAsync();
        Console.WriteLine($"PASS {TestCheck.Count - before} 正式Handler真实文件提交、结果丢失、发送故障及重选隔离断言（合成死亡，非实机）");


        GameCommandRegistry Registry(Fixture f, TownPresence? presence = null,
            Func<DungeonEntryPlan, DungeonPlannedMonster, DungeonDropGenerationPlan>? prepareDrops = null)
        {
            var commands = new GameCommandRegistry();
            new DungeonHandler(f.Log, catalog, rooms, growth, kills,
                new TownProjection(towns, computer, presence, items.EpicPieces), new CharacterLevelProjection(computer),
                DungeonMonsterDropCatalog.Load(pvf), items.Items, items.EpicPieces, items.GoldLimits,
                prepareDrops: prepareDrops).Register(commands);
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
            if (SkillPackets.BindingsChanged(selected.Learning, committed.Learning)) expectedOpcodes.Add(19);
            if (committed.Level != selected.Level) expectedOpcodes.Add(2);
            expectedOpcodes.Add(115);
            Check(stop.IsCancellationRequested && f.Count() == 28 && committed.Revision == selected.Revision + 1 &&
                committed.ActiveMainQuests.Single().ConditionState == 0 && Same(committed, f.Session.Selected!) &&
                f.Session.Phase == SessionPhase.Settlement && stream.Opcodes.SequenceEqual(expectedOpcodes) &&
                sender.PendingBytes == 0, "31失败保留同事务成长与任务，只写已成功死亡、经验和Boss帧并释放发送预算");
            Check(await commands.DispatchAsync(context, death) == CommandResult.Handled && f.Count() == 28 &&
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
            await commands.DispatchAsync(context, death);
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
            var commands = Registry(f, prepareDrops: (route, monster) =>
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
            int before = stream.Frames.Count;
            byte[] get = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(get, goldKey);
            await commands.DispatchAsync(context, new(1, 43, get));
            Check(stream.Opcodes.Skip(before).SequenceEqual(new ushort[] { 39, 14, 43 }) &&
                stream.Frames[before].Length == 63 && stream.Frames[before + 1].Length == 142 &&
                stream.Frames[before + 2].AsSpan(16).SequenceEqual(new byte[] { 1 }),
                "金币生产包序为47B通知、实际余额差量与成功ACK");
            var notice = stream.Frames[before].AsSpan(16).ToArray();
            var delta = stream.Frames[before + 1].AsSpan(16).ToArray();
            Check(notice.Length == 47 && BinaryPrimitives.ReadUInt32LittleEndian(notice.AsSpan(7)) == 17 &&
                BinaryPrimitives.ReadUInt16LittleEndian(delta.AsSpan(3)) == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(delta.AsSpan(9)) == f.Read().Inventory.Gold && f.Read().Inventory.Gold == 17,
                "金币增量和虚拟槽0的绝对余额来自同一实际提交");
            BinaryPrimitives.WriteUInt32LittleEndian(get, itemKey);
            await commands.DispatchAsync(context, new(1, 43, get));
            Check(f.Read().Inventory.Items.Single(item => item.Core.ItemId == 1000).Core.Count == 3 &&
                f.Read().Inventory.Gold == 17 && f.Session.Selected!.Inventory.Gold == 17,
                "同死亡第二个key独立拾取三件原物品，不重复授予已取金币");
            var actual = f.Read();
            int sent = stream.Frames.Count;
            await commands.DispatchAsync(context, new(1, 43, get));
            Check(stream.Frames.Count == sent + 1 && stream.Frames[^1].AsSpan(16).SequenceEqual(new byte[] { 0, 22, 0 }) && Same(actual, f.Read()),
                "集合生产重复拾取只拒绝一次，实际库存与金币保持不变");
        }

        async Task PickupLostResultAsync()
        {
            using var f = new Fixture(towns, characters, learning, quests, true, losePickup: true);
            var entry = f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms, DungeonMonsterDropCatalog.Load(pvf), items.Items, bound => 0)!;
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
        public Fixture(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog learning, DfoServer.GameWorld.Quests.QuestCatalog quests, bool lostResult, bool losePickup = false)
        {
            path = Path.Combine(root, "game.db");
            repository = new(path, quests);
            Owner = new AccountOwners().Acquire("formal-publication", () => { });
            LostResult = lostResult ? new(repository, loseGrowth: !losePickup, losePickup: losePickup) : null;
            Session = new(LostResult is null ? repository : LostResult, towns, Owner);
            Created = repository.Create(Owner, CharacterCreation.Prepare("FormalSend", characters.Get(0), towns.Room, towns.InitialReturn, learning.Get)
                with { ActiveMainQuests = [new(0,3145,1)] });
            Session.Select(Created.Slot);
            Session.MarkReady();
            Check(Session.LeaveRoom() && Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "通知fixture由合法城镇入口准备");
            Log = new(Path.Combine(root, "logs"));
        }
        public Character Read() => repository.LoadCharacter(Owner, Created.Slot);
        public long Count()
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT count(*) FROM character_dungeon_growth_receipts";
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
        bool loseSettlement = false,bool losePaid = false) : ICharacterRepository
    {
        public DfoServer.Game.Adventure.AdventureSnapshot CommitAdventureExpedition(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureExpeditionPlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.CommitAdventureExpedition(owner, plan, catalog, now);
        public DfoServer.Game.Adventure.AdventureShopCommit CommitAdventureShop(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureShopPlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.CommitAdventureShop(owner, plan, catalog, now);
        public DfoServer.Game.Adventure.AdventureCapsuleCommit CommitAdventureCapsule(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureCapsulePlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.CommitAdventureCapsule(owner, plan, catalog, now);
        public DfoServer.Game.Adventure.AdventureSnapshot CommitAdventureRepresentative(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureRepresentativePlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.CommitAdventureRepresentative(owner, plan, catalog, now);
        public DfoServer.Game.Adventure.AdventureSnapshot ReadAdventure(AccountOwners.Lease owner, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.ReadAdventure(owner, catalog, now);
        public void InitializeAdventure(AccountOwners.Lease owner, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.InitializeAdventure(owner, catalog, now);
        public DfoServer.Game.Tutorials.TutorialCheckpointResult CommitTutorialCheckpoint(AccountOwners.Lease owner,
            DfoServer.Game.Tutorials.TutorialCheckpointPlan plan) => inner.CommitTutorialCheckpoint(owner, plan);
        public Character CommitTitleAchievement(AccountOwners.Lease owner, DfoServer.Game.Titles.TitleAchievementPlan plan) => inner.CommitTitleAchievement(owner, plan);
        public InventorySnapshot ReadPublicTitleBook(AccountOwners.Lease owner, ushort characterId) => inner.ReadPublicTitleBook(owner, characterId);
        public Character AdvanceQuestCycle(AccountOwners.Lease owner, Character expected, int day) => inner.AdvanceQuestCycle(owner, expected, day);
        public CardMembership LoadCardMembership(AccountOwners.Lease owner,ushort id,DateTimeOffset now) => inner.LoadCardMembership(owner,id,now);
        public CardMembership SetCardMembership(AccountOwners.Lease owner,string name,CardMembershipKind kind,long expires) => inner.SetCardMembership(owner,name,kind,expires);
        public DungeonPaidCardCommit CommitPaidDungeonCard(AccountOwners.Lease owner,Character expected,PreparedDungeonSettlement prepared,DungeonCardDelivery delivery,DateTimeOffset now)
        {
            var result = inner.CommitPaidDungeonCard(owner,expected,prepared,delivery,now);
            if (result.Accepted && ++paidCalls == 1 && losePaid) throw new IOException("合成付费牌已提交但结果返回丢失");
            return result;
        }
        public Character ConsumeDungeonRoomFatigue(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run) =>
            inner.ConsumeDungeonRoomFatigue(owner, expected, run);
        PreparedDungeonDeath? first;
        public int Calls { get; private set; }
        int settlementCalls;
        int paidCalls;
        public bool LastDuplicate { get; private set; }
        public bool SamePrepared { get; private set; }
        public int PickupCalls { get; private set; }
        public bool PickupDuplicate { get; private set; }
        public DungeonGrowthCommit CommitDungeonGrowth(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run,
            PreparedDungeonDeath prepared, DungeonExperienceReward reward, CharacterGrowthCatalog catalog, FrozenDungeonGroundDeath? loot = null, DfoServer.Game.Quests.QuestDropPlan? questDrops = null)
        {
            Calls++;
            first ??= prepared;
            SamePrepared = ReferenceEquals(first, prepared);
            var result = inner.CommitDungeonGrowth(owner, expected, run, prepared, reward, catalog, loot, questDrops);
            LastDuplicate = result.Duplicate;
            if (Calls == 1 && loseGrowth) throw new IOException("合成SQL已提交但结果丢失");
            return result;
        }
        public AccountWallet LoadAccountWallet(AccountOwners.Lease owner) => inner.LoadAccountWallet(owner);
        public CharacterRosterEntry[] ListRoster(AccountOwners.Lease owner) => inner.ListRoster(owner);
        public Character LoadCharacter(AccountOwners.Lease owner, ushort slot) => inner.LoadCharacter(owner, slot);
        public Character[] List(AccountOwners.Lease owner) => inner.List(owner);
        public bool NameExists(string name) => inner.NameExists(name);
        public Character Create(AccountOwners.Lease owner, NewCharacter character) => inner.Create(owner, character);
        public CharacterDeletion LoadDeletionTarget(AccountOwners.Lease owner, ushort slot) => inner.LoadDeletionTarget(owner, slot);
        public CharacterDeletion Delete(AccountOwners.Lease owner, CharacterDeletion expected) => inner.Delete(owner, expected);
        public Character Save(AccountOwners.Lease owner, Character expected, Location position, Location? back) => inner.Save(owner, expected, position, back);
        public Character CommitLearning(AccountOwners.Lease owner, Character expected, CharacterLearningState learning, int? totalSp = null, int? totalTp = null) => inner.CommitLearning(owner, expected, learning, totalSp, totalTp);
        public Character CommitSkillLayout(AccountOwners.Lease owner, Character expected, CharacterLearningState learning) => inner.CommitSkillLayout(owner, expected, learning);
        public Character UpdateProgress(AccountOwners.Lease owner, CharacterProgressEdit edit) => inner.UpdateProgress(owner, edit);
        public Character AdvanceAcceptedStoryLevel(AccountOwners.Lease owner, Character expected) => inner.AdvanceAcceptedStoryLevel(owner, expected);
        public Character ResetProgress(AccountOwners.Lease owner, CharacterProgressResetPlan plan) => inner.ResetProgress(owner, plan);
        public Character ReserveDungeonAttempt(AccountOwners.Lease owner, Character expected, uint nextObjectKey) => inner.ReserveDungeonAttempt(owner, expected, nextObjectKey);
        public QuestClearReceipt? LoadQuestClear(AccountOwners.Lease owner, Character expected, ushort questId) => inner.LoadQuestClear(owner, expected, questId);
        public QuestCompletionCommit CommitQuestCompletion(AccountOwners.Lease owner, QuestCompletionPlan plan) => inner.CommitQuestCompletion(owner, plan);
        public QuestLifecycleCommit CommitQuestLifecycle(AccountOwners.Lease owner, QuestLifecyclePlan plan) => inner.CommitQuestLifecycle(owner, plan);
        public QuestQuickClearCommit CommitQuestQuickClear(AccountOwners.Lease owner, QuestQuickClearPlan plan) => inner.CommitQuestQuickClear(owner, plan);

        public DungeonSettlementCommit CommitDungeonSettlement(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run,
            PreparedDungeonSettlement prepared, CharacterGrowthCatalog catalog, InventoryPlan? cardInventory = null, bool mailCardItem = false,DungeonCardDelivery? premiumDelivery = null,
            bool grantLuckyStar = false, int luckyStarLimit = 0)
        {
            var result = inner.CommitDungeonSettlement(owner, expected, run, prepared, catalog, cardInventory, mailCardItem,premiumDelivery, grantLuckyStar, luckyStarLimit);
            if (++settlementCalls == 1 && loseSettlement) throw new IOException("合成翻牌及经验已提交但返回结果丢失");
            return result;
        }
        public Character CommitInventory(AccountOwners.Lease owner, Character expected, InventoryPlan plan) => inner.CommitInventory(owner, expected, plan);
        public DungeonPickupCommit CommitDungeonPickup(AccountOwners.Lease owner, Character expected,
            DungeonGroundPickupPreparation prepared, InventoryPlan plan)
        {
            PickupCalls++;
            var result = inner.CommitDungeonPickup(owner, expected, prepared, plan);
            PickupDuplicate = result.Duplicate;
            if (PickupCalls == 1 && losePickup) throw new IOException("合成拾取已提交但返回结果丢失");
            return result;
        }
    }
}
