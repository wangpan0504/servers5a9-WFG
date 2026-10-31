using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 装扮预设橱窗（#620）：0x0639/0x063A/0x063B 三笔C2S只有0x063D/0x063F回放，
// 扩展券 CMD515 action216 累加容量；选择预设按落库UID重新穿脱。
internal static class AvatarPresetTcpTests
{
    const ushort Rename = 1593, Select = 1594, Exchange = 1595, List = 1597, Update = 1599;
    const uint CapacityAction = 216;
    const int CouponId = 490704493;
    const ushort CouponSlot = 65;   // Main消耗品页首格；槽60不落在任何分类区间

    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var assets = resources.ItemResources;
        var items = assets.Items;
        var clock = new ConsumableUseSessionTests.FakeClock();
        var coupon = items.ActionConsumable(CouponId);
        That(coupon is { Label: "[add avatar preset]", Parameters: [] } definition && definition.CoolTime == 5000 &&
            definition.Places.Contains("[village]") && definition.DailyLimit == 0,
            "扩展券490704493来自当前PVF：无参数、冷却5秒、限城镇");
        // 取职业3自己的默认上衣：等级与职业位必然满足 CMD19 的穿戴判定，换其他职业的时装会被23拒绝。
        int legsId = resources.Characters.Get(3).DefaultAvatarEquipment[3];
        var legs = items.Get(legsId);
        That(legs is { Kind: ItemKind.Avatar, EquipmentSlot: 3 } && (legs.Jobs & (1 << 3)) != 0 && legs.MinimumLevel <= 60,
            $"{legsId}是职业3可穿的槽位3默认时装模板");

        // 两件同模板实例：服务端按UID区分，客户端只看itemId与外观；颜色用于证明换装换回的是原实例。
        var worn = InventoryRewardGrantService.CreateItem(legs, new(InventoryListType.Equipment, 3),
            InventoryRewardGrantService.Create(legs));
        worn = worn with { Avatar = worn.Avatar! with { Color1 = 3, Color2 = 7 } };
        var spare = InventoryRewardGrantService.CreateItem(legs, new(InventoryListType.Avatar, 0),
            InventoryRewardGrantService.Create(legs));
        spare = spare with { Avatar = spare.Avatar! with { Color1 = 11, Color2 = 13 } };

        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "avatar-preset"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        Character role;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            role = repository.Create(owner, new("PresetTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 3, Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(3).Skills,
                // 补满105格装扮包裹，真实TCP换装必须走原子互换，不能要求额外空格。
                Inventory = new([worn, spare, .. Enumerable.Range(1, 104).Select(index =>
                    InventoryRewardGrantService.CreateItem(legs, new(InventoryListType.Avatar, (short)index),
                        InventoryRewardGrantService.Create(legs))), InventoryRewardGrantService.CreateItem(coupon!.Item,
                    new(InventoryListType.Main, (short)CouponSlot), InventoryRewardGrantService.Create(coupon.Item, 2))])
                    { AvatarExpansion = 0 }
            });
        long wornUid = role.Inventory.Find(new(InventoryListType.Equipment, 3))!.Uid;
        long spareUid = role.Inventory.Find(new(InventoryListType.Avatar, 0))!.Uid;

        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            var initial = Parse(peer.AvatarPresetList);
            That(initial is { Capacity: 1, ActiveSlot: 0 } && initial.Rows.Length == 1 &&
                initial.Rows[0] is { Slot: 0, Name: "" } && initial.Rows[0].Parts.Length == 1 &&
                initial.Rows[0].Parts[0] is { Slot: 3, Clear: 0, Color1: 3, Color2: 7 } &&
                initial.Rows[0].Parts[0].ItemId == (uint)legs.Id && initial.Rows[0].Parts[0].Emblems.All(id => id is -1 or 0),
                "选角即自带1号橱窗：容量1，槽0按当前穿戴落库、空名交给客户端默认标签，先于每周地下城下发");
            peer.Barrier();

