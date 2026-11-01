using DfoServer.Game.Characters;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AuraSkinTests
{
    public static void RunCore()
    {
        var ticket = new InventoryItem(91, new(InventoryListType.Main, 121),
            new(490700411, ItemKind.Material, 2) { SortLockFlag = 1 });
        var template = new ItemTemplate(490700411, ItemKind.Material, 0, 1, 0, 1000, 0, 0, 1,
            "[account]", ushort.MaxValue, 0, "光环幻化券", "[material] 0");
        var role = new Character(1, 0, "Aura", 0, new(1, 0, 0, 0), null)
            { Level = 60, EquipmentSlotFlags = 17, Inventory = new([ticket]) };
        const string action = "[action type]\n`[open aura skin slot]`\n[action usable place]\n`[village]`\n" +
            "[use action packet]\n0\n[cool time]\n1000";
        That(ItemCatalog.ReadAuraSkinAction(new(action)), "127券需要资源完整声明光环开栏动作、地点及确认发送模式");
        foreach (string invalid in new[] { action.Replace("aura", "creature"), action.Replace("village", "dungeon"),
            action.Replace("\n0\n", "\n1\n"), action + "\n[action type]\n`[open aura skin slot]`" })
            That(!ItemCatalog.ReadAuraSkinAction(new(invalid)), "未知、缺失或重复127动作声明不能成为可使用券");
        var open = InventoryCreatureSkinService.PlanUnlock(role, 121, 11, 1000, template);
        That(!InventoryCreatureSkinService.PlanAuraAction(role, 121, 1000, template).Accepted &&
            InventoryCreatureSkinService.PlanAuraAction(role, 121, 1000,
                template with { OpensAuraSkinSlot = true }).Accepted,
            "旧材料券仍可走865；只有完整127资源声明才能通过515右键使用");
        That(open.Accepted && open.Plan is { Changed: true, EquipmentSlotFlagsAfter: 25 } &&
            open.Plan.After.Find(ticket.Position)?.Core.Count == 1 &&
            open.Plan.After.Find(ticket.Position)?.Uid == ticket.Uid &&
            open.Plan.CharacterPrecondition!.Matches(role, 1000),
            "光环865从真实材料券扣一张并只OR bit3，保留源UID、排序锁与其它标志");
        That(InventoryCreatureSkinService.PlanUnlock(role, -1, 11, 1000, null).Error == 1 &&
            InventoryCreatureSkinService.PlanUnlock(role with { EquipmentSlotFlags = 25 }, -1, 11, 1000, null).Error == 0,
            "仅未开通的光环缺券走已证商城引导分支");
        foreach (var bad in new[] { template with { Id = 3 }, template with { Kind = ItemKind.Consumable },
            template with { StackableType = "[material] 1" }, template with { UseForbidden = true } })
            That(!InventoryCreatureSkinService.PlanUnlock(role, 121, 11, 1000, bad).Accepted,
                "光环开栏券核对固定资源身份、材料类别及使用资格");
        That(!InventoryCreatureSkinService.PlanUnlock(role with { Inventory = new([ticket with
            { Core = ticket.Core with { EquipmentLockId = 1 } }]) }, 121, 11, 1000, template).Accepted &&
            !InventoryCreatureSkinService.PlanUnlock(role, 121, 33, 1000, template).Accepted,
            "安全锁材料和交叉目标不能开栏");

        var aura = new ItemTemplate(7001, ItemKind.Avatar, 9, 100, 0, 1, 0, 0, 0,
            "[account]", 0, 91, "外观光环");
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate> { [7001] = aura, [7002] = aura with { Id = 7002 },
            [7003] = aura with { Id = 7003, EquipmentSlot = 11 } });
        var detail = new AvatarDetail { ClearAvatarId = 3, Color1 = 7, Color2 = 8 };
        var skin = new InventoryItem(101, new(InventoryListType.Avatar, 0), new(7001, ItemKind.Avatar, 101)
            { EquipmentLockId = 5 }) { Avatar = detail };
        var real = new InventoryItem(102, new(InventoryListType.Equipment, 9), new(7002, ItemKind.Avatar, 102))
            { Avatar = new() };
        var target = new InventorySlot(InventoryListType.Equipment, 11);
        var owner = role with { EquipmentSlotFlags = 8, Inventory = new([skin, real]) };
        var request = new InventoryMoveRequest(skin.Position, skin.Core.ItemId, 1, target, 0, 0, -1, 0, 0);
        That(InventoryMoveService.Plan(owner with { EquipmentSlotFlags = 0 }, request, catalog, 1000).Accepted == false,
            "未开通光环外观栏不能穿入");
        foreach (byte otherJob in Enumerable.Range(0, 16).Select(value => (byte)value))
            That(InventoryMoveService.Plan(owner with { Job = otherJob }, request, catalog, 1000).Accepted,
                "所有职业的外观光环只要求角色开栏和正确模板部位，不继承真实9的职业等级技能门槛");
        var nativeSkin = skin with { Core = skin.Core with { ItemId = 7003 } };
        var nativeRole = owner with { Inventory = new([nativeSkin, real]) };
        That(InventoryMoveService.Plan(nativeRole, request with { SourceItemId = 7003 }, catalog, 1000).Accepted,
            "PVF本身声明11的原生外观光环也能进入已开通的11槽");
        var worn = InventoryMoveService.Plan(owner, request, catalog, 1000);
        That(worn.Accepted && worn.Plan!.After.Find(target)?.Uid == skin.Uid &&
            worn.Plan.After.Find(new(InventoryListType.Equipment, 9))?.Uid == real.Uid,
            "已开通时外观槽与真实光环分开，外观不受真实穿戴职业等级技能门槛影响");
        That(worn.Plan!.CharacterPrecondition!.Matches(owner, 1000),
            "低于外观模板等级且没有霸王契约时，合法11穿戴计划仍可提交");
        var equipped = owner with { Inventory = worn.Plan!.After };
        var removed = InventoryMoveService.Plan(equipped,
            new(target, skin.Core.ItemId, 1, skin.Position, 0, 0, -1, 0, 0), catalog, 1000);
        That(removed.Accepted && removed.Plan!.After.Find(skin.Position)?.Uid == skin.Uid &&
            removed.Plan.After.Find(new(InventoryListType.Equipment, 9))?.Uid == real.Uid,
            "外观卸下保留原UID和真实光环");
        That(removed.Plan!.CharacterPrecondition!.Matches(equipped, 1000),
            "11卸装不冻结不存在的霸王契约期限");
        RunRepository(ticket, template);
        RunMoveRepository(catalog, skin, nativeSkin, ticket, template);
    }

    static void RunMoveRepository(ItemCatalog catalog, InventoryItem skin, InventoryItem nativeSkin,
        InventoryItem ticket, ItemTemplate ticketTemplate)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-aura-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            using var owner = new AccountOwners().Acquire("aura-move", () => { });
            var location = new DfoServer.GameWorld.Towns.Location(1, 0, 10, 10);
            var role = repository.Create(owner, new("AuraMove", location, location)
            { Level = 60, Inventory = new([skin with
                { Uid = 0, Core = skin.Core with { Value = 0 } },
                nativeSkin with { Uid = 0, Core = nativeSkin.Core with { Value = 0 },
                    Position = new(InventoryListType.Avatar, 1) }, ticket with { Uid = 0 }]) });
            role = InventoryService.Commit(repository, owner, role,
                InventoryCreatureSkinService.PlanUnlock(role, ticket.Position.Index, 11, 1000, ticketTemplate));
            InventorySlot first = new(InventoryListType.Avatar, 0), second = new(InventoryListType.Avatar, 1),
                worn = new(InventoryListType.Equipment, 11);
            void Move(InventorySlot from, InventorySlot to)
            {
                var source = role.Inventory.Find(from)!;
                var target = role.Inventory.Find(to);
                var result = InventoryMoveService.Plan(role,
                    new(from, source.Core.ItemId, 1, to, target?.Core.ItemId ?? 0, 0, -1, 0, 0), catalog, 1000);
                That(result.Accepted && result.Plan!.CharacterPrecondition!.Matches(role, 1000),
                    $"低等级无霸王契约的11外观穿戴、交换和卸装均可提交: {from}->{to}, accepted={result.Accepted}, error={result.Error}, flags={role.EquipmentSlotFlags}, allows={InventoryMoveService.Allows(role,to,source.Core,catalog,1000)}");
                role = InventoryService.Commit(repository, owner, role, result);
                That(repository.LoadCharacter(owner, role.Slot).Revision == role.Revision,
                    "11外观移动提交后角色版本可重读");
            }
            Move(first, worn);
            Move(second, worn);
            Move(worn, first);
            That(role.Inventory.Find(first)?.Core.ItemId == nativeSkin.Core.ItemId &&
                role.Inventory.Find(second)?.Core.ItemId == skin.Core.ItemId && role.Inventory.Find(worn) is null,
                "真实仓储穿戴、交换、卸装保留两件外观实例");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestFiles.DeleteDirectory(root, recursive: true);
        }
    }

    static void RunRepository(InventoryItem ticket, ItemTemplate template)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-aura-skin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("aura", () => { });
            var location = new DfoServer.GameWorld.Towns.Location(1, 0, 10, 10);
            var role = repository.Create(owner, new("AuraFirst", location, location)
            {
                EquipmentSlotFlags = 17,
                Inventory = new([ticket with { Uid = 0 }])
            });
            repository.Create(owner, new("AuraSecond", location, location));
            role = repository.LoadCharacter(owner, role.Slot);
            var planned = InventoryCreatureSkinService.PlanUnlock(role, 121, 11,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), template);
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_aura_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='open_aura_skin_slot' BEGIN SELECT RAISE(ABORT,'aura rollback'); END";
            command.ExecuteNonQuery();
            Reject<SqliteException>(() => InventoryService.Commit(repository, owner, role, planned),
                "光环券扣除后审计失败必须整笔回滚");
            That(JsonSerializer.Serialize(repository.LoadCharacter(owner, role.Slot)) == JsonSerializer.Serialize(role),
                "光环事务失败不改变券、资格和revision");
            command.CommandText = "DROP TRIGGER fail_aura_late";
            command.ExecuteNonQuery();
            var committed = InventoryService.Commit(repository, owner, role, planned);
            That(committed.EquipmentSlotFlags == 25 && committed.Revision == role.Revision + 1 &&
                committed.Inventory.Find(ticket.Position)?.Core.Count == 1,
                "同一事务扣券、保留其它位并提升角色版本");
            Reject<InvalidOperationException>(() => InventoryService.Commit(repository, owner, role, planned),
                "旧revision重放不能二次扣券");
            var reopened = new CharacterRepository(path);
            That(reopened.LoadCharacter(owner, role.Slot).EquipmentSlotFlags == 25 &&
                reopened.LoadCharacter(owner, 1).EquipmentSlotFlags == 0,
                "重建仓储后按角色恢复光环开通位");
            command.CommandText = "SELECT count(*) FROM inventory_audit_log WHERE action_name='open_aura_skin_slot'";
            That((long)command.ExecuteScalar()! == 1, "光环开栏只留下单笔真实审计");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestFiles.DeleteDirectory(root, recursive: true);
        }
    }
}
