using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class KnightShieldTests
{
    static KnightShieldCatalog Catalog() => new(KnightShieldCatalog.CurrentPath,
        [new(101, 0, 25, 0), new(102, 0, 35, 0), new(103, 1, 0, 120), new(104, 2, 0, 121)]);
    static ItemTemplate Item(int id) => new(id, ItemKind.Equipment, 24, 1, 30, 1, 0, 2, 1,
        "[not trade]", 1 << 12, 0, "Shield");
    static Character Role() => new(1, 0, "Shield", 0, new(1, 0, 10, 10), null)
        { Job = 12, GrowType = 1, Level = 35 };
    static KnightShieldDeck Deck(params int[] items) => new([.. items]);
    static InventoryMoveRequest Move(byte source, short from, int id, byte target, short to) =>
        new(new((InventoryListType)source, from), id, 0, new((InventoryListType)target, to), 0, 0, -1, 0, 0);

    public static void RunCore()
    {
        int checks = Count;
        Packets();
        Rules();
        Parser();
        Persistence();
        Console.WriteLine($"PASS {Count - checks} 守护者盾牌协议、资格、原子持久化与升级断言");
    }

    static void Packets()
    {
        byte[] body = [4, 3, 2, 1, 0, 0, 0, 0, 5, 0, 0, 0, 0, 0, 0, 0, 7, 0, 0, 0];
        var state = Deck(0x01020304, 0, 5, 0, 7);
        Bytes(KnightShieldPackets.Deck(state), body, "580无前缀，仅按位置写五个i32");
        That(KnightShieldRequests.Change(body).Same(state), "657不带角色、计数或额外前缀");
        foreach (int size in new[] { 0, 19, 21, 22, 24 })
            Reject<InvalidDataException>(() => KnightShieldRequests.Change(new byte[size]), "657严格20字节");
        foreach (var invalid in new[] { Deck(1, 1, 0, 0, 0), Deck(-1, 0, 0, 0, 0), Deck(1, 0, 0, 0) })
            Reject<InvalidDataException>(invalid.Validate, "配置拒绝重复非零ID、负数及非五项");
        Bytes(KnightShieldPackets.Changed(InventoryResult.Reject(23)), [0, 23, 0], "657失败完整公共错误码");
        Reject<InvalidOperationException>(() => KnightShieldPackets.Changed(new(true, 0)), "成功配置必须来自提交收据");
    }

    static void Rules()
    {
        var service = new InventoryKnightShieldService(Item, Catalog());
        var character = Role();
        That(service.Eligible(character, 101) && service.Eligible(character, 102), "公共盾按资源等级取得");
        That(!service.Eligible(character with { Level = 24 }, 101) && !service.Eligible(character with { Level = 34 }, 102),
            "不能把EQU最低等级1误当仓库取得等级");
        That(!service.Eligible(character with { Job = 11 }, 101) && !service.Eligible(character, 999), "职业12以外与目录外编号拒绝");
        That(!service.Eligible(character, 103) && service.Eligible(character with { CompletedQuests = [120] }, 103) &&
            service.Eligible(character with { AccountCompletedQuests = [120] }, 103), "任务盾只承认真正完成历史，支持账号完成");
        That(!service.Eligible(character with { CompletedQuests = [121] }, 104) &&
            service.Eligible(character with { GrowType = 0x12, CompletedQuests = [121] }, 104), "任务盾匹配真实转职低四位");
        var arranged = service.Change(character, Deck(0, 102, 101, 0, 0));
        That(arranged.Accepted && arranged.Plan!.Changed && arranged.Plan.After.Items.IsEmpty &&
            arranged.Plan.After.ShieldDeck.Items.SequenceEqual(new[] { 0, 102, 101, 0, 0 }), "657保留客户端备用位排列，不制造普通背包物品");
        That(!service.Change(character, Deck(101, 0, 0, 0, 0)).Accepted, "657不能绕过19改主盾");
        character = character with { Inventory = arranged.Plan!.After };
        var move = Move(33, 2, 0, 33, 0);
        var equipped = service.Move(character, move);
        That(equipped.Accepted && equipped.Plan!.After.Find(InventoryKnightShieldService.MainSlot) is { Uid: 0, Core.ItemId: 101 } &&
            equipped.Plan.After.ShieldDeck.Items.SequenceEqual(new[] { 101, 102, 0, 0, 0 }), "33内部主位交换生成24完整核心且保留备用位");
        character = character with { Inventory = equipped.Plan!.After };
        That(!service.Move(character, Move(34, 0, 102, 33, 0)).Accepted, "不能让主盾与备用位重复");
        var removed = service.Move(character, Move(33, 0, 0, 34, 5));
        That(removed.Accepted && removed.Plan!.After.Find(InventoryKnightShieldService.MainSlot) is null &&
            removed.Plan.After.ShieldDeck.Items[0] == 0 && removed.Plan.After.ShieldDeck.Items[1] == 102, "拖回图鉴移除主盾实体，保留其他配置");
        var core = character.Inventory.Find(InventoryKnightShieldService.MainSlot)!;
        That(!service.Move(character with { Inventory = character.Inventory with { Items = [core with
            { Core = core.Core with { EquipmentLockId = 1 } }] } }, Move(33, 0, 0, 34, 5)).Accepted, "锁定核心不能被重建删除");
        var legacy = Role() with { Inventory = new([core with { Uid = 778, Core = core.Core with
            { EmblemSocketCount = 2, Emblem1 = 3321001 } }], 0) };
        var adopted = service.Move(legacy, Move(34, 0, 101, 33, 0));
        That(adopted.Accepted && adopted.Plan!.After.ShieldDeck.Items[0] == 101 &&
            adopted.Plan.After.Find(InventoryKnightShieldService.MainSlot) == legacy.Inventory.Items[0] &&
            legacy.Inventory.ShieldDeck.Same(KnightShieldDeck.Empty), "旧盾第一次合法拖动接管配置，保留UID与全部改造，规划不改原快照");
        That(!service.Move(legacy, Move(34, 0, 102, 33, 0)).Accepted &&
            !service.Move(legacy, Move(33, 0, 0, 34, 5)).Accepted, "有孔及徽章的旧盾不能被换模板或移除吞掉");
        That(!service.Move(Role() with { Inventory = new([core with { Core = core.Core with { ItemId = 999 } }], 0) },
            Move(34, 0, 101, 33, 0)).Accepted, "目录外旧支持装备不被接管删除");
        foreach (var bad in new[] { move with { Actor = 1 }, move with { Flag0 = 1 }, move with { Flag1 = 1 },
            move with { Count = 1 }, move with { DestinationCount = 1 }, move with { DestinationItemId = 101 },
            Move(33, -1, 0, 33, 0), Move(33, 5, 0, 33, 0), Move(33, 1, 0, 33, 2), Move(34, 1, 101, 33, 0),
            Move(33, 0, 0, 34, 0), Move(34, 0, 101, 0, 0) })
            That(!service.Move(character, bad).Accepted, "错误索引、跨容器、actor与保留字段安全拒绝");
        var ordinary = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        That(!InventoryDeleteService.Plan(character.Inventory, InventoryKnightShieldService.MainSlot, 101, 1).Accepted &&
            !InventoryDeleteService.PlanBatch(character.Inventory, new(InventoryListType.Equipment, [new(0, 24, 101, 1)], 0)).Accepted,
            "单件与批量删除均不能使已配置主盾丢失实体");
        That(!InventoryMoveService.Plan(character, Move(3, 24, 101, 0, 0), ordinary, 0).Accepted &&
            !InventoryMoveService.Plan(Role(), Move(34, 0, 101, 33, 0), ordinary, 0).Accepted,
            "普通移动不能复制虚拟仓物品或让已配置主盾与24分离");
    }

    static void Parser()
    {
        string[] names = ["[shield]", "[item index]", "[grow type]", "[get condition]", "[get shield level]", "[/shield]", "level",
            "[open quest index]", "[clear quest index]"];
        var level = new PvfToken[] { new(3, 0), new(3, 1), new(0, 101), new(3, 2), new(0, 0),
            new(3, 3), new(6, 6), new(3, 4), new(0, 25), new(3, 5), new(3, 5) };
        var parsed = KnightShieldCatalog.Parse(KnightShieldCatalog.CurrentPath, level, id => names[id]);
        That(parsed.Definitions[101] == new KnightShieldDefinition(101, 0, 25, 0), "按type3标签读取资源且孤立闭标签不串项");
        var legacy = new PvfToken[] { new(3, 0), new(3, 1), new(0, 103), new(3, 2), new(0, 1),
            new(3, 7), new(0, 120), new(3, 8), new(0, 120), new(3, 5) };
        That(KnightShieldCatalog.Parse(KnightShieldCatalog.LegacyPath, legacy, id => names[id]).Definitions[103].ClearQuest == 120,
            "旧表缺get condition仅按原任务资格读取");
        Reject<InvalidDataException>(() => KnightShieldCatalog.Parse(KnightShieldCatalog.CurrentPath, legacy, id => names[id]),
            "新表缺获取条件不能默用旧规则");
        Reject<InvalidDataException>(() => KnightShieldCatalog.Parse(KnightShieldCatalog.CurrentPath, level[..^2], id => names[id]),
            "未闭合shield块拒绝");
    }

    static void Persistence()
    {
        string folder = Path.Combine(Path.GetTempPath(), "s5a9-knight-shield-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("shield", () => { });
            var sample = Role();
            var character = repository.Create(owner, new("Shield", sample.Position, sample.Return)
                { Job = 12, GrowType = 1, Level = 35 });
            That(character.Inventory.ShieldDeck.Same(KnightShieldDeck.Empty), "旧角色和新角色空配置不伪造已拥有盾牌");
            var service = new InventoryKnightShieldService(Item, Catalog());
            var planned = service.Move(character, Move(34, 0, 101, 33, 0));
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "ALTER TABLE characters DROP COLUMN knight_shield_deck; PRAGMA user_version=158";
            command.ExecuteNonQuery();
            var upgraded = new CharacterRepository(path).LoadCharacter(owner, character.Slot);
            That(upgraded.Revision == character.Revision && upgraded.Inventory.ShieldDeck.Same(KnightShieldDeck.Empty) &&
                upgraded.Inventory.Items.IsEmpty && upgraded.Inventory.Gold == character.Inventory.Gold,
                "158升级159只加空配置，不改旧角色revision或资产");
            command.CommandText = "PRAGMA user_version";
            That(command.ExecuteScalar() is 159L, "盾牌迁移只追加159");
            command.CommandText = "CREATE TRIGGER fail_shield AFTER INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='knight_shield_deck' BEGIN SELECT RAISE(ABORT,'shield rollback'); END";
            command.ExecuteNonQuery();
            Reject<SqliteException>(() => InventoryService.Commit(repository, owner, character, planned), "末段SQL失败回滚主盾与五位配置");
            var loaded = repository.LoadCharacter(owner, character.Slot);
            That(loaded.Revision == character.Revision && loaded.Inventory.Items.IsEmpty && loaded.Inventory.ShieldDeck.Same(KnightShieldDeck.Empty),
                "失败不留新UID、库存、配置或revision");
            command.CommandText = "DROP TRIGGER fail_shield";
            command.ExecuteNonQuery();
            var committed = InventoryService.Commit(repository, owner, character, planned);
            That(committed.Inventory.Find(InventoryKnightShieldService.MainSlot) is { Uid: > 0, Core.ItemId: 101 } &&
                committed.Inventory.ShieldDeck.Items[0] == 101, "实体UID由已有提交器分配，与配置同事务写入");
            Reject<InvalidOperationException>(() => InventoryService.Commit(repository, owner, character, planned), "旧revision不能重复发放主盾");
            var arranged = InventoryService.Commit(repository, owner, committed, service.Change(committed, Deck(101, 102, 0, 0, 0)));
            loaded = new CharacterRepository(path).LoadCharacter(owner, character.Slot);
            That(loaded.Revision == arranged.Revision && loaded.Inventory.ShieldDeck.Same(Deck(101, 102, 0, 0, 0)) &&
                loaded.Inventory.Find(InventoryKnightShieldService.MainSlot)?.Uid == committed.Inventory.Find(InventoryKnightShieldService.MainSlot)?.Uid,
                "重建仓储重载五项精确顺序，备用位修改保留主盾完整实体");
            var unrelated = new InventoryPlan(loaded.Inventory, loaded.Inventory with { Gold = 7 }, "shield_unrelated");
            var next = repository.CommitInventory(owner, loaded, unrelated);
            That(next.Inventory.ShieldDeck.Same(loaded.Inventory.ShieldDeck), "无关库存提交保留盾牌配置");
            var oldRole = repository.Create(owner, new("Legacy", sample.Position, sample.Return)
                { Job = 12, GrowType = 1, Level = 35 });
            var oldCore = InventoryRewardGrantService.Create(Item(101)) with { EmblemSocketCount = 2, Emblem1 = 3321001 };
            oldRole = repository.CommitInventory(owner, oldRole, new(oldRole.Inventory,
                oldRole.Inventory with { Items = [new(0, InventoryKnightShieldService.MainSlot, oldCore)] }, "shield_legacy_fixture"));
            var oldItem = oldRole.Inventory.Items.Single();
            var adopted = InventoryService.Commit(repository, owner, oldRole, service.Change(oldRole, Deck(101, 102, 0, 0, 0)));
            var adoptedReload = new CharacterRepository(path).LoadCharacter(owner, oldRole.Slot);
            That(adoptedReload.Inventory.Items.Single() == oldItem && adoptedReload.Inventory.ShieldDeck.Same(Deck(101, 102, 0, 0, 0)) &&
                adoptedReload.Revision == adopted.Revision, "657首次接管空配置旧盾，落库重载UID及有孔完整核心逐值不变");
            That(!service.Move(adoptedReload, Move(33, 0, 0, 34, 5)).Accepted &&
                !service.Move(adoptedReload, Move(33, 1, 0, 33, 0)).Accepted, "落库后的定制盾同样拒绝移除或切换");
            var stale = service.Change(next, Deck(101, 0, 102, 0, 0));
            using var replacement = owners.Acquire("shield", () => { });
            Reject<InvalidOperationException>(() => InventoryService.Commit(repository, owner, next, stale), "新owner接管后旧owner不能改配置");
            That(repository.LoadCharacter(replacement, character.Slot).Inventory.ShieldDeck.Same(Deck(101, 102, 0, 0, 0)), "接管拒绝不影响新会话状态");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    public static void RunResources(PvfArchive archive, ItemCatalog items)
    {
        var current = KnightShieldCatalog.Load(archive, KnightShieldCatalog.CurrentPath, items);
        var legacy = KnightShieldCatalog.Load(archive, KnightShieldCatalog.LegacyPath, items);
        That(current.Definitions.Count == 25 && current.Definitions[113370008].RequiredLevel == 25 &&
            current.Definitions[113370029].RequiredLevel == 35 && current.Definitions[113370040].RequiredLevel == 90,
            "正式新表25项完整，孤立闭标签后等级条目均保留");
        That(current.Definitions[113370003].ClearQuest == 12758 && current.Definitions[113370025].GrowType == 2 &&
            legacy.Definitions.Values.All(row => row.RequiredLevel == 0 && row.ClearQuest > 0), "两表独立解析，不并集或默选规则");
        foreach (int id in current.Definitions.Keys.Order())
        {
            var core = InventoryRewardGrantService.Create(items.Get(id));
            That(core.Durability == 0 && core.SealFlag == 0 && core.ExpireTime == 0,
                "正式盾牌不消耗耐久、非封装且无期限，默认核心比较不误拒普通损耗或解封: " + id);
        }
    }
}
