using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ClearAvatarTests
{
    public static IReadOnlyList<ItemTemplate> RunAllSourceTemplates(PvfArchive archive, ItemCatalog items)
    {
        int matched = 0;
        var slots = new HashSet<byte>();
        var weapons = new List<ItemTemplate>();
        foreach (var (id, relative) in PvfScript.List(archive.ReadText("equipment/equipment.lst")))
        {
            string path = "equipment/" + relative;
            if (!archive.Contains(path)) continue;
            string text = archive.ReadText(path);
            if (!text.Contains("`clear avatar`", StringComparison.Ordinal) && !text.Contains("[clear avatar]", StringComparison.Ordinal)) continue;
            var template = items.Get(id);
            if (!template.CloneAvatar) continue;
            That(template.Kind == ItemKind.Avatar && template.EquipmentSlot <= 10,
                "每个实际克隆模板均映射到已支持的来源部位：" + id);
            var source = Avatar(1, new(InventoryListType.Avatar, 209), id, new());
            var clone = Avatar(2, new(InventoryListType.Equipment, template.EquipmentSlot), id,
                new() { ClearAvatarId = id, ClearAvatarSourceUid = 1 });
            byte[] body = ClearAvatarPackets.Sources(new([source, clone]));
            That(body.Length == 34 && body[1 + template.EquipmentSlot * 3] == template.EquipmentSlot &&
                BitConverter.ToUInt16(body, 2 + template.EquipmentSlot * 3) == 221,
                "每个实际克隆模板以原生三字节记录绑定包裹末格原件：" + id);
            slots.Add(template.EquipmentSlot);
            if (template.EquipmentSlot == 10) weapons.Add(template);
            matched++;
        }
        That(matched > 0 && slots.IsSupersetOf(Enumerable.Range(0, 8).Select(slot => (byte)slot)) &&
            slots.Contains(9) && slots.Contains(10), "名册逐项覆盖全部实际克隆族及普通八部位、光环和武器");
        Console.WriteLine($"Clone source templates checked: {matched}; slots: {string.Join(',', slots.Order())}");
        return weapons;
    }

    // 只选真实名册扫描提供的候选，不为适配模板改变TCP角色职业、等级或技能。
    internal static ItemTemplate? SelectEmptyWeaponTemplate(IEnumerable<ItemTemplate> templates) => templates
        .Where(template => template is { Kind: ItemKind.Avatar, EquipmentSlot: 10, CloneAvatar: true,
            BorrowAvatarAppearance: true, MinimumLevel: <= 95, RequiredSkill: 0, PvpFreeEquipment: false } &&
            (template.Jobs & 1) != 0)
        .MinBy(template => template.Id);

    public static void RunResources(CharacterCatalog characters, ItemCatalog items)
    {
        That(characters.Get(2).DefaultAvatarEquipment.GetValueOrDefault((byte)3) == 48600 &&
            characters.Get(2).DefaultAvatarEquipment.GetValueOrDefault((byte)8) == 50200 &&
            characters.Get(3).DefaultAvatarEquipment.GetValueOrDefault((byte)3) == 52600,
            "真实职业ETC的默认上衣、皮肤按EQU部位装配，不把顺序表的其它整数当作物品");
        That(items.Get(101500878) is { CloneAvatar: true, BorrowAvatarAppearance: true, EquipmentSlot: 3 } &&
            items.Get(113590006) is { CloneAvatar: true, BorrowAvatarAppearance: true, EquipmentSlot: 9 } &&
            !items.Get(115580222).CloneAvatar, "真实透明上衣及光环接入关联，random clear avatar不误入透明路径");
    }

    public static void RunCore()
    {
        int checks = Count;
        RunWeaponFixtureSelection();
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var templates = Enumerable.Range(0, 12).SelectMany(slot => new[] { Template(slot), Template(slot, true) })
            .Append(new ItemTemplate(300, ItemKind.Equipment, 12, 1, 10, 1, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "weapon"));
        catalog.PrefillTemplates(templates.ToDictionary(item => item.Id));
        var details = new AvatarDetail { Color1 = 7, Color2 = 8, Socket0 = new(2, 888), ExpireTime = 2000000000 };
        foreach (short slot in Enumerable.Range(0, 12).Select(slot => (short)slot))
        {
            var bag = Avatar(11, new(InventoryListType.Avatar, 0), Template(slot, true).Id, details);
            bag = bag with { Core = bag.Core with { Attr = 15, Durability = 4, SealFlag = 1, EnchantCardId = 10001,
                EnchantUpgrade = 1, AmplifyType = 3, AmplifyValue = 150, Random0 = new(7, 9, 10),
                RandomState = 3, RandomChangedIndex = byte.MaxValue } };
            var original = Avatar(12, new(InventoryListType.Equipment, slot), Template(slot).Id,
                details with { Color1 = 12, Color2 = 13 });
            var role = Role([bag, original]);
            var result = Move(role, bag.Position, original.Position);
            var worn = result.Plan!.After.Find(original.Position)!;
            That(worn.Avatar is { ClearAvatarId: var id, Color1: 12, Color2: 13, Socket0.EmblemId: 888 } &&
                id == original.Core.ItemId && worn.Avatar.ClearAvatarSourceUid == original.Uid &&
                worn.Uid == bag.Uid && worn.Core == bag.Core,
                "所有装扮槽包含皮肤、真实光环、武器及既有外观槽，关联外观不替换物品核心或镶嵌");
            var next = Avatar(13, new(InventoryListType.Avatar, 1), bag.Core.ItemId, details with { Color1 = 1 });
            role = role with { Inventory = result.Plan.After with { Items = result.Plan.After.Items.Add(next) } };
            result = Move(role, next.Position, original.Position);
            worn = result.Plan!.After.Find(original.Position)!;
            That(worn.Avatar!.ClearAvatarId == original.Core.ItemId && worn.Avatar.ClearAvatarSourceUid == original.Uid && worn.Avatar.Color1 == 12 &&
                result.Plan.After.Find(next.Position)!.Avatar is { ClearAvatarId: var keptId, ClearAvatarSourceUid: var keptSource, Color1: 12 } &&
                keptId == original.Core.ItemId && keptSource == original.Uid,
                "连续克隆继承原始外观和染色，被退回包裹的克隆保留已复制的外观与来源");
            role = role with { Inventory = result.Plan.After };
            result = Move(role, original.Position, new(InventoryListType.Avatar, 2));
            That(result.Plan!.After.Find(new(InventoryListType.Avatar, 2)) is { } unequipped &&
                unequipped.Avatar is { ClearAvatarId: var unequippedId, ClearAvatarSourceUid: var unequippedSource, Color1: 12 } &&
                unequippedId == original.Core.ItemId && unequippedSource == original.Uid &&
                result.Plan.After.Find(original.Position) is null,
                "卸装保留物品已复制的外观、来源与染色，不留下假穿戴物品");
            role = role with { Inventory = result.Plan.After };
            result = Move(role, new(InventoryListType.Avatar, 2), original.Position);
            That(result.Plan!.After.Find(original.Position)!.Avatar is { ClearAvatarId: var reusedId, ClearAvatarSourceUid: var reusedSource, Color1: 12 } &&
                reusedId == original.Core.ItemId && reusedSource == original.Uid,
                "卸下再穿回空槽仍保留已复制的外观与来源，国服外观记在克隆物品本身");
            role = Role([bag with { Avatar = details with { ClearAvatarId = 300, ClearAvatarSourceUid = 99, Color1 = 12 } }]);
            result = Move(role, bag.Position, original.Position);
            That(result.Plan!.After.Find(original.Position)!.Avatar is { ClearAvatarId: 300, ClearAvatarSourceUid: 99, Color1: 12, Color2: 8 },
                "空槽穿戴保留物品自身已复制的外观、来源与染色，不借用职业默认EQU");
            role = role with { Inventory = result.Plan.After with { Items = result.Plan.After.Items.Add(next) } };
            result = Move(role, next.Position, original.Position);
            That(result.Plan!.After.Find(original.Position)!.Avatar is { ClearAvatarId: 300, ClearAvatarSourceUid: 99 } &&
                result.Plan.After.Find(next.Position)!.Avatar is { ClearAvatarId: 300, ClearAvatarSourceUid: 99 },
                "无外观克隆替换有外观克隆时继承其关联，被替换的克隆保留自身外观");
            var emptyWorn = Avatar(14, new(InventoryListType.Equipment, 3), Template(3, true).Id, details with { ClearAvatarId = 0 });
            var emptyBag = Avatar(15, new(InventoryListType.Avatar, 0), Template(3, true).Id, details with { ClearAvatarId = 0 });
            result = Move(Role([emptyBag, emptyWorn]), emptyBag.Position, emptyWorn.Position);
            That(result.Plan!.After.Find(emptyWorn.Position)!.Avatar is { ClearAvatarId: 0, ClearAvatarSourceUid: 0 },
                "无来源克隆替换无来源克隆仍保持空关联，不制造默认物品来源");
            var lookBuyer = Avatar(16, new(InventoryListType.Avatar, 5), Template(3, true).Id,
                details with { ClearAvatarId = 777, ClearAvatarSourceUid = 88 });
            var realAvatar = Avatar(17, new(InventoryListType.Equipment, 3), Template(3).Id, details with { Color1 = 3 });
            result = Move(Role([lookBuyer, realAvatar]), lookBuyer.Position, realAvatar.Position);
            That(result.Plan!.After.Find(realAvatar.Position)!.Avatar is { ClearAvatarId: 103, ClearAvatarSourceUid: 17, Color1: 3 },
                "已复制外观的克隆重新覆盖真实装扮时更新来源和染色");
            foreach (byte growth in new byte[] { 1, 0x11, 0x21 })
            {
                role = Role([bag]) with { GrowType = growth };
                result = Move(role, bag.Position, original.Position);
                That(result.Plan!.After.Find(original.Position) is { } native && native.Uid == bag.Uid &&
                    native.Core == bag.Core && native.Avatar is { ClearAvatarSourceUid: 0 } nativeDetail &&
                    nativeDetail.ClearAvatarId == 0 && result.Plan.After.Items.Length == 1,
                    "转职及两次觉醒均不创建基础外观物品或固定任何空槽来源，完整克隆实例不变");
                role = Role([bag, original]) with { GrowType = growth };
                result = Move(role, bag.Position, original.Position);
                That(result.Plan!.After.Find(original.Position)!.Avatar is { ClearAvatarSourceUid: 12 } explicitSource &&
                    explicitSource.ClearAvatarId == original.Core.ItemId,
                    "觉醒只影响无原件的基础外观，不覆盖真实原件来源");
            }
        }
        var weapon = new InventoryItem(21, new(InventoryListType.Equipment, 12), new(300, ItemKind.Equipment, 0));
        var clearWeapon = Avatar(22, new(InventoryListType.Avatar, 0), Template(10, true).Id, details);
        var weaponRole = Role([weapon, clearWeapon]);
        var weaponResult = Move(weaponRole, clearWeapon.Position, new(InventoryListType.Equipment, 10));
        That(weaponResult.Plan!.After.Find(new(InventoryListType.Equipment, 10))!.Avatar!.ClearAvatarId == 0 &&
            weaponResult.Plan.After.Find(new(InventoryListType.Equipment, 10))!.Avatar!.ClearAvatarSourceUid == 0 &&
            weaponResult.Plan.After.Find(weapon.Position) == weapon, "有实际武器时空武器装扮也保持空来源，不固定普通装备外观");
        var replacement = weapon with { Uid = 24, Position = new(InventoryListType.Main, 9), Core = weapon.Core with { Attr = 7, EnchantCardId = 10001 } };
        weaponRole = weaponRole with { Inventory = weaponResult.Plan.After with
            { Items = weaponResult.Plan.After.Items.Add(replacement), Skins = new([32000], 32000) } };
        weaponResult = Move(weaponRole, replacement.Position, weapon.Position);
        That(weaponResult.Plan!.After.Find(new(InventoryListType.Equipment, 10))!.Avatar is { ClearAvatarId: 0, ClearAvatarSourceUid: 0 } &&
            weaponResult.Plan.After.Find(weapon.Position)!.Core == replacement.Core && weaponResult.Plan.After.Skins.Selected == 32000,
            "更换真实武器保留空克隆关联及完整属性，不把普通武器或独立幻化写入克隆来源");
        var ordinaryWeaponAvatar = Avatar(23, new(InventoryListType.Equipment, 10), Template(10).Id, details);
        weaponRole = Role([weapon, clearWeapon, ordinaryWeaponAvatar]);
        weaponResult = Move(weaponRole, clearWeapon.Position, ordinaryWeaponAvatar.Position);
        That(weaponResult.Plan!.After.Find(ordinaryWeaponAvatar.Position)!.Avatar!.ClearAvatarId == ordinaryWeaponAvatar.Core.ItemId,
            "武器装扮优先于实际武器作为被覆盖来源，不混用皮肤仓库外观字段");

        var sealedAvatar = Avatar(24, new(InventoryListType.Equipment, 3), 103, details) with
            { Core = new(103, ItemKind.Avatar, 24) { SealFlag = 1 } };
        using (var stream = new MemoryStream())
        {
            using var writer = new BinaryWriter(stream);
            EquipmentProtocolWriter.Player(writer, new([sealedAvatar]), 1000);
            That(BitConverter.ToInt32(stream.ToArray(), 15) == 0,
                "Player装扮关联为零时不能把SealFlag误投影成EQU编号");
        }
        var originalSource = Avatar(31, new(InventoryListType.Avatar, 4), 103, details);
        var duplicate = Avatar(32, new(InventoryListType.Avatar, 0), 103, details);
        var linked = Avatar(33, new(InventoryListType.Equipment, 3), 203,
            details with { ClearAvatarId = 103, ClearAvatarSourceUid = originalSource.Uid });
        var sourceInventory = new InventorySnapshot([originalSource, duplicate, linked]);
        var sourcePacket = ClearAvatarPackets.Sources(sourceInventory);
        That(sourcePacket.Length == 34 && sourcePacket[0] == 11 && BitConverter.ToUInt16(sourcePacket, 11) == 16,
            "1449来源值按真实UID定位原件格加12，不把同模板副本或模板编号作为来源");
        sourceInventory = sourceInventory with { Items = [duplicate, linked, originalSource with { Position = new(InventoryListType.Avatar, 8) }] };
        That(BitConverter.ToUInt16(ClearAvatarPackets.Sources(sourceInventory), 11) == 20,
            "原件移格后按提交后库存解析来源，不沿用穿戴时的旧槽号");
        foreach (var invalid in new[] { originalSource with { Position = new(InventoryListType.PersonalCargo, 0) },
            originalSource with { Uid = 34 }, originalSource with { Core = originalSource.Core with { ItemId = 104 } } })
        {
            That(BitConverter.ToUInt16(ClearAvatarPackets.Sources(new([duplicate, linked, invalid])), 11) == 0,
                "原件不在装扮包裹、UID或模板不匹配时清除客户端来源，不回退到同模板副本");
        }
        Reject<InvalidDataException>(() => (details with { ClearAvatarSourceUid = -1 }).Validate(), "克隆来源UID不得为负");
        var allSources = Enumerable.Range(0, 11).SelectMany(slot => new[]
        {
            Avatar(100 + slot, new(InventoryListType.Avatar, (short)(slot * 2)), 100 + slot, details),
            Avatar(200 + slot, new(InventoryListType.Equipment, (short)slot), 200 + slot,
                details with { ClearAvatarId = 100 + slot, ClearAvatarSourceUid = 100 + slot })
        }).ToImmutableArray();
        using (var reader = new BinaryReader(new MemoryStream(ClearAvatarPackets.Sources(new(allSources)))))
        {
            That(reader.ReadByte() == 11, "来源表完整清除并重建十一部位");
            for (byte slot = 0; slot < 11; slot++)
                That(reader.ReadByte() == slot && reader.ReadUInt16() == slot * 2 + 12,
                    "按原客户端u8/u16读取每个部位的真实来源，不能仅首条或零值偶然对齐");
            That(reader.BaseStream.Position == reader.BaseStream.Length, "来源表在34B精确闭合，无遗留高位字节");
        }
        RunPlayerExtensions(details);
        RunRepository(catalog, details);
        Console.WriteLine($"PASS {Count - checks} clear avatar覆盖、默认外观、完整资产及事务断言");

        InventoryResult Move(Character role, InventorySlot from, InventorySlot to)
        {
            var result = InventoryMoveService.Plan(role, new(from, role.Inventory.Find(from)!.Core.ItemId, 1,
                to, role.Inventory.Find(to)?.Core.ItemId ?? 0, 0, -1, 0, 0), catalog, 1000);
            That(result.Accepted, "合法装扮覆盖、卸装和空槽穿戴均可规划");
            return result;
        }
    }

    static void RunWeaponFixtureSelection()
    {
        var valid = Template(10, true) with { MinimumLevel = 95, Jobs = 1 };
        var other = valid with { Id = valid.Id + 1 };
        That(SelectEmptyWeaponTemplate([other, valid]) == valid &&
            SelectEmptyWeaponTemplate([valid, other]) == valid,
            "真实武器克隆候选按编号确定选择，不依赖名册枚举顺序，95级和本职业边界可用");
        foreach (var invalid in new[] { valid with { Kind = ItemKind.Equipment }, valid with { EquipmentSlot = 9 },
            valid with { CloneAvatar = false }, valid with { BorrowAvatarAppearance = false },
            valid with { MinimumLevel = 96 }, valid with { Jobs = 2 }, valid with { Jobs = 0 },
            valid with { RequiredSkill = 1 }, valid with { PvpFreeEquipment = true } })
            That(SelectEmptyWeaponTemplate([invalid]) is null &&
                SelectEmptyWeaponTemplate([invalid, other]) == other,
                "不以错误类型、部位、克隆标记或不可穿戴模板填补空武器TCP覆盖");
        That(SelectEmptyWeaponTemplate([]) is null, "无兼容真实候选时保持缺失，不合成替代模板");
    }

    static void RunPlayerExtensions(AvatarDetail details)
    {
        var slots = new short[] { 3, 8, 9, 10 };
        var items = slots.Select(slot => Avatar(slot + 100, new(InventoryListType.Equipment, slot), slot + 200,
            details with { ExpireTime = 0, ClearAvatarId = 0x181C2E70 + slot }) with
        {
            Core = new(slot + 200, ItemKind.Avatar, slot + 100)
            {
                Extension97 = 0x12345678, GuardianGemKeys = 0x1122334455667788, Extension109 = 3,
                Extension110 = 0x33333333, Extension114 = new(0x44, 0x55667788),
                Extension119 = 0x99, Extension120 = 0xAA, Extension121 = 0xBBCC
            }
        }).ToImmutableArray();
        var body = DfoServer.Testing.TcpPeer.Build(writer => EquipmentProtocolWriter.Player(writer, new(items), 1000));
        using var reader = new BinaryReader(new MemoryStream(body));
        That(reader.ReadByte() == 4, "连续Player装扮记录包含上衣、皮肤、光环和武器装扮");
        foreach (var slot in slots)
        {
            long start = reader.BaseStream.Position;
            That(reader.ReadByte() == slot, "上一记录的扩展不能挤占下一部位");
            reader.ReadBytes(11);
            That(reader.ReadUInt16() == 0xBBCC && reader.ReadInt32() == 0x181C2E70 + slot,
                "Player前缀保留完整两字节扩展及32位来源编号");
            if (slot == 9)
            {
                That(reader.ReadUInt32() == 30, "克隆光环保留来源资源的额外孔位块");
                reader.ReadBytes(30);
            }
            reader.ReadBytes(8 + 42 + 19);
            That(reader.ReadUInt32() == 4 && reader.ReadUInt32() == 0x12345678 &&
                reader.ReadUInt32() == 8 && reader.ReadUInt64() == 0x1122334455667788 && reader.ReadByte() == 3,
                "Player前两扩展及中间字节使用本版固定读宽");
            That(reader.ReadUInt32() == 5 && reader.ReadUInt32() == 0x55667788 && reader.ReadByte() == 0x44 &&
                reader.ReadUInt32() == 1 && reader.ReadByte() == 0x99 && reader.ReadUInt32() == 4 && reader.ReadInt32() == 0xAA,
                "末三RAW严格为5/1/4，不把Extension110插入流或用5B覆盖1B局部变量");
            That(reader.BaseStream.Position - start == (slot == 9 ? 164 : 130), "连续装扮条目精确闭合，不靠读取器吞掉越界字节");
        }
        That(reader.BaseStream.Position == body.Length, "独立读器消费完整Player穿戴列表");
    }

    static void RunRepository(ItemCatalog catalog, AvatarDetail details)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-clear-avatar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("clear-avatar", () => { });
            var role = repository.Create(owner, new("ClearAvatar", new(1, 0, 1, 1), null)
                { Inventory = new([Avatar(0, new(InventoryListType.Avatar, 0), 203, details),
                    Avatar(0, new(InventoryListType.Equipment, 3), 103, details)]) });
            var from = new InventorySlot(InventoryListType.Avatar, 0);
            var to = new InventorySlot(InventoryListType.Equipment, 3);
            var plan = InventoryMoveService.Plan(role, new(from, 203, 1, to, 103, 0, -1, 0, 0), catalog, 1000);
            That(plan.Accepted, "真实存档中的克隆覆盖请求通过原两端模板校验");
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_clear_avatar AFTER UPDATE ON inventory_avatar_details BEGIN SELECT RAISE(ABORT,'clear avatar rollback'); END";
                command.ExecuteNonQuery();
                Reject<SqliteException>(() => InventoryService.Commit(repository, owner, role, plan), "外观详情晚期写入失败整笔回滚");
                var restored = repository.LoadCharacter(owner, role.Slot);
                That(restored.Revision == role.Revision && restored.Inventory.Items.SequenceEqual(role.Inventory.Items),
                    "失败不移动原UID、不覆盖染色或安装部分关联");
                command.CommandText = "DROP TRIGGER fail_clear_avatar";
                command.ExecuteNonQuery();
            }
            role = InventoryService.Commit(repository, owner, role, plan);
            var loaded = new CharacterRepository(path).LoadCharacter(owner, role.Slot);
            That(loaded.Inventory.Items.SequenceEqual(role.Inventory.Items) && loaded.Inventory.Find(to)!.Avatar!.ClearAvatarId == 103 &&
                loaded.Inventory.Find(to)!.Avatar!.ClearAvatarSourceUid == loaded.Inventory.Find(from)!.Uid,
                "外观编号、原件UID及完整时装详情一起持久化，重开仓储恢复");
        }
        finally { Directory.Delete(root, true); }
    }

    static ItemTemplate Template(int slot, bool clone = false) =>
        new((clone ? 200 : 100) + slot, ItemKind.Avatar, (byte)slot, 1, 0, 1, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "avatar")
        { CloneAvatar = clone, BorrowAvatarAppearance = true };
    static InventoryItem Avatar(long uid, InventorySlot slot, int id, AvatarDetail detail) =>
        new(uid, slot, new(id, ItemKind.Avatar, checked((int)uid))) { Avatar = detail };
    static Character Role(ImmutableArray<InventoryItem> items) =>
        new(1, 0, "ClearAvatar", 0, new(1, 0, 1, 1), null) { Level = 60, EquipmentSlotFlags = 8, Inventory = new(items) };
}
