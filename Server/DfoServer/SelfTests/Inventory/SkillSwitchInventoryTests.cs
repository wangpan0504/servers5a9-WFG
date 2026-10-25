using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class SkillSwitchInventoryTests
{
    public static void RunCore()
    {
        int checks = Count;
        Parser();
        Catalog();
        StateAndService();
        Packets();
        Console.WriteLine($"PASS {Count - checks} Buff强化登记解析、领域与包体断言");
    }

    static void Parser()
    {
        That(SkillSwitchInventoryRequests.Parse([0x4D, 0, 36, 47, 0xFF, 0xFF]) ==
            new SkillSwitchInventoryRequest(SkillSwitchInventoryRequestKind.SetSkill, 77, 36, 47, ushort.MaxValue),
            "技能哨兵解析指定技能");
        That(SkillSwitchInventoryRequests.Parse([0xFF, 0xFF, 36, 47, 0xFF, 0xFF]).SkillId == 0,
            "技能FFFF哨兵归一为清除指定技能");
        That(SkillSwitchInventoryRequests.Parse([0x4D, 0, 4, 0, 20, 0]) ==
            new SkillSwitchInventoryRequest(SkillSwitchInventoryRequestKind.SetItem, 77, 4, 0, 20),
            "物品登记保留技能、部位、容器与槽位");
        That(SkillSwitchInventoryRequests.Parse([0x4D, 0, 4, 47, 0xFF, 0xFF]).Kind ==
            SkillSwitchInventoryRequestKind.RemoveItem,
            "物品空位置哨兵解析为移除登记");
        foreach (byte[] malformed in new byte[][]
        {
            [], [0x4D, 0, 36, 47, 0xFF], [0x4D, 0, 36, 0, 0xFF, 0xFF],
            [0x4D, 0, 4, 47, 20, 0], [0x4D, 0, 37, 0, 20, 0],
            [0x4D, 0, 4, 9, 20, 0]
        })
            Reject<InvalidDataException>(() => SkillSwitchInventoryRequests.Parse(malformed),
                "请求拒绝非六字节、不完整哨兵、越界部位与未知容器");
    }

    static void Catalog()
    {
        var sections = ImmutableArray.Create(
            new PvfTokenSection("character type", [new(7, 1), new(0, 0)], []),
            new PvfTokenSection("skill", [new(0, 77), new(0, 88)], []));
        var catalog = SwitchingSupportSkillCatalog.Parse(
            sections,
            value => value == 1 ? "[swordman]" : throw new KeyNotFoundException());
        That(catalog.Supports(0, 0, 77) && catalog.Supports(0, 16, 88) &&
            !catalog.Supports(0, 1, 77) && !catalog.Supports(1, 0, 77),
            "PVF白名单按职业与转职低四位限定指定技能");
        Reject<InvalidDataException>(() => SwitchingSupportSkillCatalog.Parse(
            [.. sections, .. sections],
            value => value == 1 ? "[swordman]" : throw new KeyNotFoundException()),
            "重复职业转职白名单明确拒绝");
        Reject<InvalidDataException>(() => SwitchingSupportSkillCatalog.Parse(
            [new PvfTokenSection("character type", [new(7, 1), new(0, 0)], []),
             new PvfTokenSection("skill", [], [])],
            value => value == 1 ? "[swordman]" : throw new KeyNotFoundException()),
            "空技能白名单不伪造登记资格");
    }

    static void StateAndService()
    {
        const int itemId = 1001, materialId = 1002;
        long nowSeconds = 2_000_000_000;
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        items.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [itemId] = new(itemId, ItemKind.Equipment, 4, 1, 10, 1, 0, 0, 0,
                "[trade]", ushort.MaxValue, 0, "switch-equipment"),
            [materialId] = new(materialId, ItemKind.Material, 4, 1, 0, 1000, 0, 0, 0,
                "[trade]", ushort.MaxValue, 0, "switch-material")
        });
        var profiles = ImmutableDictionary<(byte Job, byte Grow), ImmutableHashSet<ushort>>.Empty
            .Add((0, 0), ImmutableHashSet.Create<ushort>(77));
        var service = new SkillSwitchInventoryService(
            items,
            new SwitchingSupportSkillCatalog(profiles),
            new FixedClock(DateTimeOffset.FromUnixTimeSeconds(nowSeconds)));
        var equipment = new InventoryItem(501, new(InventoryListType.Main, 20),
            new(itemId, ItemKind.Equipment, 1));
        Character character = new(1, 0, "SkillSwitch", 3, new Location(38, 1, 450, 234, 0), null)
        {
            Job = 0,
            GrowType = 0,
            Skills = [new CharacterSkill(77, 1)],
            Inventory = new([equipment])
        };

        var skill = service.Plan(character,
            new(SkillSwitchInventoryRequestKind.SetSkill, 77, 36, 47, ushort.MaxValue));
        That(skill.Accepted && skill.Plan is { Changed: true, After.SkillId: 77 },
            "已学且命中职业白名单的技能可登记");
        That(service.Plan(character, new(SkillSwitchInventoryRequestKind.SetSkill, 88, 36, 47, ushort.MaxValue)).Failure ==
            SkillSwitchInventoryFailure.SkillNotLearned,
            "未学习技能不能指定");
        That(service.Plan(character with { Skills = [new CharacterSkill(88, 1)] },
            new(SkillSwitchInventoryRequestKind.SetSkill, 88, 36, 47, ushort.MaxValue)).Failure ==
            SkillSwitchInventoryFailure.SkillNotSupported,
            "已学但不在PVF白名单的技能不能指定");

        character = character with { SkillSwitchInventory = skill.Plan!.After };
        var registered = service.Plan(character,
            new(SkillSwitchInventoryRequestKind.SetItem, 77, 4, 0, 20));
        That(registered.Accepted && registered.Plan is { Changed: true } &&
            registered.Plan.After.Items.Single() == new SkillSwitchItemBinding(4, 501, itemId),
            "登记物品保存稳定UID、模板与部位而非临时槽位");
        SkillSwitchInventoryState state = registered.Plan!.After;
        foreach (var position in new InventorySlot[]
        {
            new(InventoryListType.Main, 21), new(InventoryListType.Equipment, 4)
        })
        {
            var moved = new InventorySnapshot([equipment with { Position = position }]);
            That(state.Reconcile(moved, character.Skills).Same(state) &&
                state.Locate(moved).Single().Item.Position == position,
                "携带或穿戴移动保留稳定UID登记并解析当前位置");
        }
        var stored = new InventorySnapshot([equipment with { Position = new(InventoryListType.PersonalCargo, 0) }]);
        That(state.Reconcile(stored, character.Skills).Same(state) && state.Locate(stored).IsEmpty,
            "个人仓库保留同角色UID登记配置，但不能投影为有效装备");
        That(state.Reconcile(stored, character.Skills).Locate(character.Inventory).Single().Item.Uid == equipment.Uid,
            "取回同一UID后恢复携带登记，不重新绑定同模板其他实例");
        var accountStored = new InventorySnapshot([equipment with { Position = new(InventoryListType.AccountCargo, 0) }]);
        That(state.Reconcile(accountStored, character.Skills).Items.IsEmpty && state.Locate(accountStored).IsEmpty,
            "账号仓库不保留角色登记，其他角色不能继承绑定");
        That(service.Plan(character with { Inventory = stored },
            new(SkillSwitchInventoryRequestKind.SetItem, 77, 4, 2, 0)).Failure ==
            SkillSwitchInventoryFailure.ItemLocationInvalid,
            "仓库请求保持合法协议，但不能登记未携带装备");
        That(state.Reconcile(new([equipment with { Uid = equipment.Uid + 1 }]), character.Skills).Items.IsEmpty,
            "同槽同模板的新实例不能继承售出或销毁物品的登记");
        That(state.Reconcile(InventorySnapshot.Empty, character.Skills).Items.IsEmpty,
            "售出或销毁后没有原实例即清除登记");
        That(state.Reconcile(new([equipment with { Core = equipment.Core with { ItemId = itemId + 1 } }]),
            character.Skills).Items.IsEmpty,
            "实例模板变化清除原登记绑定");
        That(state.Reconcile(character.Inventory, []).Same(SkillSwitchInventoryState.Empty),
            "指定技能失学清除技能与全部登记物品");

        That(service.Plan(character, new(SkillSwitchInventoryRequestKind.SetItem, 77, 5, 0, 20)).Failure ==
            SkillSwitchInventoryFailure.ItemPartMismatch,
            "请求部位必须匹配PVF模板部位");
        That(service.Plan(character, new(SkillSwitchInventoryRequestKind.SetItem, 77, 4, 17, 4)).Failure ==
            SkillSwitchInventoryFailure.ItemLocationInvalid,
            "协议合法但当前没有权威容器的列表按业务拒绝");
        var expired = equipment with { Core = equipment.Core with { ExpireTime = checked((int)nowSeconds) } };
        That(service.Plan(character with { Inventory = new([expired]) },
            new(SkillSwitchInventoryRequestKind.SetItem, 77, 4, 0, 20)).Failure ==
            SkillSwitchInventoryFailure.ItemExpired,
            "到期实例不能登记");
        var stacked = new InventoryItem(502, new(InventoryListType.Main, 21),
            new(materialId, ItemKind.Material, 2));
        That(service.Plan(character with { Inventory = new([stacked]) },
            new(SkillSwitchInventoryRequestKind.SetItem, 77, 4, 0, 21)).Failure ==
            SkillSwitchInventoryFailure.ItemStacked,
            "堆叠实例不能登记");
        That(service.Plan(character with
        {
            Inventory = new([stacked with { Core = stacked.Core with { Value = 1 } }])
        }, new(SkillSwitchInventoryRequestKind.SetItem, 77, 4, 0, 21)).Failure ==
            SkillSwitchInventoryFailure.ItemPartMismatch,
            "非装备种类不能利用默认部位伪装成Buff强化登记项");
        var duplicateState = new SkillSwitchInventoryState(77,
            [new SkillSwitchItemBinding(5, equipment.Uid, equipment.Core.ItemId)]);
        That(service.Plan(character with { SkillSwitchInventory = duplicateState },
            new(SkillSwitchInventoryRequestKind.SetItem, 77, 4, 0, 20)).Failure ==
            SkillSwitchInventoryFailure.DuplicateItem,
            "同一UID不能同时绑定多个部位");
        var unchanged = service.Plan(character with { SkillSwitchInventory = state },
            new(SkillSwitchInventoryRequestKind.SetItem, 77, 4, 0, 20));
        That(unchanged.Accepted && unchanged.Plan is { Changed: false },
            "重复登记返回权威成功但不产生状态变化");
    }

    static void Packets()
    {
        var item = new InventoryItem(501, new(InventoryListType.Main, 20),
            new(1001, ItemKind.Equipment, 1));
        var inventory = new InventorySnapshot([item]);
        var state = new SkillSwitchInventoryState(77,
            [new SkillSwitchItemBinding(4, item.Uid, item.Core.ItemId)]);
        That(SkillSwitchInventoryPackets.Ack().Length == 0,
            "命令ACK正文为空");
        That(Convert.ToHexString(SkillSwitchInventoryPackets.Delta(state, inventory, [4])) ==
            "010104001400",
            "差量携带存在标志、部位和当前位置");
        That(Convert.ToHexString(SkillSwitchInventoryPackets.Delta(
            new SkillSwitchInventoryState(77, []), InventorySnapshot.Empty, [4])) ==
            "0100042FFFFF",
            "差量用47与FFFF声明部位已移除");
        That(Convert.ToHexString(SkillSwitchInventoryPackets.AllData(state, inventory)) ==
            "4D00010104001400",
            "ALLDATA携带指定技能及排序后的全部位置");

        var stored = new InventorySnapshot([item with { Position = new(InventoryListType.PersonalCargo, 0) }]);
        That(Convert.ToHexString(SkillSwitchInventoryPackets.Delta(state, stored, [4])) == "0100042FFFFF" &&
            Convert.ToHexString(SkillSwitchInventoryPackets.AllData(state, stored)) == "4D0000",
            "旧仓库绑定的差量与全量均投影为空，不泄漏可生效位置");
        using (var storedStream = new MemoryStream())
        using (var storedWriter = new BinaryWriter(storedStream))
        {
            SkillSwitchInventoryPackets.WriteUserInfo(storedWriter, state, stored, 2_000_000_000);
            That(Convert.ToHexString(storedStream.ToArray()) == "4D0000",
                "重登本人资料不回放仓库中的旧登记实例");
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        SkillSwitchInventoryPackets.WriteUserInfo(writer, state, inventory, 2_000_000_000);
        byte[] userInfo = stream.ToArray();
        That(userInfo.Length == 130 &&
            Convert.ToHexString(userInfo.AsSpan(0, 13)) == "4D0001000014001400E9030000",
            "USERINFO登记项包含技能、数量、外层位置及123字节物品核心");

        var avatar = new InventoryItem(502, new(InventoryListType.Avatar, 3),
            new(1002, ItemKind.Avatar, 1))
        {
            Avatar = new()
        };
        using var avatarStream = new MemoryStream();
        using var avatarWriter = new BinaryWriter(avatarStream);
        SkillSwitchInventoryPackets.WriteUserInfo(
            avatarWriter,
            new(77, [new(4, avatar.Uid, avatar.Core.ItemId)]),
            new([avatar]),
            2_000_000_000);
        That(avatarStream.Length == 172,
            "时装登记项在123字节核心后追加RAW30与RAW4可选块");
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
