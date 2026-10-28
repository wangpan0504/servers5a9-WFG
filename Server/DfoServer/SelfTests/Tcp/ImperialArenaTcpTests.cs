using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.Game.ImperialArena;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class ImperialArenaTcpTests
{
    internal static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-27T08:00:00+08:00"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "imperial-arena"), resources: resources, clock: clock);
        Character first, second;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var repository = new CharacterRepository(fixture.Database);
            Character Create(string name, byte level)
            {
                var plan = CharacterCreation.Prepare(name, resources.Characters.Get(0), resources.Towns.Room,
                    resources.Towns.InitialReturn, resources.Learning.Get);
                return repository.Create(owner, plan with { Level = level, Inventory = plan.Inventory with { Gold = 1_000_000,
                    Items = plan.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65),
                        InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(1113), 2))) },
                    InitialLearning = SkillPointLedger.Synchronize(0, 0, level,
                        plan.InitialLearning with { BonusSp = 0, BonusTp = 0 }, resources.Learning) });
            }
            first = Create("EmpireFirst", 90); Create("EmpireLow", 89); second = Create("EmpireSecond", 90);
        }
        await fixture.StartAsync();
        int port = ChannelTcpTests.Endpoints(fixture)[48];
        var catalog = new ImperialArenaCatalog(resources.Archive, resources.ItemResources.Items);
        using (var peer = Open(fixture, gamePort: port, channelNumber: 48))
        {
            That(peer.LoginEndpoint[3] == 39, "帝国频道真实LOGIN类型39");
            // 首次入城编队为空时按当前角色为队首自动配队：客户端没有 runtimeActor 时头像、队伍设置和入场按钮全部失效。
            peer.Select(tagEvildom: [1403, 1400, 1632, 1393, 1394]);
            Bytes(peer.TagEvildomInfo[0].Body, [0, 0, 0], "帝国初始未购买日券");
            Opponents(peer.TagEvildomInfo.Single(packet => packet.Opcode == 1400).Body,
                ImperialArenaDailyRoute.Create(catalog, ImperialArenaPeriod.At(clock.GetUtcNow())));
            Bytes(peer.TagEvildomInfo.Single(packet => packet.Opcode == 1393).Body, [2, 0, 2, 2],
                "自动配队取当前角色为队首、名册里第一名其他90级非决斗角色为队友");
            That(fixture.Query("SELECT first,second FROM account_imperial_arena_configuration") is [[long seededFirst, long seededSecond]] &&
                seededFirst == first.Id && seededSecond == second.Id, "自动配队落库稳定身份");
            // 清空编队后仍须拒绝购券：不扣金币、不递增角色版本、不创建日券。
            fixture.Sql("DELETE FROM account_imperial_arena_configuration");
            string noTeam = Assets();
            peer.Send(1461, [1]); peer.Expect(1, 1461, [0, 19, 0]); peer.Barrier();
            That(Assets() == noTeam && fixture.Query("SELECT count(*) FROM account_imperial_arena_days") is [[0L]],
                "空编队购券不扣金币、不递增角色版本、不创建日券");
            foreach (byte[] invalid in new byte[][] { [2, 0, 2, 1], [2, 0, 2, 0], [3, 0, 2, 2], [2, 0, 2, 99] })
            {
                peer.Send(1460, invalid); peer.Expect(1, 1460, [0, 19, 0]); peer.Barrier();
            }
            That(fixture.Query("SELECT count(*) FROM account_imperial_arena_configuration") is [[0L]], "无效编队不落库");
            peer.Send(1460, [2, 0, 2, 2]); peer.Expect(1, 1460, [1, 2, 0, 2, 2]);
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(tagEvildom: [1403, 1400, 1632, 1393, 1394]);
            Bytes(peer.TagEvildomInfo[3].Body, [2, 0, 2, 2], "帝国双Actor保留真实名册索引");
            peer.Send(1426); peer.Expect(0, 23); peer.Expect(0, 24); peer.Barrier();
            That(fixture.Query("SELECT town,area FROM characters WHERE id=$p0", first.Id) is [[48L, 0L]], "帝国实际出门城镇48/0");
            peer.Send(1463); peer.Expect(1, 1463, [1, 0, 0, 0, 0, 0]);
            peer.Send(1464); peer.Expect(1, 1464, [0, 19, 0]); peer.Barrier();
            for (int day = 0; day < 2; day++)
            {
                byte mode = checked((byte)(day + 1));
                uint dungeon = day == 0 ? 320u : 336u;
                Purchase(peer, mode);
                That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", first.Id) is [[long gold]] && gold == 850_000 - day * 150_000,
                    "帝国每日只扣首次15万");
                Purchase(peer, mode);
                peer.Send(1461, [mode == 1 ? (byte)2 : (byte)1]); peer.Expect(1, 1461, [0, 19, 0]); peer.Barrier();
                var route = ImperialArenaDailyRoute.Create(catalog, ImperialArenaPeriod.At(clock.GetUtcNow())).Opponents((ImperialArenaMode)mode);
                long mailBefore = (long)fixture.Query("SELECT count(*) FROM mailbox_messages")[0][0]!;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    // 实机2026-10-28 10:43 竞技场25B16末位u32为0xFFFFFFFF，按客户端原样回放。
                    var map = Enter(peer, dungeon, uint.MaxValue);
                    for (int room = 0; room <= route.Length; room++)
                    {
                        ClearRoom(peer, map, room == 0 ? 0 : route[room - 1].MonsterId, clock, attempt == 0,
                            attempt == 0 && (mode == 1 || room == route.Length),
                            room == 0 && attempt == 0 ? () => Restricted(peer) : null);
                        if (mode == 2 && room > 0 && room < route.Length)
                            That(fixture.Query("SELECT count(*) FROM mailbox_messages") is [[long current]] && current == mailBefore + attempt,
                                "困难中途只冻结奖励，结束前不逐回合发信");
                        if (room < route.Length)
                        {
                            var move = new byte[100]; move[0] = checked((byte)(room + 1));
                            peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
                        }
                    }
                    That(fixture.Query("SELECT cleared_round,elapsed_ms FROM account_imperial_arena_runs ORDER BY admitted_at DESC LIMIT 1") is
                        [[long round, long elapsed]] && round == route.Length && elapsed == (route.Length + 1) * 10_000,
                        "帝国真实全路线、指定对手与单调耗时持久化");
                    Return(peer);
                }
                That(fixture.Query("SELECT count(*) FROM mailbox_messages") is [[long after]] && after == mailBefore + (mode == 1 ? route.Length : 1),
                    "每账号每日对手邮件永久幂等，重进不增发");
                if (mode == 2)
                {
                    peer.Send(1618); peer.Expect(1, 1618, [1]);
                    var challenge = peer.Expect(0, 1632).Body;
                    That(challenge.Length == 13 && U32(challenge, 0) == 80000 && U32(challenge, 8) == 240000 && challenge[12] == 0,
                        "每周挑战恢复真实耗时与配置目标，领奖后关闭领取位");
                    peer.Expect(0, 99); peer.Barrier();
                    var claimed = fixture.Snapshot("SELECT * FROM account_imperial_arena_challenge_claims");
                    peer.Send(1618); peer.Expect(1, 1618, [1]); peer.Expect(0, 1632); peer.Expect(0, 99); peer.Barrier();
                    That(fixture.Snapshot("SELECT * FROM account_imperial_arena_challenge_claims") == claimed &&
                        fixture.Query("SELECT count(*) FROM mailbox_messages") is [[long final]] && final == mailBefore + 2,
                        "每周荣誉邮件与资格同事务，重复请求不增发");
                }
                if (day == 0)
                {
                    NextDay(peer, clock, resources);
                }
            }
            NextDay(peer, clock, resources);
            Purchase(peer, 2);
            var partial = ImperialArenaDailyRoute.Create(catalog, ImperialArenaPeriod.At(clock.GetUtcNow())).Hard;
            long countBefore = (long)fixture.Query("SELECT count(*) FROM mailbox_messages")[0][0]!;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var map = Enter(peer, 336);
                ClearRoom(peer, map, 0, clock, false, false);
                for (int room = 1; room <= attempt + 1; room++)
                {
                    var move = new byte[100]; move[0] = checked((byte)room);
                    peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
                    ClearRoom(peer, map, partial[room - 1].MonsterId, clock, room == attempt + 1, false);
                }
                That(fixture.Query("SELECT count(*) FROM mailbox_messages") is [[long beforeEnd]] && beforeEnd == countBefore + attempt,
                    "困难部分通关的累计奖励不会提前发信");
                if (attempt == 0) Return(peer, recoveredMail: true);
                else if (attempt == 1)
                {
                    peer.Send(1468, [1]); peer.Expect(1, 1468, [1]); peer.Expect(0, 1407, [1]);
                    peer.Send(1468, [0]); peer.Expect(1, 1468, [0, 19, 0]); peer.Barrier();
                    clock.Advance(TimeSpan.FromSeconds(2));
                    peer.Send(1468, [0]); peer.Expect(1, 1468, [1]); peer.Expect(0, 1407, [0]);
                    peer.Send(1469, [0]); peer.Expect(0, 1407, [1]); peer.Barrier();
                    peer.Send(1468, [0]); peer.Expect(1, 1468, [0, 19, 0]);
                    peer.Send(1469, [1]); peer.Expect(1, 1469, [1]); peer.Expect(0, 99); peer.Barrier();
                    Return(peer);
                }
                // 第三次在下一作用域前真实断线，End或新owner恢复冻结的第三回合差额。
            }
            fixture.Sql("CREATE TRIGGER imperial_disconnect_abort BEFORE INSERT ON mailbox_messages BEGIN SELECT RAISE(ABORT,'imperial disconnect rollback'); END");
        }
        using (var peer = Open(fixture, gamePort: port, channelNumber: 48))
        {
            That(fixture.Query("SELECT count(*) FROM account_imperial_arena_rewards WHERE mail_id=0") is [[1L]],
                "断线结算故障保留完整冻结差额，旧owner不提交半封邮件");
            fixture.Sql("DROP TRIGGER imperial_disconnect_abort");
            bool recover = fixture.Query("SELECT count(*) FROM account_imperial_arena_rewards WHERE mail_id=0") is [[long pending]] && pending > 0;
            peer.Select(tagEvildom: recover ? [99, 1403, 1400, 1632, 1393, 1394] : [1403, 1400, 1632, 1393, 1394]);
            Bytes(peer.TagEvildomInfo.Single(packet => packet.Opcode == 1403).Body, [1, 2, 3], "重登恢复本日困难券及三回合已清头像遮罩");
            That(fixture.Query("SELECT first,second FROM account_imperial_arena_configuration") is [[long a, long b]] &&
                a == first.Id && b == second.Id, "数据库保存稳定身份而非名册索引");
            That(fixture.Query("SELECT count(*) FROM account_imperial_arena_runs WHERE mode=2 AND closed=0") is [[0L]],
                "困难完整、放弃、败北和断线实例均完成唯一结算");
            That(fixture.Query("SELECT count(DISTINCT mail_id) FROM account_imperial_arena_rewards WHERE day=20261029 AND mail_id>0") is [[3L]],
                "三次部分挑战各自只发一次新增差额邮件");
            That(fixture.Query("SELECT hard_round FROM account_imperial_arena_days WHERE day=20261029") is [[3L]],
                "断线重打低回合不重置账号本日困难进度");
        }
        Console.WriteLine($"PASS {Count - checks} 帝国竞技场编队、日券、双模式完整路线、奖励幂等与重登TCP断言");

        string Assets() => fixture.Snapshot("SELECT id,revision,gold,selected_skill_tree FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT character_id,list_type,slot_index,item_uid FROM character_inventory_slots ORDER BY character_id,list_type,slot_index");

        void Restricted(TcpPeer peer)
        {
            string before = Assets();
            peer.Send(48, [12]); peer.Barrier();
            That(Assets() == before, "帝国真实战斗耐久报告不改变装备或角色版本");
            int weapon = first.Inventory.Find(new(InventoryListType.Equipment, 12))!.Core.ItemId;
            peer.Send(19, Build(w => { w.Write((byte)3); w.Write((short)12); w.Write(weapon); w.Write(1);
                w.Write((byte)0); w.Write((short)9); w.Write(0); w.Write(0); w.Write(-1); w.Write((ushort)0); }));
            peer.Expect(1, 19, [0, 1, 0, 3, 0]); peer.Barrier();
            That(Assets() == before, "帝国拒绝真实已穿武器卸装，装备和库存不变");
            peer.Send(260, [0]); peer.Expect(1, 260, [0, 1, 0]); peer.Barrier();
            That(Assets() == before, "帝国拒绝修改技能方案且学习资料不变");
            peer.Send(44, Build(w => { w.Write((ushort)65); w.Write((byte)0); w.Write(123u); w.Write(1113); w.Write(0); }));
            peer.Expect(1, 44, Build(w => { w.Write((byte)0); w.Write((ushort)123); w.Write((byte)0); w.Write(123u); w.Write(1113); }));
            peer.Barrier();
            That(Assets() == before, "帝国拒绝真实普通药品使用，数量和资产不变");
        }
    }

    static void Purchase(TcpPeer peer, byte mode)
    {
        peer.Send(1461, [mode]); peer.Expect(0, 14); peer.Expect(0, 1403); peer.Expect(0, 1400); peer.Expect(0, 1632); peer.Expect(0, 1393); peer.Expect(0, 1394);
        peer.Expect(1, 1461, [1]); peer.Barrier();
    }

    static void Opponents(byte[] body, ImperialArenaDailyRoute route)
    {
        That(body.Length == 64 && body[0] == 4 && body[21] == 1 && body[27] == 7 && body[63] <= 1,
            "1400独立解码验证4普通、1特殊、7困难及共同弱点完整64字节合同");
        for (int i = 0; i < 5; i++)
        {
            int at = i < 4 ? 1 + i * 5 : 22;
            That(U32(body, at) == route.Normal[i].MonsterId && body[at + 4] == route.NormalWeakness[i], "1400普通与特殊对手、弱点对应实际当天路线");
        }
        for (int i = 0; i < 7; i++)
            That(U32(body, 28 + i * 5) == route.Hard[i].MonsterId && body[32 + i * 5] == route.HardWeakness[i], "1400七轮困难对手与元素弱点逐项一致");
        That(body[63] == route.CommonWeakness && body[26] == 4, "1400共同弱点准确且竞技之王保留无元素弱点");
    }
    // trailer 是25B16末位u32：实机帝国竞技场发0xFFFFFFFF，普通副本发0；全0变体仍由336困难路线覆盖。
    static byte[] Enter(TcpPeer peer, uint dungeon, uint trailer = 0)
    {
        peer.Send(15, Build(w => { w.Write(dungeon); w.Write((ushort)0); }));
        peer.Send(16, Build(w => { w.Write(dungeon); w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write((byte)0);
            w.Write(ushort.MaxValue); w.Write(0u); w.Write((byte)0); w.Write(0u); w.Write((byte)0); w.Write(trailer); }));
        peer.Expect(1, 15, [1]); peer.Expect(0, 3); peer.Expect(0, 27); peer.Expect(0, 28);
        return peer.Expect(0, 29).Body;
    }
    static void Return(TcpPeer peer, bool recoveredMail = false)
    {
        peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0, 3); peer.Expect(0, 24);
        FormalDungeonTcpTests.RefreshedQuests(peer); peer.Expect(0, 2); peer.Expect(0, 2); peer.Expect(0, 19);
        if (recoveredMail) peer.Expect(0, 99);
        peer.Expect(0, 1403); peer.Expect(0, 1400); peer.Expect(0, 1632); peer.Expect(0, 1393); peer.Expect(0, 1394); peer.Barrier();
    }

    static void NextDay(TcpPeer peer, DailyBoosterTestClock clock, TcpTestResources resources)
    {
        clock.Advance(TimeSpan.FromDays(1));
        var daily = new Dictionary<int, byte>();
        int expected = resources.ItemResources.Boosters.DailyRetainedTemplateIds.Count;
        while (daily.Count < expected)
        {
            var packet = peer.Read();
            if (packet is { Kind: 0, Opcode: 541 })
            {
                foreach (var row in DailyBoosterProjectionTests.Decode(packet.Body)) daily.Add(row.Key, row.Value);
                That(daily.Count <= expected, "跨日礼盒次数分片不重复且不超出当期名册");
                continue;
            }
            That(ServiceDayNotice(packet), $"帝国跨日只容纳全局日切通知，实际{packet.Kind}/{packet.Opcode}");
        }
        ExpectDay(peer, 1403, [0, 0, 0]);
        ExpectDay(peer, 1400); ExpectDay(peer, 1632); ExpectDay(peer, 1393); ExpectDay(peer, 1394); peer.Barrier();
    }

    // 跨天会先跑完整服务日刷新；婚房用例已按真实运行登记这批全局日切通知，帝国用例沿用同一清单放行，
    // 帝国自己的五包仍按原顺序、原包体精确核对，不跳过任何帝国通知。
    static bool ServiceDayNotice(WirePacket packet) => packet.Kind == 0 &&
        packet.Opcode is 2 or 3 or 4 or 6 or 14 or 21 or 22 or 23 or 24 or 36 or 356 or 550 or 574 or 720
            or 1003 or 1005 or 1011 or 1012 or 1014 or 1015 or 1016 or 1017 or 1018 or 1343 or 1349 or 1379 or 1536 or 1681;

    static void ExpectDay(TcpPeer peer, ushort opcode, byte[]? body = null)
    {
        WirePacket packet;
        do { packet = peer.Read(); } while (ServiceDayNotice(packet));
        That(packet.Kind == 0 && packet.Opcode == opcode, $"帝国跨日通知顺序，期望(0,{opcode})，实际({packet.Kind},{packet.Opcode})");
        if (body is not null) Bytes(packet.Body, body, $"帝国跨日通知{opcode}包体");
    }

    static void ClearRoom(TcpPeer peer, byte[] map, int opponent, DailyBoosterTestClock clock, bool reward, bool mail, Action? afterLoad = null)
    {
        FormalDungeonTcpTests.Load(peer); peer.Barrier(); afterLoad?.Invoke(); clock.Advance(TimeSpan.FromSeconds(10));
        var actors = Enumerable.Range(0, map[18]).Select(i => 19 + 22 * i)
            .Where(at => map[at + 13] != 9 && map[at + 21] == 0).OrderBy(at => U32(map, at + 8) == opponent).ToArray();
        if (opponent != 0) That(actors.Any(at => U32(map, at + 8) == opponent), "每日公布的对手确实出现在实际房间");
        foreach (int at in actors)
        {
            peer.Send(39, FormalDungeonTcpTests.Death(U16(map, at + 6), peer.Id, map[at + 12])); peer.Expect(0, 38);
            if (opponent != 0 && U32(map, at + 8) == opponent)
            {
                That(peer.Expect(0, 1402).Body[0] == (reward ? 2 : 0), "帝国只展示本次实际新增奖励");
                if (mail) peer.Expect(0, 99);
                peer.Barrier(); return;
            }
            peer.Barrier();
        }
    }
}