            peer.Send(Rename, NameBody(0, "预设一"));
            var renamedFirst = Parse(peer.Expect(0, List).Body);
            That(renamedFirst is { Capacity: 1, ActiveSlot: 0 } && renamedFirst.Rows.Length == 1 &&
                renamedFirst.Rows[0] is { Slot: 0, Name: "预设一" } && renamedFirst.Rows[0].Parts.Length == 1 &&
                renamedFirst.Rows[0].Parts[0].ItemId == (uint)legs.Id,
                "已有1号橱窗改名回放整表：客户端靠整表重绘行文字，部位保留当前穿戴");

            peer.Send(Rename, NameBody(0, "改名"));
            var renamed = Parse(peer.Expect(0, List).Body);
            That(renamed is { Capacity: 1, ActiveSlot: 0 } && renamed.Rows.Length == 1 &&
                renamed.Rows[0] is { Slot: 0, Name: "改名" } && renamed.Rows[0].Parts.Length == 1 &&
                renamed.Rows[0].Parts[0].ItemId == (uint)legs.Id, "重复改名仍回整表，部位不变");

            string unchanged = State();
            peer.Send(Rename, [0, 0x41]);
            peer.NoFrame(200, "畸形名字请求不回复放包", (0, List), (0, Update));
            peer.Send(Rename, NameBody(1, "越界"));
            peer.NoFrame(200, "槽位不小于容量时拒绝创建", (0, List), (0, Update));
            peer.Barrier();
            That(State() == unchanged, "拒绝的0x0639不写库、不递增revision");

            peer.Send(515, UseBody(CouponSlot));
            peer.Expect(1, 515, AckBody(CouponSlot));
            var expanded = Parse(peer.Expect(0, List).Body);
            That(expanded is { Capacity: 2, ActiveSlot: 0 } && expanded.Rows.Length == 2 && expanded.Rows[0].Name == "改名" &&
                expanded.Rows[1] is { Slot: 1, Name: "", Parts.Length: 0 },
                "扩展券成功ACK后回放容量2、原预设与第二格空占位，不改变预设内容");
            peer.Send(515, UseBody(CouponSlot));
            peer.Expect(1, 515, RejectBody(CouponSlot));
            peer.NoFrame(200, "冷却5秒内重复使用不发容量回放", (0, List));
            peer.Barrier();

            peer.Send(Rename, NameBody(1, "第二格"));
            var created = Parse(peer.Expect(0, List).Body);
            That(created is { Capacity: 2, ActiveSlot: 0 } && created.Rows.Length == 2 && created.Rows[0].Name == "改名" &&
                created.Rows[1] is { Slot: 1, Name: "第二格" } && created.Rows[1].Parts.Length == 1 &&
                created.Rows[1].Parts[0].ItemId == (uint)legs.Id,
                "容量升到2后第二格可创建，整表回放让新行名字立刻可见");
            peer.Send(Exchange, [0, 1]);
            var swapped = Parse(peer.Expect(0, List).Body);
            That(swapped.ActiveSlot == 1 && swapped.Rows.Single(row => row.Slot == 0).Name == "第二格" &&
                swapped.Rows.Single(row => row.Slot == 1).Name == "改名",
                "0x063B交换两个已占用槽的内容，槽位身份不变");
            peer.Barrier();

            // 交换后当前橱窗跟着内容走到1号；先选回0号橱窗，后面的换装才实时落在0号，
            // 1号保留旧快照，才能构造“选择预设按UID换回原实例”的场景。选中不改穿戴时没有库存差量。
            peer.Send(Select, [0]);
            var sameItems = Parse(peer.Expect(0, List).Body);
            That(sameItems is { ActiveSlot: 0, Capacity: 2 } && sameItems.Rows[0].Name == "第二格",
                "相同装扮跨槽应用仍回整表，同步当前槽、名称、高亮与应用按钮");
            peer.Barrier();
            That(fixture.Query("SELECT json_extract(avatar_preset_state,'$.ActiveSlot') FROM characters") is [[0L]],
                "交换后重新选中0号橱窗即时生效，不改动当前穿戴");

