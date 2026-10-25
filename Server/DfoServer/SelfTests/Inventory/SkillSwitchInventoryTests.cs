using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Accounts;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
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
        PresetPersistence();
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
            new PvfTokenSection("skill", [new(0, 77), new(0, 800), new(0, 88), new(0, 801)], []));
        var catalog = SwitchingSupportSkillCatalog.Parse(
            sections,
            value => value == 1 ? "[swordman]" : throw new KeyNotFoundException());
        That(catalog.Supports(0, 0, 77) && catalog.Supports(0, 16, 88) &&
            !catalog.Supports(0, 1, 77) && !catalog.Supports(1, 0, 77) && !catalog.Supports(0, 0, 800),
            "PVF白名单按职业与转职低四位限定指定技能");
        var single = SwitchingSupportSkillCatalog.Parse(
            [sections[0], new PvfTokenSection("skill", [new(0, 77), new(0, 800)], [])], _ => "[swordman]");
        That(single.Skills(0, 0).SetEquals(new ushort[] { 77 }), "单候选声明不把链接列作为第二候选");
        Reject<InvalidDataException>(() => SwitchingSupportSkillCatalog.Parse(
            [sections[0], new PvfTokenSection("skill", [new(0, 77)], [])],
            _ => "[swordman]"), "不完整技能与链接编号对明确拒绝");
        foreach (var pair in new[] { (Grow: (byte)1, First: (ushort)45, Second: (ushort)258),
            (Grow: (byte)3, First: (ushort)47, Second: (ushort)48) })
        {
            var candidates = SwitchingSupportSkillCatalog.Parse(
                [new("character type", [new(7, 1), new(0, pair.Grow)], []),
                 new("skill", [new(0, pair.First), new(0, 836), new(0, pair.Second), new(0, 838)], [])],
                _ => "[priest]");
            var service = new SkillSwitchInventoryService(null!, candidates);
            Character character = new(1, 0, "Preset", 3, new Location(38, 1, 450, 234, 0), null)
                { Job = 4, GrowType = pair.Grow, Skills = [] };
            foreach (ushort id in new[] { pair.First, pair.Second, (ushort)0 })
            {
                var result = service.Plan(character, new(SkillSwitchInventoryRequestKind.SetSkill, id, 36, 47, ushort.MaxValue));
                That(result.Accepted && result.Plan!.After.SkillId == id && character.Skills.Length == 0,
                    "多候选职业未学时可选择、切换及取消偏好，不授予技能");
                character = character with { SkillSwitchInventory = result.Plan!.After };
            }
            foreach (ushort id in new ushort[] { 836, 838, 999 })
                That(service.Plan(character, new(SkillSwitchInventoryRequestKind.SetSkill, id, 36, 47, ushort.MaxValue)).Failure ==
                    SkillSwitchInventoryFailure.SkillNotSupported, "链接编号与任意技能不获得候选资格");
            That(service.Plan(character with { Job = 0 },
                new(SkillSwitchInventoryRequestKind.SetSkill, pair.First, 36, 47, ushort.MaxValue)).Failure ==
                SkillSwitchInventoryFailure.SkillNotSupported, "另一职业不能保存该预设");
        }
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
            SkillSwitchInventoryFailure.SkillNotSupported,
            "未学习且不在职业候选中的技能不能指定");
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
            That(state.Reconcile(moved).Same(state) &&
                state.Locate(moved).Single().Item.Position == position,
                "携带或穿戴移动保留稳定UID登记并解析当前位置");
        }
        var stored = new InventorySnapshot([equipment with { Position = new(InventoryListType.PersonalCargo, 0) }]);
        That(state.Reconcile(stored).Same(state) && state.Locate(stored).IsEmpty,
            "个人仓库保留同角色UID登记配置，但不能投影为有效装备");
        That(state.Reconcile(stored).Locate(character.Inventory).Single().Item.Uid == equipment.Uid,
            "取回同一UID后恢复携带登记，不重新绑定同模板其他实例");
        var accountStored = new InventorySnapshot([equipment with { Position = new(InventoryListType.AccountCargo, 0) }]);
        That(state.Reconcile(accountStored).Items.IsEmpty && state.Locate(accountStored).IsEmpty,
            "账号仓库不保留角色登记，其他角色不能继承绑定");
        That(service.Plan(character with { Inventory = stored },
            new(SkillSwitchInventoryRequestKind.SetItem, 77, 4, 2, 0)).Failure ==
            SkillSwitchInventoryFailure.ItemLocationInvalid,
            "仓库请求保持合法协议，但不能登记未携带装备");
        That(state.Reconcile(new([equipment with { Uid = equipment.Uid + 1 }])).Items.IsEmpty,
            "同槽同模板的新实例不能继承售出或销毁物品的登记");
        That(state.Reconcile(InventorySnapshot.Empty).Items.IsEmpty,
            "售出或销毁后没有原实例即清除登记");
        That(state.Reconcile(new([equipment with { Core = equipment.Core with { ItemId = itemId + 1 } }])).Items.IsEmpty,
            "实例模板变化清除原登记绑定");
        That(state.Reconcile(character.Inventory).Same(state),
            "库存核对不根据技能学习状态清除目标偏好");

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

    static void PresetPersistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-buff-preset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            using var owner = new AccountOwners().Acquire("buff-preset", () => { });
            var learning = new CharacterLearningState(0,
                [new(0, 0, 0, [new(0, 1, 1)]), new(1, 0, 0, [new(0, 1, 1)])])
                { BonusSp = 0, BonusTp = 0 };
            var character = repository.Create(owner, new("PresetDb", new Location(38, 1, 450, 234, 0), null)
                { Job = 4, GrowType = 1, Skills = [new(1, 1)], InitialLearning = learning });
            var beforeLevel = character;
            repository.UpdateProgress(owner, new(CharacterProgressField.Level, owner.Account, character.Name, 2, 0));
            character = repository.CommitSkillSwitchInventory(owner,
                new(beforeLevel, new(45, []), null, "preset_test"));
            That(character.Level == 2 && character.SkillSwitchInventory.SkillId == 45 &&
                !character.Skills.Any(skill => skill.Id == 45), "同revision等级变化不阻止合法偏好，保存不授予技能");
            character = repository.CommitLearning(owner, character, character.Learning with
            {
                Trees = character.Learning.Trees.Select(tree => tree with { Skills = [new(0, 1, 1), new(1, 45, 1)] }).ToArray()
            });
            character = repository.UpdateProgress(owner,
                new(CharacterProgressField.Skill, owner.Account, character.Name, 45, 2));
            character = repository.UpdateProgress(owner,
                new(CharacterProgressField.Skill, owner.Account, character.Name, 45, 0));
            That(character.SkillSwitchInventory.SkillId == 45 && !character.Skills.Any(skill => skill.Id == 45),
                "GM修改和删除学习等级不抹合法目标偏好");
            character = repository.CommitLearning(owner, character, character.Learning with
            {
                Trees = character.Learning.Trees.Select(tree => tree with { Skills = [new(0, 1, 1), new(1, 45, 1)] }).ToArray()
            });
            character = repository.CommitLearning(owner, character, character.Learning with
            {
                Trees = character.Learning.Trees.Select(tree => tree with { Skills = [new(0, 1, 1)] }).ToArray()
            });
            That(character.SkillSwitchInventory.SkillId == 45 && !character.Skills.Any(skill => skill.Id == 45),
                "学习再洗点移除已学等级时保留目标偏好");
            character = repository.ResetProgress(owner,
                new(character, 3, character.GrowType, character.Learning));
            That(character.Level == 3 && character.SkillSwitchInventory.SkillId == 45,
                "正式GM等级重置入口保留同职业偏好");
            character = repository.UpdateProgress(owner,
                new(CharacterProgressField.Grow, owner.Account, character.Name, 17, 0));
            That(character.SkillSwitchInventory.SkillId == 45, "同分支觉醒不清除偏好");
            using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "game.db")};Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_preset_reset BEFORE DELETE ON character_skill_switch_inventory BEGIN SELECT RAISE(ABORT,'preset reset rollback'); END";
                command.ExecuteNonQuery();
                Reject<SqliteException>(() => repository.UpdateProgress(owner,
                    new(CharacterProgressField.Grow, owner.Account, character.Name, 19, 0)),
                    "职业变更登记清理失败不能留下半提交职业");
                command.CommandText = "DROP TRIGGER fail_preset_reset";
                command.ExecuteNonQuery();
            }
            var restored = repository.LoadCharacter(owner, character.Slot);
            That(restored.GrowType == 17 && restored.SkillSwitchInventory.SkillId == 45,
                "职业变更失败回滚grow与登记配置");
            var stale = character;
            character = repository.UpdateProgress(owner,
                new(CharacterProgressField.Grow, owner.Account, character.Name, 19, 0));
            That(character.SkillSwitchInventory.Same(SkillSwitchInventoryState.Empty), "真实职业分支改变清除原职业偏好");
            var staleEmpty = stale with { SkillSwitchInventory = SkillSwitchInventoryState.Empty };
            Reject<InvalidOperationException>(() => repository.CommitSkillSwitchInventory(owner,
                new(staleEmpty, new(45, []), null, "preset_stale")),
                "同revision且原登记均为空时，旧职业计划仍不能越过候选资格");
            character = repository.CommitSkillSwitchInventory(owner,
                new(character, new(47, []), null, "preset_new_profession"));
            character = repository.ResetProgress(owner, new(character, character.Level, 17, character.Learning));
            That(character.SkillSwitchInventory.Same(SkillSwitchInventoryState.Empty),
                "正式GM职业重置入口清除旧分支偏好");
        }
        finally { Directory.Delete(root, true); }
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
