using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class TagEvildomTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        await GuildTrainingEntryTcpTests.VerifySoloAsync(source, root, resources, imperial: false);
        ResourceRewards(resources);
        var clock = new DfoServer.Testing.Inventory.DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-27T08:00:00+08:00"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "tag-evildom"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        var profile = resources.Characters.Get(0);
        var template = profile.Equipment.First(row => row.Template.EquipmentSlot is >= 12 and <= 25).Template;
        var equipment = new ItemCore(template.Id, ItemKind.Equipment, 12345) { Durability = 12 };
        Character primary, standby;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            Character Create(string name, byte level, bool equipped = false, bool secondTree = false)
            {
                var plan = CharacterCreation.Prepare(name, profile, resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, level, plan.InitialLearning with { BonusSp = 0, BonusTp = 0 }, resources.Learning);
                if (secondTree)
                {
                    var changed = SkillLearning.Plan(0, 0, level, learning, new(1, 0, [new(5, false, 1)]), resources.Learning);
                    That(changed.Success, "备用第二树按真实资源学习并与第一树分离");
                    learning = changed.State! with { SelectedTree = 1 };
                }
                // 纯金币修理要求角色钱包够付全套装修理价，夹具按线上角色带真实金币。
                var inventory = equipped
                    ? plan.Inventory with { Gold = 10_000_000u, Items = [new(0, new(InventoryListType.Equipment, template.EquipmentSlot), equipment)] }
                    : plan.Inventory with { Gold = 10_000_000u };
                return repository.Create(owner, plan with { Level = level, InitialLearning = learning, Inventory = inventory });
            }
            // 主角也带一件真实受损装备：线上作废已装双Actor的是进场前修理扣金币，夹具必须能复现这条路径。
            primary = Create("TagFirst", 95, equipped: true);
            Create("TagLow", 94);
            standby = Create("TagSecond", 95, equipped: true, secondTree: true);
            Create("TagTooLow", 93);
        }
        ResourceRoutes(resources, primary, standby);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        byte[] empty = Teams(-1, -1), populated = Teams(0, 2), status = Gathering();
        using (var peer = Open(fixture, gamePort: endpoints[46], channelNumber: 46))
        {
            That(peer.LoginEndpoint[3] == 53, "再战真实频道LOGIN类型53，不借魔界大战54或普通频道");
            // 选角前不应公布城镇名册；后续SELECT严格包序同时验证没有迟到1408。
            peer.Send(1470);
            peer.Select(tagEvildom: [1677, 1678, 1393, 1394]);
            Bytes(peer.TagEvildomInfo[0].Body, populated, "无编队的合格双角色入城提交第一支队伍并下发54B");
            Bytes(peer.TagEvildomInfo[1].Body, status, "无通关事实的真实1678初态46B");
            ValidateProjection(peer.TagEvildomInfo, populated, status, primary.Id, standby, equipment, template.EquipmentSlot);
            long expectFirst = primary.Id, expectSecond = standby.Id;
            That(fixture.Query("SELECT first0,second0,revision FROM account_tag_evildom_configuration") is [[long seededFirst, long seededSecond, 1L]] &&
                seededFirst == expectFirst && seededSecond == expectSecond,
                "首次入城按名册把当前角色配为队首、下一名95级角色配为队友，保存稳定ID");
            That(fixture.Query("SELECT town,area FROM characters WHERE id=$p0", primary.Id) is [[38L, 1L]],
                "type53仍按本版原生落38/1，不伪称默认62/0");
            string assets = Assets();
            string configuration = fixture.Snapshot("SELECT * FROM account_tag_evildom_configuration");
            RequestTeamRoster(peer);
            RequestTeamRoster(peer);
            That(Assets() == assets && configuration == fixture.Snapshot("SELECT * FROM account_tag_evildom_configuration"),
                "重复1470只完成真实名册就绪，不修改已有第一队、角色或资产");
            foreach (byte[] invalid in new[] { Setting(0, 1), Setting(0, 0), Setting(0, 99), Setting(0, 2, region: 3) })
            {
                peer.Send(1662, invalid); peer.Expect(1, 1662, [0, 19, 0]); peer.Expect(0, 1709); peer.Barrier();
                That(fixture.Query("SELECT first0,second0,revision FROM account_tag_evildom_configuration") is [[long keptFirst, long keptSecond, 1L]] &&
                    keptFirst == expectFirst && keptSecond == expectSecond && Assets() == assets,
                    "94级、重复、名册外和跨区配置均拒绝，不覆盖首次入城编队，不改资产");
            }
            peer.Send(1662, Setting(0, 2));
            peer.Expect(1, 1662, Accepted(populated));
            ReadConfiguration(peer, populated, status);
            That(fixture.Query("SELECT first0,second0,revision FROM account_tag_evildom_configuration") is [[long sameFirst, long sameSecond, 1L]] &&
                sameFirst == expectFirst && sameSecond == expectSecond && Assets() == assets,
                "重复保存同一对稳定ID不推进配置版本，也不改角色资产");
            ExitRoom(peer, republish: true);
            That(fixture.Query("SELECT town,area FROM characters WHERE id=$p0", primary.Id) is [[62L, 0L]],
                "真实53房间出口通过当前PVF准入和gate落中央竞技场62/0，并在新位置重发双角色资料");
            // 修理只改主角自己的装备耐久与角色金币，账号钱包版本不变，已装的双Actor仍然有效；线上2026-10-28
            // 02:54:54修理后两次入场都被拒绝，一次修理不能变成重登要求。
            long gold = (long)fixture.Query("SELECT gold FROM characters WHERE id=$p0", primary.Id)[0][0]!;
            // 第三字段是源券槽位：-1 表示纯金币修理全部穿戴，不能拿装备槽号当券槽。
            // 纯金币修理只回23，并按提交后的耐久下发差量；券容器不会整列重发，所以这里用屏障批量收通知。
            peer.Send(23, Build(w => { w.Write((byte)0); w.Write((short)-1); w.Write((short)-1); w.Write((ushort)0); w.Write((byte)0); }));
            peer.Expect(1, 23);
            var repairNotices = PartyDungeonTcpTests.Drain(peer);
            That(repairNotices.Any(row => row.Kind == 0 && row.Opcode == 14), "修理后按提交耐久下发差量");
            That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", primary.Id) is [[long repairedGold]] && repairedGold < gold,
                "夹具真实扣金币修理主角装备");
            That(repairNotices.All(row => !(row.Kind == 0 && row.Opcode is 1393 or 1394)),
                "一次金币修理不作废已装双Actor，入场前不重发1393/1394");
            string beforeEntry = Economy(fixture);
            foreach (uint dungeon in new uint[] { 9608, 400000095 })
            {
                var map = EnterTag(peer, dungeon);
                // Loading的生命请求不改状态，1468按项目通用拒绝解除pending。
                peer.Send(1468, [1]); peer.Expect(1, 1468, [0, 19, 0]);
                FormalDungeonTcpTests.Load(peer); peer.Barrier();
                peer.Send(1468, [1]); peer.Expect(1, 1468, [1]); peer.Expect(0, 1407, [1]);
                peer.Send(1468, [1]); peer.Expect(1, 1468, [1]); peer.Barrier();
                peer.Send(1469, [0]); peer.Barrier();
                peer.Send(1469, [0]); peer.Barrier();
                peer.Send(1468, [0]); peer.Expect(1, 1468, [0, 19, 0]);
                clock.Advance(TimeSpan.FromMilliseconds(11_999)); peer.Barrier();
                clock.Advance(TimeSpan.FromMilliseconds(1)); peer.Expect(0, 1676, new byte[4]);
                peer.Send(1469, [1]); peer.Expect(0, 1407, [0]);
                peer.Send(1469, [0]); peer.Expect(1, 1469, [1]); peer.Barrier();
                ReturnTag(peer);
                clock.Advance(TimeSpan.FromSeconds(12)); peer.Barrier();
                That(Economy(fixture) == beforeEntry && fixture.Query("SELECT count(*) FROM account_tag_evildom_clearances") is [[0L]],
                    "双难度真实15/16/37、切角/单角12秒复活/双死42仅推进原Run，不扣疲劳币、不发奖励；回城自动重建下一轮pair");
            }
            status = Gathering(4);
            peer.Send(1663); peer.Expect(1, 1663, Accepted(empty));
            peer.Expect(0, 1677, empty); peer.Expect(0, 1678, status); peer.Barrier();
            peer.Send(15, Build(w => { w.Write(9608u); w.Write((ushort)0); })); peer.Send(16, Selection(9608));
            peer.Expect(1, 15, [0, 1, 0]); peer.Expect(0, 1709); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
            // ACK1663后等待原生7，不能服务端再主动小退一次。
            ReturnToSelection(peer);
            peer.Select(tagEvildom: [1677, 1678]); peer.Barrier();
            RequestTeamRoster(peer);
            peer.Send(1662, Setting(0, 2)); peer.Expect(1, 1662, Accepted(populated));
            ReadConfiguration(peer, populated, status);
            peer.Send(1662, Setting(-1, -1)); peer.Expect(1, 1662, Accepted(empty));
            peer.Expect(0, 1677, empty); peer.Expect(0, 1678, status);
            peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            // 主动清空后的同一次选角保持空队，不把当前角色再写成队友。新连接才重新配队。
            peer.Select(tagEvildom: [1677, 1678]); peer.Barrier();
            Bytes(peer.TagEvildomInfo[0].Body, empty, "同一次登录里主动清空后重选保持空队");
            That(peer.Id == primary.Id, "空1662走完整既有小退与新名册，随后可正常重选无旧active资料残留发送");
            ReturnToSelection(peer);
            peer.Select(3, tagEvildom: [1677, 1678]); ExitRoom(peer, republish: false);
            That(fixture.Query("SELECT town FROM characters WHERE name='TagTooLow'") is [[38L]],
                "93级不因新增频道跳过Stadium原有94级准入");
        }
        using (var peer = Open(fixture, gamePort: endpoints[46], channelNumber: 46))
        {
            peer.Select(tagEvildom: [1677, 1678, 1393, 1394]);
            Bytes(peer.TagEvildomInfo[0].Body, populated, "新连接遇到空选中队时再次配第一支合格队伍");
            ValidateProjection(peer.TagEvildomInfo, populated, status, primary.Id, standby, equipment, template.EquipmentSlot);
            ReturnToSelection(peer);
            peer.Select(tagEvildom: [1677, 1678, 1393, 1394]);
            ExitRoom(peer, republish: true);
            peer.Send(15, Build(w => { w.Write(9608u); w.Write((ushort)1); })); peer.Closed();
        }
        using (var ordinary = Open(fixture))
        {
            ordinary.Select();
            ordinary.Send(1470); ordinary.Barrier();
            ordinary.Send(1662, Setting(0, 2)); ordinary.Expect(1, 1662, [0, 19, 0]); ordinary.Expect(0, 1709); ordinary.Barrier();
            ordinary.Send(15, Build(w => { w.Write(9608u); w.Write((ushort)0); })); ordinary.Closed();
        }
        using (var recovered = Open(fixture, gamePort: endpoints[46], channelNumber: 46))
        {
            recovered.Select(tagEvildom: [1677, 1678, 1393, 1394]);
            ValidateProjection(recovered.TagEvildomInfo, populated, status, primary.Id, standby, equipment, template.EquipmentSlot);
            recovered.Send(1663, [0]); recovered.Closed();
        }
        await VerifyExpiredEquipmentAsync(fixture, endpoints[46], standby);
        await VerifyExpiredStandbyAsync(fixture, resources, endpoints[46], primary, standby);
        await fixture.StopAsync();
        await VerifyMultipleTeamsAsync(source, root, resources);
        await VerifyClearAsync(source, root, resources);
        await VerifyDailyScheduleAsync(source, root, resources);
        Console.WriteLine($"PASS {Count - checks} 再战魔界真实频道、编队重置、双角色资料、准入拒绝与重登TCP断言");

        string Assets() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_skill_points ORDER BY character_id,context");
    }

    static void ReturnToSelection(TcpPeer peer) { peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2); }
    static void ExitRoom(TcpPeer peer, bool republish = false)
    {
        peer.Send(1426); peer.Expect(0, 23); peer.Expect(0, 24);
        if (republish) { peer.Expect(0, 1677); peer.Expect(0, 1678); peer.Expect(0, 1393); peer.Expect(0, 1394); }
        peer.Barrier();
    }
    // trailer 是25B16的末位u32：普通副本固定0，再战魔界/帝国竞技场实机发0xFFFFFFFF，两者都必须能入场。
    static byte[] Selection(uint dungeon, uint trailer = 0) => Build(w =>
    {
        w.Write(dungeon); w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write((byte)0);
        w.Write(ushort.MaxValue); w.Write(0u); w.Write((byte)0); w.Write(0u); w.Write((byte)0); w.Write(trailer);
    });
    static byte[] Teams(int first, int second, byte region = 2) => Build(w =>
    {
        foreach (int index in new[] { first, second, -1, -1, -1, -1 })
        { w.Write(index < 0 ? (byte)0 : region); w.Write(index); w.Write(0u); }
    });
    static byte[] Setting(int first, int second, byte region = 2) => Build(w =>
    { w.Write(Enumerable.Repeat((byte)0xAC, 13).ToArray()); w.Write(Teams(first, second, region)); w.Write(0); w.Write(0x78563412); });
    static byte[] Accepted(byte[] teams) => Build(w => { w.Write((byte)1); w.Write(new byte[13]); w.Write(teams); w.Write(0); w.Write(0); });
    static byte[] Gathering(int mode = 3) => Build(w =>
    { w.Write(0); w.Write(0); w.Write(-1); w.Write(mode); for (int i = 0; i < 6; i++) { w.Write((byte)0); w.Write(-1); } });

    static void ReadConfiguration(TcpPeer peer, byte[] teams, byte[] gathering)
    { peer.Expect(0, 1677, teams); peer.Expect(0, 1678, gathering); peer.Barrier(); }

    static void ValidateProjection(WirePacket[] packets, byte[] teams, byte[] gathering, ushort owner, Character standby, ItemCore core, byte part)
    {
        Bytes(packets[0].Body, teams, "1677真实原名册身份"); Bytes(packets[1].Body, gathering, "1678真实初始通关状态");
        Bytes(packets[2].Body, [2, 0, 2, 2], "1393先安装active双人metadata，第二成员保留原index2");
        using var input = new MemoryStream(packets[3].Body);
        using var reader = new BinaryReader(input);
        That(reader.ReadByte() == 1 && reader.ReadUInt16() == owner && reader.ReadByte() == 1 && reader.ReadByte() == 0,
            "1394当前主角隐式复用，备用一份，ignoredPrefix不是名册index");
        int width = reader.ReadInt32();
        Bytes(reader.ReadBytes(width), DfoServer.Game.Characters.CharacterName.Encoding.GetBytes(standby.Name), "备用真实CP936名称");
        Bytes(reader.ReadBytes(10), [95, 0, 0, 0, 0, 0, 0, 0, 0, 0], "备用职业/等级/当前未启用附属状态固定边界");
        That(reader.ReadInt32() == 89, "TAG属性使用真实89B组件带长度");
        byte[] attributes = reader.ReadBytes(89);
        That(U32(attributes, 0) > 0 && U32(attributes, 4) > 0 && attributes[84] == 100,
            "1394携真实资源计算HP/MP与满体力，而非四个伪造HUD整数");
        byte[] expectedTail = Build(w =>
        {
            w.Write(standby.EquipmentSlotFlags); w.Write((byte)((standby.EquipmentSlotFlags & 16) != 0 ? 1 : 0)); w.Write(0u);
            w.Write((byte)1); w.Write(part); w.Write(core.ItemId); w.Write(4); w.Write(0u);
            w.Write(new byte[3]); w.Write(0u); w.Write(0u); w.Write((byte)0);
            w.Write(0u); w.Write(new byte[4]);
            w.Write((byte)1); w.Write(part); w.Write(core.ItemId); w.Write(core.Value); w.Write((byte)0); w.Write((ushort)12);
            w.Write((ushort)0); w.Write(0u); w.Write(0u); w.Write((byte)0); w.Write(new byte[3]);
            w.Write((byte)0); w.Write(0); w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write(new byte[10]);
            w.Write(4u); w.Write(0u); w.Write(8u); w.Write(0UL); w.Write((byte)0);
            w.Write(5u); w.Write(0u); w.Write((byte)0); w.Write(1u); w.Write((byte)0); w.Write(4u); w.Write(0u);
            w.Write(0u); w.Write((byte)0);
            var tree = standby.Learning.Trees.Single(tree => tree.Context == standby.Learning.ActiveContext);
            w.Write(tree.Sp); w.Write((byte)tree.Skills.Count); w.Write(standby.Learning.ActiveContext);
            foreach (var skill in tree.Skills) { w.Write(skill.Slot); w.Write(skill.Id); w.Write(skill.Level); }
            w.Write(0u); w.Write(new byte[20 + 8 + 8]);
        });
        Bytes(reader.ReadBytes(checked((int)(input.Length - input.Position))), expectedTail,
            "独立编码非空真实装备完整核心、外观、SP、原始技能slot及TAG专用尾，不借被测Builder生成预期");
        That(standby.Learning.Trees[0].Sp > 0 && standby.Learning.Trees[0].Skills.Where((skill, index) => skill.Slot != index).Any(),
            "真实PVF用例确有非零SP及非数组序号的稳定技能槽");
    }
}