            // 把穿戴实例换到背包，让预设引用与当前外观不一致，验证选择预设会按UID换回来。
            peer.Send(19, Build(writer =>
            {
                writer.Write((byte)1); writer.Write((short)0); writer.Write(legs.Id); writer.Write(1);
                writer.Write((byte)3); writer.Write((short)3); writer.Write(legs.Id); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
            }));
            peer.Expect(1, 19, Build(writer =>
            {
                writer.Write((byte)1); writer.Write((byte)1); writer.Write((short)0); writer.Write(1);
                writer.Write((byte)3); writer.Write((short)3);
            }));
            peer.Expect(0, 1681);
            peer.Expect(0, 1681);
            // 当前橱窗跟着换装实时回写，服务端必须把新内容补回客户端缓存；否则装扮栏预览停在旧内容。
            var resampled = ParseRecord(peer.Expect(0, Update).Body);
            That(resampled is { Slot: 0, Name: "第二格" } && resampled.Parts.Length == 1 &&
                resampled.Parts[0] is { Slot: 3, Color1: 11, Color2: 13 },
                "换装后回写当前橱窗并补发0x063F单条：缓存里的实例与染色跟随新穿戴");
            That(peer.Expect(0, 2).Body[0] == 0 && peer.Expect(0, 2).Body[0] == 1, "手动互换后按Actor再Player刷新外观");
            peer.Barrier();

            peer.Send(Select, [1]);
            var avatarDelta = peer.Expect(0, 14).Body;
            That(avatarDelta[0] == 1 && U16(avatarDelta, 1) == 1 &&
                U16(avatarDelta, 3) == 0 && U32(avatarDelta, 5) == (uint)legs.Id,
                "满包选择预设直接互换，装扮栏只回放原槽最终实例，不制造额外空格");
            var equipmentDelta = peer.Expect(0, 14).Body;
            That(equipmentDelta[0] == 3 && U16(equipmentDelta, 1) == 1 && U16(equipmentDelta, 3) == 3 &&
                U32(equipmentDelta, 5) == (uint)legs.Id, "装备栏差量把预设引用的实例穿回槽位0");
            That(peer.Expect(0, 2).Body[0] == 0 && peer.Expect(0, 2).Body[0] == 1, "选择预设后按Actor再Player刷新外观");
            // 整表首字段同步已应用槽与浏览槽，预览按当前穿戴显示，不得越界回槽0。
            var applied = Parse(peer.Expect(0, List).Body);
            That(applied is { Capacity: 2, ActiveSlot: 1 } && applied.Rows.Single(row => row.Slot == 0).Name == "第二格" &&
                applied.Rows.Single(row => row.Slot == 1).Name == "改名",
                "选择预设换装后回放整表：客户端重建纸娃娃，两侧名字保持落库值");
            peer.Barrier();
            That(fixture.Query("SELECT s.item_uid FROM character_inventory_slots s WHERE s.list_type=3 AND s.slot_index=3") is [[var wornNow]] &&
                Convert.ToInt64(wornNow) == wornUid &&
                fixture.Query("SELECT s.item_uid FROM character_inventory_slots s WHERE s.list_type=1 AND s.slot_index=0") is [[var spareNow]] &&
                Convert.ToInt64(spareNow) == spareUid,
                "选择预设按UID换回原实例，不重建同模板外观");

