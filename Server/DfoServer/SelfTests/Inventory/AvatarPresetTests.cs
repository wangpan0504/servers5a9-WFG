using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class AvatarPresetTests
{
    public static void RunCore()
    {
        int checks = Count;
        Names();
        Packets();
        State();
        Service();
        Capacity();
        Console.WriteLine($"PASS {Count - checks} 装扮预设请求、回包、容量与穿脱规划断言");
    }

    static void Names()
    {
        That(AvatarPresetRequests.Name(Body(0, "装扮预设")) == new AvatarPresetNameRequest(0, "装扮预设"),
            "名字请求保留槽位与CP936名称");
        That(AvatarPresetRequests.Name(Body(5, "A")) == new AvatarPresetNameRequest(5, "A"), "最短单字符名字合法");
        // 实机正文（08:16:44 / 08:17:54）：slot + u32字节长度 + CP936，无结尾NUL。
        That(AvatarPresetRequests.Name([0, 3, 0, 0, 0, 0x31, 0x31, 0x31]) == new AvatarPresetNameRequest(0, "111"),
            "实机改名正文 slot0 + 长度3 + \"111\" 可解析");
        That(AvatarPresetRequests.Name([0, 0x0B, 0, 0, 0, 0xD0, 0xC2, 0xD7, 0xB0, 0xB0, 0xE7, 0xD4, 0xA4, 0xC9, 0xE8, 0x33])
            == new AvatarPresetNameRequest(0, "新装扮预设3"), "实机正文按CP936解出中文名字");
        That(AvatarPresetRequests.Name(Body(3, new string('名', 127))).Name.Length == 127,
            "127个字符是客户端S2C读取上限(小于0x100字节)内的最长名字");
        // 长度0是把名字恢复成客户端默认标签，不是畸形请求。
        That(AvatarPresetRequests.Name([0, 0, 0, 0, 0]) == new AvatarPresetNameRequest(0, ""), "空名请求合法");
        foreach (byte[] invalid in new[]
        {
            Array.Empty<byte>(),
            new byte[] { 0 },
            new byte[] { 0, 0x41 },
            new byte[] { 0, 1, 0, 0, 0 },                     // 缺名字字节
            new byte[] { 0, 2, 0, 0, 0, 0x41 },               // 长度与正文不符
            new byte[] { 0, 1, 0, 0, 0, 0x41, 0x42 },         // 尾随字节
            new byte[] { 0, 0xFF, 0xFF, 0xFF, 0xFF },         // 长度越界
            new byte[] { 0, 1, 0, 0, 0, 0x00 },               // 控制字符
            new byte[] { 0, 1, 0, 0, 0, 0x81 },               // 不完整的CP936前导字节
            Body(0, new string('名', 128))                     // 超过127，S2C长度会达到0x100
        })
            Reject<InvalidDataException>(() => AvatarPresetRequests.Name(invalid), "畸形名字请求必须拒绝");
        That(AvatarPresetRequests.Select([2]) == new AvatarPresetSlotRequest(2), "选择请求是单字节槽位");
        That(AvatarPresetRequests.Exchange([1, 4]) == new AvatarPresetExchangeRequest(1, 4), "交换请求是两个槽位");
        Reject<InvalidDataException>(() => AvatarPresetRequests.Select([]), "选择槽位不能缺字节");
        Reject<InvalidDataException>(() => AvatarPresetRequests.Select([1, 2]), "选择槽位不能带尾随字节");
        Reject<InvalidDataException>(() => AvatarPresetRequests.Exchange([1]), "交换槽位不能缺字节");
        Reject<InvalidDataException>(() => AvatarPresetRequests.Exchange([1, 2, 3]), "交换槽位不能带尾随字节");
    }

    static void Packets()
    {
        var part = new AvatarPresetPart(4, 99, 0x01020304, 0, 0x1122, 0x3344,
            [new(0, 0), new(1, 42), new(2, -1), new(3, 7), new(4, 0)]);
        var preset = new AvatarPreset(2, "A", [part]);
        byte[] record =
        [
            2, 1, 0, 0, 0, 0x41, 1, 4,
            4, 3, 2, 1,
            0, 0, 0, 0,
            0x22, 0x11,
            0x44, 0x33,
            0, 0, 0, 0, 0, 0,
            1, 0, 42, 0, 0, 0,
            2, 0, 255, 255, 255, 255,
            3, 0, 7, 0, 0, 0,
            4, 0, 0, 0, 0, 0
        ];
        Bytes(AvatarPresetPackets.Update(preset), record,
            "0x063F单条记录：槽位、u32名称字节数、CP936名字、部位，itemId在前clearAvatarId在后");
        // 中文名逐字节锁定：客户端读取器按u32字节数原样复制、显示侧按CP936解码；
        // 写成UTF-16LE时实机会把字节按GBK显示成乱码（「新装扮预设1」变成「麤餍nb糪縓3」）。
        Bytes(AvatarPresetPackets.Update(new AvatarPreset(0, "新装扮预设1", [])),
        [
            0, 11, 0, 0, 0,
            0xD0, 0xC2, 0xD7, 0xB0, 0xB0, 0xE7, 0xD4, 0xA4, 0xC9, 0xE8, 0x31,
            0
        ], "中文名按CP936写出u32字节数前缀，不带结尾NUL");
        // 空名写长度0：客户端FUN_03be25f0走回退分支，显示自己的默认标签（0xB743）。
        Bytes(AvatarPresetPackets.Update(new AvatarPreset(0, "", [])), [0, 0, 0, 0, 0, 0],
            "空名写长度0与零部位，客户端回退到默认标签");
        var state = new AvatarPresetState(3, [preset]);
        Bytes(AvatarPresetPackets.List(state), [3, 3, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, .. record],
            "0x063D整表：容量与容量等长的条目数，槽2真实记录前后各补空名零部位占位");
        Bytes(AvatarPresetPackets.List(AvatarPresetState.Empty), [1, 1, 0, 0, 0, 0, 0, 0],
            "空预设也回放一条空占位，客户端按记录条数画格子");
    }

    static void State()
    {
        var part = new AvatarPresetPart(0, 7, 1001, 0, 0, 0,
            [AvatarSocket.Empty(0), AvatarSocket.Empty(1), AvatarSocket.Empty(2), AvatarSocket.Empty(3), AvatarSocket.Empty(4)]);
        var preset = new AvatarPreset(0, "外观", [part]);
        new AvatarPresetState(1, [preset]).Validate();
        new AvatarPresetState(1, [preset], 0).Validate();
        AvatarPresetState.Empty.Validate();
        Reject<InvalidDataException>(() => new AvatarPresetState(1, [preset], 1).Validate(), "当前橱窗槽位必须小于容量");
        Reject<InvalidDataException>(() => new AvatarPresetState(7, []).Validate(), "容量上限6");
        Reject<InvalidDataException>(() => new AvatarPresetState(1, [preset, preset with { Name = "重复" }]).Validate(),
            "同槽位不能出现两条预设");
        Reject<InvalidDataException>(() => new AvatarPresetState(1, [preset with { Slot = 1 }]).Validate(),
            "槽位必须小于容量");
        new AvatarPreset(0, "", []).Validate();   // 空名合法：客户端显示自己的默认标签
        Reject<InvalidDataException>(() => new AvatarPreset(0, " ", []).Validate(), "纯空白名拒绝");
        Reject<InvalidDataException>(() => new AvatarPreset(0, " 名", []).Validate(), "名字不允许首尾空白");
        Reject<InvalidDataException>(() => new AvatarPreset(0, new string('名', 128), []).Validate(), "名字超过127拒绝");
        Reject<InvalidDataException>(() => new AvatarPreset(0, "\uD800", []).Validate(), "孤立代理不能按替换字符静默上网");
        Reject<InvalidDataException>(() => new AvatarPreset(0, "a\tb", []).Validate(), "控制字符拒绝");
        Reject<InvalidDataException>(() => new AvatarPresetPart(0, 0, 1001, 0, 0, 0, part.Sockets).Validate(), "部位UID必须为正");
        Reject<InvalidDataException>(() => new AvatarPresetPart(0, 7, 0, 0, 0, 0, part.Sockets).Validate(), "部位物品编号必须为正");
        Reject<InvalidDataException>(() => new AvatarPresetPart(0, 7, 1001, 0, 0, 0, [AvatarSocket.Empty(0)]).Validate(),
            "部位必须带完整五个孔");
        Reject<InvalidDataException>(() => new AvatarPresetPart(0, 7, 1001, 0, 0, 0,
            [new(0, -2), AvatarSocket.Empty(1), AvatarSocket.Empty(2), AvatarSocket.Empty(3), AvatarSocket.Empty(4)]).Validate(),
            "徽章编号不能小于-1");
        var second = new AvatarPreset(2, "外观2", []);
        var first = new AvatarPreset(0, "外观1", []);
        var ordered = AvatarPresetState.Empty.With(second).With(first);
        That(ordered.Presets.Select(row => row.Slot).SequenceEqual(new byte[] { 0, 2 }) &&
            ordered.Without(0).Presets.Single().Slot == 2 && ordered.Find(1) is null,
            "写入按槽位排序，删除只移除指定槽，空洞保持为空");
        That(ordered.Same(ordered) && !ordered.Same(ordered.With(new AvatarPreset(1, "外观3", []))),
            "预设差异参与库存变更判定");
        // 补发 0x063F 的判断必须按内容：重新采样必然产生新数组，引用相等会把同内容误判成变化。
        var contentState = new AvatarPresetState(1, [preset], 0);
        var resampled = new AvatarPresetState(1, [preset with { Parts = [part with { Sockets = [.. part.Sockets] }] }], 0);
        That(contentState.ContentChangedSlots(resampled).Length == 0, "内容相同但重新采样的预设不补发单条更新");
        That(contentState.ContentChangedSlots(contentState).Length == 0, "未改预设的库存变化共用同一份数组，直接跳过比较");
        That(contentState.ContentChangedSlots(new AvatarPresetState(1, [preset with { Parts = [part with { Color1 = 9 }] }], 0))
            .SequenceEqual(new byte[] { 0 }), "染色变化标记该槽需要补发");
        That(contentState.ContentChangedSlots(new AvatarPresetState(1, [preset with { Parts = [] }], 0))
            .SequenceEqual(new byte[] { 0 }), "部位清空同样标记补发");
        That(contentState.ContentChangedSlots(AvatarPresetState.Empty).SequenceEqual(new byte[] { 0 }) &&
            AvatarPresetState.Empty.ContentChangedSlots(contentState).SequenceEqual(new byte[] { 0 }),
            "记录被删除或新建都标记该槽");
        var json = JsonSerializer.Serialize(new AvatarPresetState(2, [preset], 0));
        That(JsonSerializer.Deserialize<AvatarPresetState>(json) is { Capacity: 2, ActiveSlot: 0 } restored &&
            restored.Find(0) is { Name: "外观", Parts.Length: 1 } row && row.Parts[0].ItemUid == 7,
            "JSON列往返保留容量、当前橱窗、槽位、名称与内部UID");
        That(JsonSerializer.Deserialize<AvatarPresetState>("{\"Capacity\":1,\"Presets\":[]}") is { Capacity: 1, ActiveSlot: null },
            "旧存档没有ActiveSlot时按未选中读取");
        // 上一版存档的部位没有来源字段：反序列化按0读取，应用时回退到物品自身仍有效的来源。
        var partJson = JsonSerializer.Serialize(new AvatarPresetPart(0, 7, 2001, 1001, 5, 6,
            [AvatarSocket.Empty(0), AvatarSocket.Empty(1), AvatarSocket.Empty(2), AvatarSocket.Empty(3), AvatarSocket.Empty(4)], 31));
        That(partJson.Contains("\"ClearAvatarSourceUid\":31") &&
            JsonSerializer.Deserialize<AvatarPresetPart>(partJson.Replace(",\"ClearAvatarSourceUid\":31", "")) is
                { ClearAvatarId: 1001, ClearAvatarSourceUid: 0 },
            "来源UID参与JSON列往返，旧存档缺字段时按0读取");
    }

    static void Service()
    {
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        ItemTemplate Avatar(int id, byte slot) => new(id, ItemKind.Avatar, slot, 0, 0, 1, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "avatar");
        ItemTemplate Clone(int id, byte slot) => new(id, ItemKind.Avatar, slot, 0, 0, 1, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "avatar")
            { CloneAvatar = true, BorrowAvatarAppearance = true };
        items.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [1001] = Avatar(1001, 0), [1002] = Avatar(1002, 1), [1003] = Avatar(1003, 0), [1004] = Avatar(1004, 5),
            [2001] = Clone(2001, 0), [2002] = Clone(2002, 1)
        });
        var service = new AvatarPresetService(items);
        var worn0 = new InventoryItem(10, new(InventoryListType.Equipment, 0), new(1001, ItemKind.Avatar, 10))
            { Avatar = new() { Color1 = 5, Color2 = 6 } };
        var worn1 = new InventoryItem(11, new(InventoryListType.Equipment, 1), new(1002, ItemKind.Avatar, 11)) { Avatar = new() };
        var bagA = new InventoryItem(12, new(InventoryListType.Avatar, 0), new(1002, ItemKind.Avatar, 12)) { Avatar = new() };
        var bagB = new InventoryItem(13, new(InventoryListType.Avatar, 1), new(1003, ItemKind.Avatar, 13)) { Avatar = new() };
        var bagC = new InventoryItem(14, new(InventoryListType.Avatar, 2), new(1004, ItemKind.Avatar, 14)) { Avatar = new() };
        var character = Role(worn0, worn1, bagA, bagB);

        var basePlan = service.PlanEnsureBase(character);
        That(basePlan.Accepted && basePlan.Plan is { Reason: "avatar_preset_base" } ensure &&
            ensure.After.AvatarPresets.Find(0) is { Name: "", Parts.Length: 2 } basePreset &&
            basePreset.Parts[0] is { Slot: 0, ItemUid: 10 } && basePreset.Parts[1] is { Slot: 1, ItemUid: 11 } &&
            ensure.After.AvatarPresets.ActiveSlot == 0,
            "1号橱窗在槽0缺失时按当前穿戴自动建立，并作为当前橱窗实时跟踪穿戴");
        That(service.PlanEnsureBase(character with { Inventory = basePlan.Plan!.After }).Plan is { Changed: false },
            "已有1号橱窗且已选中时不重复写库");
        var afterBaseMove = InventoryMoveService.Plan(character with { Inventory = basePlan.Plan.After },
            new InventoryMoveRequest(new(InventoryListType.Equipment, 0), 1001, 1, new(InventoryListType.Avatar, 3), 0, 0, 0, 0, 0), items, 0);
        That(AvatarPresetService.SyncActive(afterBaseMove).Plan!.After.AvatarPresets.Find(0) is { Parts.Length: 1 } autoSaved &&
            autoSaved.Parts[0].ItemUid == 11,
            "登录即默认选中1号橱窗：之后的换装自动保存到第一套，不需要先点选");
        // 旧存档（从未点选过橱窗）登录时把当前穿戴补存进1号橱窗并记为当前橱窗。
        var legacy = character with
        {
            Inventory = character.Inventory with
            {
                AvatarPresets = AvatarPresetState.Empty.With(new AvatarPreset(0, "装扮预设1", []))
            }
        };
        That(service.PlanEnsureBase(legacy).Plan is { Changed: true, Reason: "avatar_preset_base_active" } renamedBase &&
            renamedBase.After.AvatarPresets is { ActiveSlot: 0 } &&
            renamedBase.After.AvatarPresets.Find(0) is { Name: "", Parts.Length: 2 },
            "旧构建写入的默认名清空，并按当前穿戴补存1号橱窗且记为当前橱窗");
        var newerLegacy = legacy with
        {
            Inventory = legacy.Inventory with
            {
                AvatarPresets = AvatarPresetState.Empty.With(new AvatarPreset(0, "新装扮预设1", []))
            }
        };
        That(service.PlanEnsureBase(newerLegacy).Plan?.After.AvatarPresets.Find(0) is { Name: "", Parts.Length: 2 },
            "上一轮构建写入的「新装扮预设N」同样清空，服务端不再自造名字");
        var customName = legacy with
        {
            Inventory = legacy.Inventory with
            {
                AvatarPresets = AvatarPresetState.Empty.With(new AvatarPreset(0, "我的搭配", []))
            }
        };
        That(service.PlanEnsureBase(customName).Plan is { Changed: true } customBase &&
            customBase.After.AvatarPresets.Find(0) is { Name: "我的搭配", Parts.Length: 2 } &&
            customBase.After.AvatarPresets.ActiveSlot == 0,
            "玩家自定义名字不因默认名归一被改写，当前穿戴同样补存为1号橱窗");
        That(service.PlanEnsureBase(Role()).Plan?.After.AvatarPresets is { ActiveSlot: 0 } emptyBase &&
            emptyBase.Find(0) is { Parts.Length: 0 },
            "未穿戴时装时1号橱窗是空预设，同样是当前橱窗");

        var created = service.PlanName(character, 0, "外观一");
        var plan = created.Plan!;
        That(created.Accepted && plan is { Reason: "avatar_preset_name" } &&
            plan.After.AvatarPresets.Find(0) is { Name: "外观一", Parts.Length: 2 } preset &&
            preset.Parts[0] is { Slot: 0, ItemUid: 10, ItemId: 1001, Color1: 5, Color2: 6 } &&
            preset.Parts[1] is { Slot: 1, ItemUid: 11, ItemId: 1002 },
            "空槽0x0639按当前穿戴创建预设，部位取槽位顺序与完整外观");
        That(plan.Before.Find(worn0.Position) == worn0 && plan.After.Find(worn0.Position) == worn0,
            "创建预设不改动已穿戴实例");
        var bare = Role();
        That(service.PlanName(bare, 0, "空套").Plan?.After.AvatarPresets.Find(0) is { Parts.Length: 0 },
            "未穿戴时装时允许创建空预设");
        That(!service.PlanName(character, 1, "越界").Accepted, "槽位不小于容量时拒绝创建");
        var existing = character with
        {
            Inventory = character.Inventory with
            {
                AvatarPresets = AvatarPresetState.Empty.With(new AvatarPreset(0, "旧名", [Part(0, 13, 1003)]))
            }
        };
        var renamed = service.PlanName(existing, 0, "新名");
        That(renamed.Accepted && renamed.Plan!.After.AvatarPresets.Find(0) is { Name: "新名", Parts.Length: 1 } kept &&
            kept.Parts[0].ItemUid == 13, "已有槽0x0639只改名，不覆盖原部位");

        var exchanged = service.PlanExchange(existing with { Inventory = existing.Inventory with
            { AvatarPresets = AvatarPresetState.Empty with { Capacity = 2, Presets =
                [new AvatarPreset(0, "甲", []), new AvatarPreset(1, "乙", [])] } } }, 0, 1);
        That(exchanged.Accepted && exchanged.Plan!.After.AvatarPresets.Find(0)?.Name == "乙" &&
            exchanged.Plan.After.AvatarPresets.Find(1)?.Name == "甲", "两个已占用槽交换完整内容");
        var moved = service.PlanExchange(existing with { Inventory = existing.Inventory with
            { AvatarPresets = AvatarPresetState.Empty with { Capacity = 3, Presets = [new AvatarPreset(0, "甲", [])] } } }, 0, 2);
        That(moved.Accepted && moved.Plan!.After.AvatarPresets.Find(2)?.Name == "甲" &&
            moved.Plan.After.AvatarPresets.Find(0) is null, "目标槽为空时把预设移到空洞");
        That(service.PlanExchange(existing, 0, 0).Accepted, "同槽交换是无变化成功回执");
        That(!service.PlanExchange(existing with { Inventory = existing.Inventory with
            { AvatarPresets = AvatarPresetState.Empty with { Capacity = 2, Presets = [] } } }, 0, 1).Accepted,
            "源槽为空时交换拒绝");
        That(!service.PlanExchange(existing, 0, 1).Accepted, "目标槽不小于容量时交换拒绝");

        var target = existing with { Inventory = existing.Inventory with
            { AvatarPresets = AvatarPresetState.Empty.With(new AvatarPreset(0, "换装",
                [Part(0, 13, 1003), Part(1, 12, 1002)])) } };
        var wornPlan = service.PlanSelect(target, 0);
        var wear = wornPlan.Plan!;
        That(wornPlan.Accepted && wear is { Reason: "avatar_preset_select" } &&
            wear.After.Find(new(InventoryListType.Equipment, 0))?.Uid == 13 &&
            wear.After.Find(new(InventoryListType.Equipment, 1))?.Uid == 12 &&
            wear.After.Items.Single(item => item.Uid == 10).Position.List == InventoryListType.Avatar &&
            wear.After.Items.Single(item => item.Uid == 11).Position.List == InventoryListType.Avatar &&
            wear.After.Find(new(InventoryListType.Avatar, 2))?.Uid == 10 &&
            wear.After.Find(new(InventoryListType.Avatar, 3))?.Uid == 11,
            "选择预设先卸下不匹配时装到空格，再按UID穿齐目标部位");
        That(wear.Before.Find(worn0.Position) == worn0, "选择预设计划不原地修改当前快照");
        var missing = target with { Inventory = target.Inventory with
            { AvatarPresets = AvatarPresetState.Empty.With(new AvatarPreset(0, "缺件", [Part(0, 999, 1003)])) } };
        That(!service.PlanSelect(missing, 0).Accepted, "预设引用的时装已不存在时整笔拒绝");
        var wrongKind = Role(worn0, worn1, bagA, bagB, bagC) with
        {
            Inventory = character.Inventory with
            {
                Items = [.. character.Inventory.Items, bagC],
                AvatarPresets = AvatarPresetState.Empty.With(new AvatarPreset(0, "错位", [Part(0, 14, 1004)]))
            }
        };
        That(!service.PlanSelect(wrongKind, 0).Accepted, "模板部位与预设槽位不符时拒绝，不穿一半");
        var emptySelect = service.PlanSelect(character, 0);
        That(emptySelect.Accepted && emptySelect.Plan is { Reason: "avatar_preset_select" } emptyPlan &&
            emptyPlan.After.AvatarPresets.Find(0) is { Name: "", Parts.Length: 0 } &&
            emptyPlan.After.AvatarPresets.ActiveSlot == 0 &&
            emptyPlan.After.Items.All(item => item.Position.List != InventoryListType.Equipment),
            "选择未保存的新格等价于空预设：卸下全部装扮并成为当前橱窗");
        That(!service.PlanSelect(character, 1).Accepted, "槽位不小于容量时选择拒绝");

        var activeState = AvatarPresetState.Empty with
        {
            Capacity = 2, ActiveSlot = 0,
            Presets = [new AvatarPreset(0, "甲", []), new AvatarPreset(1, "乙", [])]
        };
        var activeSwap = service.PlanExchange(existing with
            { Inventory = existing.Inventory with { AvatarPresets = activeState } }, 0, 1);
        That(activeSwap.Accepted && activeSwap.Plan!.After.AvatarPresets.ActiveSlot == 1,
            "交换槽位时当前橱窗跟随内容移动");

        var activeCharacter = character with
        {
            Inventory = character.Inventory with
            {
                AvatarPresets = AvatarPresetState.Empty.With(new AvatarPreset(0, "当前",
                    [Part(0, 10, 1001), Part(1, 11, 1002)])) with { ActiveSlot = 0 }
            }
        };
        var unequip = InventoryMoveService.Plan(activeCharacter, new InventoryMoveRequest(
            new(InventoryListType.Equipment, 0), 1001, 1, new(InventoryListType.Avatar, 4), 0, 0, 0, 0, 0), items, 0);
        That(unequip.Accepted, "卸下装扮的移动规划成功");
        var synced = AvatarPresetService.SyncActive(unequip);
        That(synced.Plan!.After.AvatarPresets.Find(0) is { Parts.Length: 1 } syncedPreset &&
            syncedPreset.Parts[0].ItemUid == 11 && synced.Plan.After.AvatarPresets.ActiveSlot == 0,
            "已选橱窗后卸下装扮实时回写该槽部位");
        var inactive = activeCharacter with
        {
            Inventory = activeCharacter.Inventory with
            { AvatarPresets = activeCharacter.Inventory.AvatarPresets with { ActiveSlot = null } }
        };
        var untouched = AvatarPresetService.SyncActive(InventoryMoveService.Plan(inactive, new InventoryMoveRequest(
            new(InventoryListType.Equipment, 0), 1001, 1, new(InventoryListType.Avatar, 4), 0, 0, 0, 0, 0), items, 0));
        That(untouched.Plan!.After.AvatarPresets.Find(0)!.Parts.Length == 2, "未选中橱窗时换装不修改预设");

        var alreadyWorn = character with { Inventory = character.Inventory with
            { AvatarPresets = AvatarPresetState.Empty.With(new AvatarPreset(0, "当前",
                [Part(0, 10, 1001), Part(1, 11, 1002)])) } };
        var noop = service.PlanSelect(alreadyWorn, 0);
        That(noop.Accepted && noop.Plan!.After.Items.SequenceEqual(noop.Plan.Before.Items),
            "预设与当前穿戴一致时成功但不搬动任何实例");

        // 稀有克隆、梦幻克隆武器与转职光环装扮走同一机制：外观编号、来源实例与染色记在物品与预设上，
        // 切到别的橱窗再切回来必须原样恢复；来源实例只在仍是装扮背包里的同模板原件时回写。
        var lookSource = new InventoryItem(31, new(InventoryListType.Avatar, 0), new(1001, ItemKind.Avatar, 31))
            { Avatar = new() { Color1 = 2 } };
        var cloneWorn = new InventoryItem(30, new(InventoryListType.Equipment, 0), new(2001, ItemKind.Avatar, 30))
            { Avatar = new() { ClearAvatarId = 1001, ClearAvatarSourceUid = 31, Color1 = 5, Color2 = 6 } };
        var clonePeer = new InventoryItem(32, new(InventoryListType.Equipment, 1), new(2002, ItemKind.Avatar, 32)) { Avatar = new() };
        var cloneBase = Role(lookSource, cloneWorn, clonePeer);
        var cloneRole = cloneBase with
        {
            Inventory = cloneBase.Inventory with { AvatarPresets = AvatarPresetState.Empty with { Capacity = 2 } }
        };
        var saved = service.PlanName(cloneRole, 1, "克隆套");
        var savedCharacter = cloneRole with { Inventory = saved.Plan!.After };
        That(saved.Accepted && saved.Plan.After.AvatarPresets.Find(1) is { Parts.Length: 2 } cloneRow &&
            cloneRow.Parts[0] is { Slot: 0, ItemUid: 30, ItemId: 2001, ClearAvatarId: 1001, ClearAvatarSourceUid: 31, Color1: 5, Color2: 6 },
            "创建预设时克隆部位保存外观编号、来源实例UID与染色");
        var cleared = service.PlanSelect(savedCharacter, 0);
        var clearedCharacter = cloneRole with { Inventory = cleared.Plan!.After };
        That(cleared.Accepted && cleared.Plan.After.Items.All(item => item.Position.List != InventoryListType.Equipment) &&
            cleared.Plan.After.Items.Single(item => item.Uid == 30).Avatar is { ClearAvatarId: 1001, ClearAvatarSourceUid: 31 },
            "选走空橱窗卸下克隆时外观留在物品自身，不随穿戴栏清空");
        var back = service.PlanSelect(clearedCharacter, 1);
        var backPlan = back.Plan!;
        That(back.Accepted && backPlan.After.Find(new(InventoryListType.Equipment, 0)) is
                { Avatar: { ClearAvatarId: 1001, ClearAvatarSourceUid: 31, Color1: 5, Color2: 6 } } &&
            backPlan.After.AvatarPresets.Find(1)?.Parts[0] is { ClearAvatarId: 1001, ClearAvatarSourceUid: 31 },
            "切回克隆橱窗重新穿戴后外观、来源与染色不变，预设重新采样仍带来源");
        var stripped = backPlan.After with
        {
            Items = [.. backPlan.After.Items.Select(item => item.Uid == 30
                ? item with { Avatar = item.Avatar! with { ClearAvatarId = 0, ClearAvatarSourceUid = 0, Color1 = 0, Color2 = 0 } }
                : item)]
        };
        var repaired = service.PlanSelect(cloneRole with { Inventory = stripped }, 1);
        That(repaired.Accepted && repaired.Plan!.After.Find(new(InventoryListType.Equipment, 0))!.Avatar is
                { ClearAvatarId: 1001, ClearAvatarSourceUid: 31, Color1: 5, Color2: 6 },
            "旧逻辑清过的克隆按预设快照回写外观、来源与染色");
        var backRow = backPlan.After.AvatarPresets.Find(1)!;
        var staleRow = backRow with { Parts = [backRow.Parts[0] with { ClearAvatarSourceUid = 999 }, backRow.Parts[1]] };
        var stale = service.PlanSelect(cloneRole with
            { Inventory = stripped with { AvatarPresets = stripped.AvatarPresets.With(staleRow) } }, 1);
        That(stale.Accepted && stale.Plan!.After.Find(new(InventoryListType.Equipment, 0))!.Avatar is
                { ClearAvatarId: 1001, ClearAvatarSourceUid: 0 },
            "预设来源UID已不在装扮背包时回写外观但清空来源，不指向错误实例");
        var legacyRow = backRow with { Parts = [backRow.Parts[0] with { ClearAvatarSourceUid = 0 }, backRow.Parts[1]] };
        var legacyCharacter = cloneRole with
            { Inventory = backPlan.After with { AvatarPresets = backPlan.After.AvatarPresets.With(legacyRow) } };
        var legacyApplied = service.PlanSelect(legacyCharacter, 1);
        That(legacyApplied.Accepted && legacyApplied.Plan!.After.Find(new(InventoryListType.Equipment, 0))!.Avatar is
                { ClearAvatarId: 1001, ClearAvatarSourceUid: 31 },
            "旧预设没有来源字段时保留物品自身仍有效的来源，不因回写丢来源表");
    }

    static void Capacity()
    {
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var coupon = new ItemTemplate(900, ItemKind.Consumable, 0, 1, 0, 100, 0, 0, 0, "[account]", ushort.MaxValue, 0, "装扮预设橱窗扩展券");
        items.PrefillTemplates(new Dictionary<int, ItemTemplate> { [900] = coupon });
        var definition = new ActionConsumableDefinition(coupon, "[add avatar preset]", [], 0, -900, 0, 0, ["[village]"]);
        That(definition.AllowsPlace("[village]") && definition.AllowsPlace("[seria room]") && !definition.AllowsPlace("[dungeon]"),
            "扩展券的地点是城镇，赛丽亚房间属于城镇");
        var service = new AvatarPresetService(items);
        var source = new InventorySlot(InventoryListType.Main, 3);
        var character = Role(new InventoryItem(20, source, new(900, ItemKind.Consumable, 2)));
        var result = service.PlanCapacity(character, source, definition, inTown: true);
        That(result.Accepted && result.Plan is { Reason: "avatar_preset_capacity" } plan &&
            plan.After.AvatarPresets.Capacity == 2 && plan.After.Find(source) is { Core.Value: 1 } &&
            plan.CharacterPrecondition is not null,
            "扩展券+1容量与扣券同一计划，默认容量1升到2");
        That(!service.PlanCapacity(character with { Inventory = character.Inventory with
            { AvatarPresets = new AvatarPresetState(AvatarPresetState.MaximumCapacity, []) } }, source, definition, true).Accepted,
            "容量已满6时拒绝且不扣券");
        var fifth = service.PlanCapacity(character with { Inventory = character.Inventory with
            { AvatarPresets = new AvatarPresetState(5, []) } }, source, definition, true);
        That(fifth.Accepted && fifth.Plan!.After.AvatarPresets.Capacity == 6, "容量5升到上限6");
        That(!service.PlanCapacity(character, source, definition with { Label = "[add mileage]", Parameters = [10] }, true).Accepted,
            "标签不是装扮预设扩展券时拒绝");
        That(!service.PlanCapacity(character, source, definition, inTown: false).Accepted, "副本内不能使用城镇扩展券");

        Character Role(params InventoryItem[] rows) => new(1, 0, "Preset", 0, new(0, 0, 0, 0), null)
        {
            Level = 1,
            Inventory = new([.. rows])
        };
    }

    static AvatarPresetPart Part(byte slot, long uid, uint itemId) =>
        new(slot, uid, itemId, 0, 0, 0, [AvatarSocket.Empty(0), AvatarSocket.Empty(1), AvatarSocket.Empty(2), AvatarSocket.Empty(3), AvatarSocket.Empty(4)]);

    static Character Role(params InventoryItem[] rows) => new(1, 0, "Preset", 0, new(0, 0, 0, 0), null)
    {
        Level = 1,
        Inventory = new([.. rows])
    };

    static byte[] Body(byte slot, string name)
    {
        var text = CharacterName.Encoding.GetBytes(name);
        var body = new byte[5 + text.Length];
        body[0] = slot;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), (uint)text.Length);
        text.CopyTo(body, 5);
        return body;
    }
}
