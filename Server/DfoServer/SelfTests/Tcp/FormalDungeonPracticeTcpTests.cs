using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using DfoServer.Network.Management;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunPracticeAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "dungeon-practice"), gm: true,
            resources: resources, clock: new FixedElapsedClock());
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _),
            "练习验证不用GM无限疲劳掩盖入场策略");
        using var peer = Open(fixture);
        peer.Create("PracticeTcp");
        fixture.Sql("UPDATE characters SET level=20,revive_coins=0 WHERE id=$p0", peer.Id);
        fixture.Sql("UPDATE character_fatigue SET used=maximum,remaining=0 WHERE character_id=$p0", peer.Id);
        short potion;
        short resultPotion;
        short projectile;
        short placed;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var repository = new CharacterRepository(fixture.Database);
            var character = repository.LoadCharacter(owner, 0);
            var items = resources.ItemResources;
            var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
            character = InventoryService.Commit(repository, owner, character, inventory.Grant(character,
                [new(1065, 3), new(1107, 3), new(6001,1), new(1073,1), new(3037, 20)]));
            potion = character.Inventory.Items.Single(item => item.Core.ItemId == 1065).Position.Index;
            resultPotion = character.Inventory.Items.Single(item => item.Core.ItemId == 1107).Position.Index;
            projectile = character.Inventory.Items.Single(item => item.Core.ItemId == 6001).Position.Index;
            placed = character.Inventory.Items.Single(item => item.Core.ItemId == 1073).Position.Index;
        }
        peer.Select();
        EntryTown(peer);
        string stable = Stable();
        byte[] request = Convert.FromHexString("030000000000000001FFFF0000000000000000000000000000");
        peer.Send(16, request);
        peer.Expect(1, 16, [0, 9, 0, 0]);
        peer.Barrier();
        That(Stable() == stable, "缺少15选图上下文的直接练习请求拒绝，不向空客户端场景发布28/29");
        SelectMap(peer);
        byte[] ordinary = (byte[])request.Clone();
        ordinary[8] = 0;
        peer.Send(16, ordinary);
        // 普通副本零疲劳按 dstr 49061 回业务错误22（疲劳值不足），不再是通用静默9。
        peer.Expect(1, 16, [0, 22, 0, 0]);
        peer.Barrier();
        foreach (int offset in new[] { 5, 7, 9, 11, 15, 16, 20 })
        {
            byte[] invalid = (byte[])request.Clone();
            invalid[offset] ^= 1;
            peer.Send(16, invalid);
            peer.Expect(1, 16, [0, 9, 0, 0]);
            peer.Barrier();
        }
        byte[] legendPractice = (byte[])request.Clone();
        legendPractice[7] = 2;
        peer.Send(16,legendPractice);
        peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
        That(Stable() == stable && fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[0L]],
            "零疲劳普通入场及非法混合模式拒绝；非零任务号不冒充授权回放，均不预留尝试");
        request.AsSpan(21,4).Fill(255);
        byte[] map = EnterPractice();
        string entered = fixture.Snapshot();
        peer.Send(18, Convert.FromHexString("1000000010001A0A080210E60218DD172005200001"));
        peer.Expect(1, 18, Convert.FromHexString("010D0000000800120708E602100018021800"));
        peer.Barrier();
        That(Stable() == stable && fixture.Snapshot() == entered, "练习加载阶段仍属本次免耗Run，脚本材料不提前扣费");
        LoadPractice(1);
        peer.Barrier();
        peer.Send(44, Build(w => { w.Write((ushort)potion); w.Write((byte)0); w.Write(0u); w.Write(1065); w.Write(0); }));
        peer.Expect(1, 44, Build(w => { w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write(0u); w.Write(1065); }));
        peer.Barrier();
        foreach (var item in new[] { (Slot: projectile,Id: 6001), (Slot: placed,Id: 1073) })
        {
            peer.Send(44, Build(w => { w.Write((ushort)item.Slot); w.Write((byte)0); w.Write(0u); w.Write(item.Id); w.Write(0); }));
            peer.Expect(1, 44, Build(w => { w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write(0u); w.Write(item.Id); }));
            peer.Barrier();
        }
        peer.Send(18, Convert.FromHexString("1000000010001A0A080210E60218DD172005200001"));
        peer.Expect(1, 18, Convert.FromHexString("010D0000000800120708E602100018021800"));
        peer.Barrier();
        byte[] key = [(byte)peer.Id, (byte)(peer.Id >> 8)];
        peer.Send(40, [0x74, 0, 4, 1, 0, 0]);
        peer.Expect(0, 32, [key[0], key[1], 0, 0]);
        peer.Send(41, key);
        peer.Expect(1, 41, [1, key[0], key[1], 0]);
        peer.Expect(0, 32, [key[0], key[1], 1, 0]);
        peer.Expect(0, 14, CoinBalance(0));
        peer.Barrier();
        That(Stable() == stable && fixture.Snapshot() == entered, "练习药剂、技能材料及零币复活无资产/revision变化，ACK按免扣同步");
        var route = new (byte X, byte Y, uint Map)[] { (0,1,58548),(1,1,58549),(1,0,58550),
            (1,1,58549),(2,1,58551),(2,0,58552),(3,0,58553) };
        byte[]? bossReport = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var visited = new HashSet<uint>();
            for (int index = 0; index < route.Length; index++)
            {
                if (index > 0)
                {
                    byte[] move = new byte[100];
                    move[0] = route[index].X;
                    move[1] = route[index].Y;
                    peer.Send(45, move);
                    peer.Expect(1, 45, [1]);
                    map = peer.Expect(0, 29).Body;
                    Load(peer);
                }
                bool revisit = !visited.Add(route[index].Map);
                That(revisit ? map.Length == 16 : U32(map, 14) == route[index].Map, "练习普通路线逐房加载，回访沿正式缓存不重建演员");
                foreach (int at in Enumerable.Range(0, revisit ? 0 : map[18]).Select(row => 19 + row * 22)
                    .Where(at => map[at + 13] != 9 && map[at + 14] == 0).OrderBy(at => map[at + 13] == 3))
                {
                    ushort monster = U16(map, at + 6);
                    byte[] report = Death(monster, peer.Id, map[at + 12]);
                    bool boss = map[at + 13] == 3;
                    if (boss)
                    {
                        report[55] = 1;
                        BinaryPrimitives.WriteUInt64LittleEndian(report.AsSpan(18), 9000);
                        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(26), 30);
                        SealDeath(report);
                        bossReport = report;
                    }
                    peer.Send(39, report);
                    byte[] dead = peer.Expect(0, 38).Body;
                    That(U16(dead) == monster && U16(dead, 2) == 0 && dead.Length == 9, "练习合法死亡发原key，无地面奖励");
                    if (boss)
                        That(peer.Expect(0, 255).Body is { Length: 5 } cleared && cleared[4] == 0,
                        "练习Boss只发本版255结果，不发普通115/31/经验/翻牌");
                    peer.Barrier();
                }
            }
            if (attempt == 0)
            {
                peer.Send(44, Build(w => { w.Write((ushort)resultPotion); w.Write((byte)0); w.Write(2u); w.Write(1107); w.Write(0); }));
                peer.Expect(1, 44, Build(w => { w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write(2u); w.Write(1107); }));
                peer.Barrier();
            }
            else
            {
                That(resources.ItemResources.RecoveryConsumables.TryGet(1065,out var instant) && instant.EffectMilliseconds == 0 && instant.CoolTime > 0,
                    "回选图冷却清理使用当前PVF中的无持续效果即时药");
                peer.Send(44,Build(w=>{w.Write((ushort)potion);w.Write((byte)0);w.Write(3u);w.Write(1065);w.Write(0);}));
                peer.Expect(1,44,Build(w=>{w.Write((byte)0);w.Write((ushort)0);w.Write((byte)0);w.Write(3u);w.Write(1065);}));
                peer.Barrier();
            }
            That(bossReport is not null && Stable() == stable && fixture.Snapshot() == entered, "完整练习通关及结果阶段使用道具仍免耗，不改变角色/资产/任务/revision");
            peer.Send(39, bossReport!);
            peer.Send(46, DungeonSettlementProtocolTests.Report(peer.Id, 90));
            peer.Send(69);
            peer.Send(70);
            peer.Send(71, [0, 0]);
            peer.Barrier();
            That(fixture.Snapshot() == entered && EmptyLedgers(), "死亡重放与伪造普通结算/翻牌不发奖或写普通账本");
            peer.Send(72, [2, 0]);
            peer.Expect(1, 72, [1, 2, 0]);
            if (attempt == 0)
            {
                peer.Send(72, [1, 0]);
                peer.Expect(1, 72, [1, 1, 0]);
                peer.Expect(0, 27, new byte[42]);
                PracticePlayer(1);
                That(peer.Expect(0, 28).Body[12] == 1, "再练习保留模式，不能转为普通奖励副本");
                map = peer.Expect(0, 29).Body;
                LoadPractice(2);
                peer.Barrier();
                entered = fixture.Snapshot();
            }
            else
            {
                peer.Send(72, [1, 1]);
                peer.Expect(1, 72, [1, 1, 1]);
                peer.Expect(1, 15, [1]);
                peer.Expect(0, 3, [1, key[0], key[1], 1]);
                peer.Expect(0, 27, new byte[42]);
                peer.Expect(0,172,[1,0x29,0x04,0,0,0,0,0,0]);
                peer.Barrier();
                // 退场后迟到的1/3不能再回ACK72{1,3}改写已离开的结算页。
                peer.Send(72, [1, 3]);
                peer.Barrier();
                That(EmptyLedgers(), "练习选择其他地下城直接完整选图，不跟随活动任务自动继续主线，旧尾包静默忽略");
            }
        }
        That(Stable() == stable && fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[2L]],
            "零疲劳和零门票可练习两次，不扣金币/材料或占普通玩法次数；退出无虚弱，不完成主线");
        // 用户实际请求：普通副本1000、offset8=1、任务0；不借当前主线路线入场。
        SelectMap(peer);
        BinaryPrimitives.WriteUInt32LittleEndian(request, 1000);
        peer.Send(16, request);
        PracticePlayer(2);
        byte[] actualInfo = peer.Expect(0, 28).Body;
        byte[] actualMap = peer.Expect(0, 29).Body;
        That(U32(actualInfo) == 1000 && actualInfo[12] == 1 && actualInfo[7] == 0 && U32(actualMap, 14) == 58590,
            "1000练习请求携活动尾字段进入暗黑雷鸣废墟普通迷宫，不误走剧情回放");
        LoadPractice(3);
        peer.Barrier();
        ClearLastPractice();
        peer.Send(72, [1, 0]);
        peer.Expect(1, 72, [1, 1, 2]);
        peer.Expect(0, 3, [1, key[0], key[1], 0]);
        peer.Expect(0, 24);
        RefreshedQuests(peer);
        peer.Barrier();
        That(Stable() == stable && EmptyLedgers(), "三次练习耗尽后再练习明确ACK回城，不留结果页等待或普通奖励");
        peer.Send(72, [1, 0]);
        peer.Barrier();
        SelectMap(peer);
        string exhausted = fixture.Snapshot();
        peer.Send(16, request);
        peer.Expect(1, 16, [0, 173, 0, 0]);
        peer.Barrier();
        That(fixture.Snapshot() == exhausted &&
            fixture.Query("SELECT practice_used,last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[3L,3L]],
            "跨DGN第三次共用角色额度，第四次按173拒绝且不预留新attempt或扣资产");
        peer.Send(3, [0]);
        peer.Expect(1, 3, [1]);
        Console.WriteLine("PASS 副本练习实际请求、零疲劳、逐房回访、免消耗、无奖励、重放、再练习与回城TCP");

        byte[] EnterPractice()
        {
            peer.Send(16, request);
            PracticePlayer(0);
            byte[] info = peer.Expect(0, 28).Body;
            That(info.Length == 36 && info[12] == 1 && info[7] == 0, "练习发布普通maze0和本版模式位");
            return peer.Expect(0, 29).Body;
        }
        void LoadPractice(byte used)
        {
            peer.Send(37, new byte[8]);
            peer.Expect(1, 37, [1]);
            peer.Expect(0, 30, new byte[5]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
            PracticePlayer(used);
            peer.ExpectOptionalFatigue();
            That(fixture.Query("SELECT practice_used FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[var stored]] &&
                Convert.ToInt64(stored) == used, "37后Player恢复本次已提交计数，存储始终保持After");
        }
        void PracticePlayer(byte used)
        {
            byte[] body = peer.Expect(0, 2).Body;
            // 本夹具不授予皮肤且技能后附表为空，原生尾部恰21B；非空时明确拒绝该简化解析。
            That(body.Length > 21 && body[0] == 1 && U16(body,22) == peer.Id && body[^1] == 0 &&
                body[^21] == 0 && body[^20] == 0 && body[^18] == 0 && body[^17] == 0,
                "练习Player属于本人且保留空附表的21字节尾部");
            That(body[^19] == used, "练习Player独立u8计数严格匹配包序");
        }
        void ClearLastPractice()
        {
            // 复用本轮只读目录，沿实际1000迷宫最短路逐房战斗，不能伪造结算状态来触发72。
            var maze = resources.Dungeons.Get(1000).Mazes.Single(value => value.SourceOrdinal == actualInfo[7]);
            var start = new DungeonPoint(actualMap[0], actualMap[1]);
            var boss = new DungeonPoint(actualInfo[8], actualInfo[9]);
            var from = new Dictionary<DungeonPoint, DungeonPoint?> { [start] = null };
            var queue = new Queue<DungeonPoint>();
            queue.Enqueue(start);
            while (queue.TryDequeue(out var point) && !from.ContainsKey(boss))
            foreach (var link in maze.StaticTopology.Links!)
            {
                DungeonPoint? next = link.First == point ? link.Second : link.Second == point ? link.First : null;
                if (next is not { } adjacent || from.ContainsKey(adjacent)) continue;
                from.Add(adjacent, point);
                queue.Enqueue(adjacent);
            }
            That(from.ContainsKey(boss), "实际1000练习Boss可沿资源连接抵达");
            var path = new Stack<DungeonPoint>();
            for (var point = boss; from[point] is { } parent; point = parent) path.Push(point);
            bool cleared = false;
            while (true)
            {
                foreach (int at in Enumerable.Range(0, actualMap[18]).Select(row => 19 + row * 22)
                    .Where(at => actualMap[at + 13] != 9 && actualMap[at + 14] == 0).OrderBy(at => actualMap[at + 13] == 3))
                {
                    byte[] report = Death(U16(actualMap, at + 6), peer.Id, actualMap[at + 12]);
                    bool isBoss = actualMap[at + 13] == 3;
                    if (isBoss) { report[55] = 1; SealDeath(report); }
                    peer.Send(39, report);
                    That(peer.Expect(0, 38).Body.Length == 9, "第三次练习沿真实死亡通道免奖励");
                    if (isBoss) { That(peer.Expect(0, 255).Body is { Length: 5 }, "第三次练习真实清图"); cleared = true; }
                    peer.Barrier();
                }
                if (!path.TryPop(out var next)) break;
                byte[] move = new byte[100];
                move[0] = checked((byte)next.X);
                move[1] = checked((byte)next.Y);
                peer.Send(45, move);
                peer.Expect(1, 45, [1]);
                actualMap = peer.Expect(0, 29).Body;
                Load(peer);
            }
            That(cleared, "先有真实Boss清图事实，再请求耗尽额度的72");
        }
        bool EmptyLedgers() => new[] { "character_dungeon_growth_receipts","character_dungeon_loot_receipts",
            "character_dungeon_fatigue_receipts","character_dungeon_settlement_receipts" }
            .All(table => fixture.Query($"SELECT count(*) FROM {table} WHERE character_id=$p0", peer.Id) is [[0L]]);
        string Stable() => fixture.Snapshot("SELECT level,experience,gold,stamina_until,revive_coins FROM characters WHERE id=$p0", peer.Id) +
            string.Concat(new[] { "character_fatigue","character_active_main_quests","character_completed_quests",
                "character_inventory_items","character_inventory_slots" }
                .Select(table => fixture.Snapshot($"SELECT * FROM {table} WHERE character_id=$p0 ORDER BY 1,2", peer.Id))) +
            fixture.Snapshot("SELECT * FROM account_currency_balances ORDER BY account,currency_kind");
    }
}