            // 已选中橱窗后的换装要实时回写，玩家不需要再点保存/应用。
            peer.Send(19, Build(writer =>
            {
                writer.Write((byte)1); writer.Write((short)0); writer.Write(legs.Id); writer.Write(1);
                writer.Write((byte)3); writer.Write((short)3); writer.Write(legs.Id); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
            }));
            peer.Expect(1, 19, Build(writer =>
            {
                writer.Write((byte)1); writer.Write((byte)1); writer.Write((short)0); writer.Write(1);
                writer.Write((byte)3); writer.Write((short)3);
            }));
            peer.Expect(0, 1681);
            peer.Expect(0, 1681);
            var resampledActive = ParseRecord(peer.Expect(0, Update).Body);
            That(resampledActive is { Slot: 1, Name: "改名" } && resampledActive.Parts.Length == 1 &&
                resampledActive.Parts[0] is { Slot: 3, Color1: 11, Color2: 13 },
                "选中1号橱窗后的换装同样补发该槽0x063F，客户端缓存跟着当前穿戴更新");
            That(peer.Expect(0, 2).Body[0] == 0 && peer.Expect(0, 2).Body[0] == 1, "选中后换装仍按Actor再Player刷新外观");
            peer.Barrier();
            That(fixture.Query("SELECT json_extract(avatar_preset_state,'$.ActiveSlot') FROM characters") is [[1L]],
                "0x063A选中后当前橱窗随存档持久化");
            That(fixture.Query("SELECT json_extract(value,'$.Parts[0].ItemUid') FROM characters, " +
                    "json_each(avatar_preset_state,'$.Presets') WHERE json_extract(value,'$.Slot')=1") is [[var savedUid]] &&
                Convert.ToInt64(savedUid) == spareUid, "已选橱窗后换装实时回写该槽的部位UID");

            peer.Send(Rename, NameBody(1, "改名"));
            That(Parse(peer.Expect(0, List).Body) is { ActiveSlot: 1, Capacity: 2 },
                "已应用第二格时改名整表保持当前槽1，不跳回第一格");
            clock.Advance(TimeSpan.FromSeconds(6));
            peer.Send(515, UseBody(CouponSlot));
            peer.Expect(1, 515, AckBody(CouponSlot));
            var third = Parse(peer.Expect(0, List).Body);
            That(third is { Capacity: 3, ActiveSlot: 1 } && third.Rows.Length == 3 &&
                third.Rows[2] is { Slot: 2, Name: "", Parts.Length: 0 }, "冷却结束后第二张扩展券把容量升到3并补第三格空占位");

            string settled = State();
            peer.Send(Rename, NameBody(3, "越界"));
            peer.NoFrame(200, "容量3时槽位3仍越界", (0, List), (0, Update));
            peer.Send(515, UseBody(CouponSlot));
            // 源槽已空：按STK动作身份不匹配在通用层拒绝（错误码0），不会走到扩展券处理器。
            peer.Expect(1, 515, Build(writer =>
            { writer.Write((byte)0); writer.Write((ushort)0); writer.Write(CouponSlot); writer.Write((byte)0); writer.Write((byte)CapacityAction); }));
            peer.Barrier();
            That(State() == settled, "券用尽与越界创建都不写库");

