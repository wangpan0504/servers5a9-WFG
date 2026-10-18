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
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ClearAvatarTests
{
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
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var templates = Enumerable.Range(0, 12).SelectMany(slot => new[] { Template(slot), Template(slot, true) })
            .Append(new ItemTemplate(300, ItemKind.Equipment, 12, 1, 10, 1, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "weapon"));
        catalog.PrefillTemplates(templates.ToDictionary(item => item.Id));
        var profiles = new CharacterCatalog(new Dictionary<byte, CharacterProfile>
        {
            [0] = new(0, null!, []) { DefaultAvatarEquipment = Enumerable.Range(0, 12)
                .ToImmutableDictionary(slot => (byte)slot, slot => Template(slot).Id) }
        });
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
                result.Plan.After.Find(next.Position)!.Avatar is { ClearAvatarId: 0, ClearAvatarSourceUid: 0, Color1: 0 },
                "连续克隆继承原始外观和染色，被退回包裹的克隆清除借用关系");
            role = role with { Inventory = result.Plan.After };
            result = Move(role, original.Position, new(InventoryListType.Avatar, 2));
            That(result.Plan!.After.Find(new(InventoryListType.Avatar, 2)) is { Avatar.ClearAvatarId: 0, Avatar.ClearAvatarSourceUid: 0, Avatar.Color1: 0 } &&
                result.Plan.After.Find(original.Position) is null, "卸装清理所属关联，不留下假穿戴物品");
            role = Role([bag]);
            result = Move(role, bag.Position, original.Position);
            That(result.Plan!.After.Find(original.Position)!.Avatar is { ClearAvatarSourceUid: 0 } empty && empty.ClearAvatarId == original.Core.ItemId,
                "空槽使用职业默认外观，并把解析后的EQU编号写入原时装详情");
        }
        var weapon = new InventoryItem(21, new(InventoryListType.Equipment, 12), new(300, ItemKind.Equipment, 0));
        var clearWeapon = Avatar(22, new(InventoryListType.Avatar, 0), Template(10, true).Id, details);
        var weaponRole = Role([weapon, clearWeapon]);
        var weaponResult = Move(weaponRole, clearWeapon.Position, new(InventoryListType.Equipment, 10));
        That(weaponResult.Plan!.After.Find(new(InventoryListType.Equipment, 10))!.Avatar!.ClearAvatarId == 300 &&
            weaponResult.Plan.After.Find(new(InventoryListType.Equipment, 10))!.Avatar!.ClearAvatarSourceUid == 0 &&
            weaponResult.Plan.After.Find(weapon.Position) == weapon, "空武器装扮克隆当前实际武器，不把实际武器变为装扮或移走");
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
        That(sourcePacket.Length == 56 && sourcePacket[0] == 11 && BitConverter.ToInt32(sourcePacket, 17) == 16,
            "1449来源值按真实UID定位原件格加12，不把同模板副本或模板编号作为来源");
        sourceInventory = sourceInventory with { Items = [duplicate, linked, originalSource with { Position = new(InventoryListType.Avatar, 8) }] };
        That(BitConverter.ToInt32(ClearAvatarPackets.Sources(sourceInventory), 17) == 20,
            "原件移格后按提交后库存解析来源，不沿用穿戴时的旧槽号");
        foreach (var invalid in new[] { originalSource with { Position = new(InventoryListType.PersonalCargo, 0) },
            originalSource with { Uid = 34 }, originalSource with { Core = originalSource.Core with { ItemId = 104 } } })
        {
            That(BitConverter.ToInt32(ClearAvatarPackets.Sources(new([duplicate, linked, invalid])), 17) == 0,
                "原件不在装扮包裹、UID或模板不匹配时清除客户端来源，不回退到同模板副本");
        }
        Reject<InvalidDataException>(() => (details with { ClearAvatarSourceUid = -1 }).Validate(), "克隆来源UID不得为负");
        RunPlayerExtensions(details);
        RunRepository(catalog, profiles, details);
        Console.WriteLine($"PASS {Count - checks} clear avatar覆盖、默认外观、完整资产及事务断言");

        InventoryResult Move(Character role, InventorySlot from, InventorySlot to)
        {
            var result = InventoryMoveService.Plan(role, new(from, role.Inventory.Find(from)!.Core.ItemId, 1,
                to, role.Inventory.Find(to)?.Core.ItemId ?? 0, 0, -1, 0, 0), catalog, 1000, characters: profiles);
            That(result.Accepted, "合法装扮覆盖、卸装和空槽穿戴均可规划");
            return result;
        }
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

    static void RunRepository(ItemCatalog catalog, CharacterCatalog profiles, AvatarDetail details)
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
            var plan = InventoryMoveService.Plan(role, new(from, 203, 1, to, 103, 0, -1, 0, 0), catalog, 1000, characters: profiles);
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
