using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunDespairAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        DungeonDespairEntryTests.Run(resources);
        DungeonDespairClearTests.Run(resources);
        var clock = new DespairDays();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "dungeon-despair"), resources: resources, clock: clock);
        await fixture.StartAsync();
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 11008));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(area.Default.Bounds.SelectMany(row => row.Quests))
            .Concat(area.Default.DungeonGates.SelectMany(row => row.Quests)).Distinct().ToArray();
        var bounds = area.Map(done.ToHashSet()).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new DfoServer.GameWorld.Towns.Location(area.Town, area.Area,
            checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        var growth = SelfTestCatalogs.Growth(resources.Archive);
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var repo = new CharacterRepository(fixture.Database);
            var candidate = CharacterCreation.Prepare("Despair466", resources.Characters.Get(0), position, position, resources.Learning.Get);
            created = repo.Create(owner, candidate with
            {
                Level = 70, AcceptedStoryLevel = 70, CompletedQuests = done,
                InitialLearning = SkillPointLedger.Synchronize(0, 0, 70, candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(70) }, resources.Learning)
            });
            created = InventoryService.Commit(repo, owner, created,
                new InventoryService(resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces)
                    .Grant(created, [new(4473, 2)]));
            created = repo.CommitInventory(owner, created, new(created.Inventory, created.Inventory with { Gold = 1 }, "despair_tcp_potion")
                { ExpBonus = (clock.GetUtcNow().ToUnixTimeSeconds() + 3600, 1000) });
        }
        using var peer = Open(fixture);
        peer.Select(created.Slot, prepared: true, resumeTown: true, room: false);
        OpenSelection(0);
        var first = EnterTower(11008);
        Load(peer);
        That(Amount(4473) == 2 && U32(first, 14) == 15130, "正式绝望首进加载真实首层且不扣失败邀请函");
        ReturnTown();
        OpenSelection(0);
        peer.Send(16, Request(11009)); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
        That(EntryCount() == 1 && Amount(4473) == 2, "伪造未来层既不创建实例也不扣票");
        var map = EnterTower(11008);
        That(Amount(4473) == 1 && EntryCount() == 2, "失败重试与新尝试原子扣一张4473");
        byte[]? oldDeath = null;
        int overflowNotices = 0;
        for (int layer = 1; layer <= 100; layer++)
        {
            var definition = dungeons.Get(11007 + layer);
            var maze = dungeons.ResolveOrdinaryMaze(definition, definition.Mazes.Single());
            var current = maze.Starts.Single();
            int clears = 0;
            while (true)
            {
                var resource = maze.MapSpecification!.Single(row => row.Position == current);
                That(U32(map, 14) == resource.Id && map[18] > 0, "正式绝望使用当前层实际房间与已发布演员：" + layer);
                Load(peer);
                int actorCount = map[18];
                for (int actor = 0; actor < actorCount; actor++)
                {
                    int at = 19 + actor * 22;
                    if (map[at + 21] != 0) continue;
                    ushort key = U16(map, at + 6);
                    if (layer == 100) That(map[at + 12] == 92, "100层保留梁月AIC自身92级，不套用70级奖励参考等级");
                    byte[] death = Death(key, peer.Id, map[at + 12]);
                    long mailBefore = MailCount();
                    peer.Send(39, death);
                    var packets = Drain();
                    if (MailCount() > mailBefore)
                    {
                        That(packets.Any(row => row.Opcode == 99), "已提交的通关溢出邮件必须发布99邮箱提醒");
                        overflowNotices++;
                    }
                    That(packets.Any(row => row.Opcode == 38 && U16(row.Body, 0) == key), "已登记敌对演员CMD39死亡得到NOTI38确认");
                    if (layer == 1) That(packets.TakeWhile(row => row.Opcode != 348).Any(row => row.Opcode == 37),
                        "有经验秘药的终末击杀先发布成长再提交专用结果，资产快照替换不吞掉EXP通知");
                    foreach (var packet in packets.Where(row => row.Opcode == 348))
                    {
                        clears++;
                        That(packet.Body.Length == 87 && U16(packet.Body, 4) == layer && packet.Body[6] == 10,
                            "348完整计时、当前层、十个(itemId,addInfo)结果槽");
                        if (layer == 100) That(U32(packet.Body, 79) == 3314 && U32(packet.Body, 83) == 1,
                            "100层特殊槽为当前PVF通关奖章一枚");
                        else if (layer % 10 == 0) That(U32(packet.Body, 79) == 10089402 && U32(packet.Body, 83) == 6,
                            "十层特殊槽按明确标记的公开兼容规则发气息，不发旧1252");
                    }
                    oldDeath = death;
                }
                if (current == maze.Bosses.Single()) break;
                That(clears == 0, "多房层中间房死亡不提前推进持久层数或发布348");
                var goal = maze.Bosses.Single();
                current = new(current.X + Math.Sign(goal.X - current.X), current.Y + Math.Sign(goal.Y - current.Y));
                byte[] move = new byte[100]; move[0] = checked((byte)current.X); move[1] = checked((byte)current.Y);
                peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
            }
            That(clears == 1 && fixture.Query("SELECT next_layer,clear_count FROM dungeon_despair_progress WHERE character_id=$p0", peer.Id)
                is [[long next, long count]] && next == (layer == 100 ? 1 : layer + 1) && count == layer,
                "一次终末清图只推进一次原层进度：" + layer);
            string stable = fixture.Snapshot("SELECT * FROM dungeon_despair_clear_receipts WHERE character_id=$p0", peer.Id);
            peer.Send(39, oldDeath!);
            That(Drain().All(row => row.Opcode != 348) &&
                fixture.Snapshot("SELECT * FROM dungeon_despair_clear_receipts WHERE character_id=$p0", peer.Id) == stable,
                "重复死亡不重发348或重复落库");
            ReturnTown();
            OpenSelection(checked((ushort)(layer == 100 ? 1 : layer + 1)));
            peer.Send(16, Request(11008)); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
            That(EntryCount() == layer + 1, "当天通关后拒绝继续入场，次数不增加");
            if (layer == 100) break;
            clock.AdvanceDay();
            peer.ExpectUnusedDailyBoosterReset();
            // 交替使用入口号和服务器已持久批准的当前层号，两者都不能授权下一层。
            map = EnterTower(layer % 2 == 0 ? 11008u : checked((uint)(11008 + layer)));
        }
        That(overflowNotices > 0 && Amount(3314) == 1 && Amount(10089402) == 54 && Amount(4473) == 1 &&
            fixture.Query("SELECT count(*) FROM dungeon_despair_clear_receipts WHERE character_id=$p0", peer.Id) is [[100L]],
            "完整100层后奖章、九次十层材料、重试票与100条唯一回执一致");
        That(fixture.Query("SELECT count(*) FROM character_title_progress WHERE character_id=$p0 AND quest_id BETWEEN 6600 AND 6648 AND completed_revision>0", peer.Id)
            is [[49L]], "全部49个楼层称号随原层通关持久完成");
        string progressBeforeRestart = fixture.Snapshot("SELECT * FROM dungeon_despair_progress WHERE character_id=$p0", peer.Id);
        string receiptsBeforeRestart = fixture.Snapshot("SELECT * FROM dungeon_despair_clear_receipts WHERE character_id=$p0", peer.Id);
        string inventoryBeforeRestart = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id);
        peer.Dispose();
        await fixture.StopAsync();
        await fixture.StartAsync("despair-actual-restart", isolatedProcess: true);
        using (var resumed = Open(fixture))
        {
            resumed.Select(created.Slot, prepared: true, resumeTown: true, room: false);
            SelectTowerMap(resumed,1);
            That(fixture.Snapshot("SELECT * FROM dungeon_despair_progress WHERE character_id=$p0", resumed.Id) == progressBeforeRestart &&
                fixture.Snapshot("SELECT * FROM dungeon_despair_clear_receipts WHERE character_id=$p0", resumed.Id) == receiptsBeforeRestart &&
                fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", resumed.Id) == inventoryBeforeRestart,
                "真实子进程冷启动恢复100层后的下一层、唯一通关回执、物品UID与完整资产");
            resumed.Barrier();
        }
        Console.WriteLine("PASS 绝望正式TCP100层106房、进度、首免/重试票、49称号、347/348、防重放与进程冷启动（非客户端验收）");

        long Amount(int item) => (long)fixture.Query("SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1", peer.Id, item)[0][0];
        long EntryCount() => (long)fixture.Query("SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$p0", peer.Id)[0][0];
        long MailCount() => (long)fixture.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_id=$p0", peer.Id)[0][0];
        byte[] Request(uint id) { var body = Selection(); BinaryPrimitives.WriteUInt32LittleEndian(body, id); BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), 0); return body; }
        byte[] EnterTower(uint id)
        {
            peer.Send(16, Request(id));
            var packet = peer.Read();
            if (packet is { Kind: 0, Opcode: 347 })
            {
                That(packet.Body.Length == 129 && packet.Body[0] is >= 10 and <= 90 && packet.Body[0] % 10 == 0 &&
                    packet.Body.AsSpan(1).IndexOfAnyExcept((byte)0) < 0,
                    "每十层先发完整347空名合同，启用客户端已有本机角色回退而不遗留上一场APC");
                packet = peer.Read();
            }
            That(packet is { Kind: 0, Opcode: 28 }, $"正式绝望按347(若需要)→28→29发布，实际{packet.Kind}/{packet.Opcode}");
            var info = packet.Body;
            That(U32(info, 0) is >= 11008 and <= 11107, "28发布服务器批准的实际层号");
            var loaded = peer.Expect(0, 29).Body;
            _ = Drain(); // 扣票背包及跨日维护通知在入图后完整收取。
            return loaded;
        }
        void OpenSelection(ushort floor)
        {
            SelectTowerMap(peer,floor);
        }
        void ReturnTown()
        {
            peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
            peer.Expect(0, 24); RefreshedQuests(peer); peer.Barrier();
        }
        List<WirePacket> Drain()
        {
            peer.SendBarrier();
            var packets = new List<WirePacket>();
            for (int messages = 0; messages < 100; messages++)
            {
                var packet = peer.Read();
                if (packet is { Kind: 1, Opcode: 1347 }) { Bytes(packet.Body, [1], "绝望命令屏障"); return packets; }
                That(packet.Kind == 0 && packet.Opcode is not (31 or 115 or 208 or 209), "绝望不进入普通或祭坛结算许可");
                packets.Add(packet);
            }
            throw new InvalidOperationException("绝望命令响应超过有界预算");
        }
    }

    sealed class DespairDays : TimeProvider
    {
        long milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref milliseconds));
        public void AdvanceDay() => Interlocked.Add(ref milliseconds, 86400000);
    }
}