            That(fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE list_type=0 AND slot_index=65") is [[0L]],
                "两张扩展券按ACK逐张消耗，源槽随第二张清空");
            That(fixture.Query("SELECT count(*) FROM character_consumable_states WHERE character_id=$p0 AND item_id=$p1",
                    role.Id, CouponId) is [[1L]], "成功使用写入会话冷却，跨重连仍生效");
        }

        That(fixture.Query("SELECT json_extract(avatar_preset_state,'$.Capacity'), count(*) FROM characters, " +
            "json_each(avatar_preset_state,'$.Presets')") is [[3L, 2L]] &&
            fixture.Query("SELECT json_extract(value,'$.Slot'), json_extract(value,'$.Name') FROM characters, " +
            "json_each(avatar_preset_state,'$.Presets') ORDER BY json_extract(value,'$.Slot')") is [[0L, "第二格"], [1L, "改名"]],
            "容量与预设按槽位落库到JSON列，重排后名字跟随槽位");

        using (var peer = Open(fixture))
        {
            peer.Select();
            var restored = Parse(peer.AvatarPresetList);
            That(restored is { Capacity: 3, ActiveSlot: 1 } && restored.Rows.Length == 3 &&
                restored.Rows[0].Name == "第二格" && restored.Rows[1].Name == "改名" &&
                restored.Rows[1].Parts.Length == 1 && restored.Rows[1].Parts[0].ItemId == (uint)legs.Id &&
                restored.Rows[2] is { Slot: 2, Name: "", Parts.Length: 0 },
                "重连后按落库容量、槽位与部位整表恢复，未用槽只补空占位");
            peer.Barrier();
        }

        string State() => fixture.Snapshot("SELECT id,revision,avatar_preset_state FROM characters") +
            fixture.Snapshot("SELECT s.list_type,s.slot_index,s.item_uid FROM character_inventory_slots s ORDER BY s.list_type,s.slot_index") +
            fixture.Snapshot("SELECT * FROM character_consumable_states ORDER BY character_id,item_id");
    }

    // 0x0639 = slot:u8 + nameLength:u32 + CP936字节（无结尾NUL），与客户端 FUN_03bdf8e0 写入一致。
    static byte[] NameBody(byte slot, string name)
    {
        var text = CharacterName.Encoding.GetBytes(name);
        var body = new byte[5 + text.Length];
        body[0] = slot;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), (uint)text.Length);
        text.CopyTo(body, 5);
        return body;
    }
    static byte[] UseBody(ushort slot) => Build(writer =>
    {
        writer.Write(slot); writer.Write((byte)0); writer.Write(0u); writer.Write(CapacityAction);
        writer.Write(0u); writer.Write(0u);
    });
    static byte[] AckBody(ushort slot) => Build(writer =>
    { writer.Write((byte)1); writer.Write(slot); writer.Write((byte)0); writer.Write(CapacityAction); });
    // 失败ACK必须带非零错误码，否则客户端不弹任何提示且看起来像没响应。
    static byte[] RejectBody(ushort slot) => Build(writer =>
    { writer.Write((byte)0); writer.Write((ushort)19); writer.Write(slot); writer.Write((byte)0); writer.Write((byte)CapacityAction); });

    static PresetRow ParseRecord(byte[] body) => Parse([0, 1, .. body]).Rows[0];

    // 0x063D：已应用槽u8、条数u8、逐条记录；条数决定容量，记录与0x063F同构。
    static PresetTable Parse(byte[] body)
    {
        byte activeSlot = body[0];
        int count = body[1], at = 2;
        var rows = new PresetRow[count];
        for (int index = 0; index < count; index++)
        {
            byte slot = body[at++];
            int nameLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at)));
            at += 4;
            string name = CharacterName.Encoding.GetString(body, at, nameLength);
            at += nameLength;
            int parts = body[at++];
            var list = new PartRow[parts];
            for (int part = 0; part < parts; part++)
            {
                var emblems = new int[5];
                for (int socket = 0; socket < 5; socket++)
                    emblems[socket] = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at + 15 + socket * 6));
                list[part] = new PartRow(body[at], BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 1)),
                    BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 5)),
                    BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at + 9)),
                    BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at + 11)), emblems);
                at += 43;
            }
            rows[index] = new PresetRow(slot, name, list);
        }
        That(at == body.Length, "装扮预设回放包长度与条目数一致");
        return new(activeSlot, rows);
    }

    readonly record struct PresetTable(byte ActiveSlot, PresetRow[] Rows)
    {
        public int Capacity => Rows.Length;
    }
    readonly record struct PresetRow(byte Slot, string Name, PartRow[] Parts);
    readonly record struct PartRow(byte Slot, uint ItemId, uint Clear, ushort Color1, ushort Color2, int[] Emblems);
}
