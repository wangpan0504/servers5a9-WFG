using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CreatureSkinTests
{
    public static void RunCore()
    {
        CreatureSkinMoveTests.RunCore();
        AuraSkinTests.RunCore();
        That(CreatureSkinRequests.Read([255, 255, 33, 0]) == ((short)-1, (ushort)33) &&
            CreatureSkinRequests.Read([65, 0, 33, 0]) == ((short)65, (ushort)33), "865完整保留Main槽号哨兵与目标栏字段");
        foreach (byte[] body in new byte[][] { [], [255, 255], [255, 255, 33], [255, 255, 33, 0, 0] })
            Reject<InvalidDataException>(() => CreatureSkinRequests.Read(body), "865不足或多余字段均拒绝");
        Bytes(CreatureSkinPackets.Open(33, new(true, 0)), [1, 33, 0], "865成功必须包含目标栏");
        Bytes(CreatureSkinPackets.Open(33, InventoryResult.Reject(1)), [0, 1, 0, 33, 0], "865缺券兼容回复包含商城分支码和目标栏");
        Bytes(CreatureSkinPackets.Open(11, InventoryResult.Reject(0)), [0, 0, 0, 11, 0], "非本项目标拒绝保留原目标且不触发商城分支");
        var character = new Character(1, 0, "Skin", 1, new(1, 0, 10, 10), null);
        CharacterStats stats = new(1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        foreach (byte flags in new byte[] { 0, 1, 16, 31, 32, 63, 255 })
        {
            byte[] player = TownPackets.Player(character with { EquipmentSlotFlags = flags }, stats);
            That(player[21] == ((flags & 32) == 0 ? 0 : 1) && player.AsSpan(5, 16).IndexOfAnyExcept((byte)0) < 0,
                "Player原始17字节头的末字节与持久宠物幻化bit5一致，其他头字段保留");
        }
        var precondition = new InventoryCharacterPrecondition(character);
        That(!precondition.Matches(character with { EquipmentSlotFlags = 32 }),
            "库存资格冻结装备栏开通位，同revision的外部开通变化也拒绝旧计划");
        var real = new InventoryItem(101, new(InventoryListType.Equipment, 26), new(63023, ItemKind.Creature, 101))
            { Creature = new() { Name = "Actual" } };
        var skin = new InventoryItem(102, new(InventoryListType.Equipment, 33), new(63000, ItemKind.Creature, 102))
            { Creature = new() { Name = "Appearance" } };
        var appearance = character with { EquipmentSlotFlags = 32, Inventory = new([real, skin]) };
        That(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(TownPackets.Actor(appearance, utcSeconds: 1000).AsSpan(94)) == 63000,
            "Actor固定头89处投影独立幻化模板，不覆盖尾部真实宠物声明");
        foreach (var hidden in new[] { appearance with { EquipmentSlotFlags = 0 },
            appearance with { Inventory = new([skin]) }, appearance with { Inventory = new([real]) },
            appearance with { Inventory = new([real, skin with { Creature = skin.Creature! with { ExpireTime = 1000 } }]) },
            appearance with { Inventory = new([real with { Creature = real.Creature! with { ExpireTime = 1000 } }, skin]) } })
            That(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(TownPackets.Actor(hidden, utcSeconds: 1000).AsSpan(94)) == 0,
                "无真实宠物、无幻化、未解锁或任一到期时清独立外观，不能生成孤立假宠物");

        const string declaration = "[action type]\n`[open creature skin slot]`\n[action usable place]\n`[village]`\n[use action packet]\n0";
        That(ItemCatalog.ReadCreatureSkinTicket(new(declaration)), "开栏券按动作、使用地点及发包模式解释，不依赖名字或单一物品号");
        foreach (string invalid in new[] { declaration.Replace("skin", "unknown"), declaration.Replace("village", "dungeon"),
            declaration.Replace("\n0", "\n1"), declaration + "\n[action type]\n`[open creature skin slot]`" })
            That(!ItemCatalog.ReadCreatureSkinTicket(new(invalid)), "未知或重复开栏声明不能成为合法消费券");

        var ticket = new InventoryItem(91, new(InventoryListType.Main, 65), new(490703887, ItemKind.Consumable, 2)
            { SortLockFlag = 1, ExpireTime = int.MaxValue });
        var template = new ItemTemplate(490703887, ItemKind.Consumable, 0, 1, 0, 1000, 0, 0, 1, "[account]", ushort.MaxValue, 0, "券")
            { OpensCreatureSkinSlot = true };
        foreach (byte job in Enumerable.Range(0, 16).Select(value => (byte)value))
        {
            var owner = character with { Job = job, EquipmentSlotFlags = 17, Inventory = new([ticket]) };
            var result = InventoryCreatureSkinService.PlanUnlock(owner, 65, 33, 1000, template);
            That(result.Accepted && result.Plan is { Changed: true, EquipmentSlotFlagsAfter: 49 } && result.Plan.After.Find(ticket.Position) == ticket with
                { Core = ticket.Core with { Value = 1 } } && result.Plan.CharacterPrecondition!.Matches(owner, 1000) &&
                owner.Inventory.Find(ticket.Position) == ticket,
                "所有职业从同一完整源实例扣一张，保留原UID/锁/期限，规划不修改原快照");
        }
        var sourceRole = character with { Inventory = new([ticket]) };
        foreach (var invalid in new[] { sourceRole with { EquipmentSlotFlags = 32 },
            sourceRole with { Inventory = InventorySnapshot.Empty },
            sourceRole with { Inventory = new([ticket with { Uid = 0 }]) },
            sourceRole with { Inventory = new([ticket with { Core = ticket.Core with { EquipmentLockId = 1 } }]) },
            sourceRole with { Inventory = new([ticket with { Core = ticket.Core with { ExpireTime = 1000 } }]) } })
            That(!InventoryCreatureSkinService.PlanUnlock(invalid, 65, 33, 1000, template).Accepted,
                "已开栏、空槽、临时实例、安全锁及精确到期拒绝且不扣券");
        foreach (var invalid in new[] { template with { Id = 1 }, template with { OpensCreatureSkinSlot = false },
            template with { UseForbidden = true }, template with { MinimumLevel = 2 }, template with { Jobs = 0 } })
            That(!InventoryCreatureSkinService.PlanUnlock(sourceRole, 65, 33, 1000, invalid).Accepted,
                "模板身份、资源动作、禁用和角色合法资格不能绕过");
        That(InventoryCreatureSkinService.PlanUnlock(character, -1, 33, 1000, null).Error == 1 &&
            InventoryCreatureSkinService.PlanUnlock(character with { EquipmentSlotFlags = 32 }, -1, 33, 1000, null).Error == 0 &&
            !InventoryCreatureSkinService.PlanUnlock(sourceRole, 65, 11, 1000, template).Accepted,
            "仅未开通且缺券走商城兼容分支，已开通及其它目标不误消费或误导购买");
        RunRepository(ticket, template);
    }

    static void RunRepository(InventoryItem ticket, ItemTemplate template)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-creature-skin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("skin", () => { });
            var location = new DfoServer.GameWorld.Towns.Location(1, 0, 10, 10);
            var character = repository.Create(owner, new("First", location, location)
            {
                EquipmentSlotFlags = 17,
                Inventory = new([ticket with { Uid = 0, Core = ticket.Core with { ExpireTime = 0 } }])
            });
            var second = repository.Create(owner, new("Second", location, location));
            character = repository.LoadCharacter(owner, character.Slot);
            second = repository.LoadCharacter(owner, second.Slot);
            var result = InventoryCreatureSkinService.PlanUnlock(character, 65, 33, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), template);
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            string before = JsonSerializer.Serialize(repository.List(owner));
            command.CommandText = "CREATE TRIGGER fail_skin_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='open_creature_skin_slot' BEGIN SELECT RAISE(ABORT,'skin rollback'); END";
            command.ExecuteNonQuery();
            Reject<SqliteException>(() => InventoryService.Commit(repository, owner, character, result),
                "库存核心和开通标志写入后审计失败，不发送成功");
            That(JsonSerializer.Serialize(repository.List(owner)) == before, "后期SQL失败原子回滚券、开栏位、版本及其它角色");
            command.CommandText = "DROP TRIGGER fail_skin_late";
            command.ExecuteNonQuery();
            var committed = InventoryService.Commit(repository, owner, character, result);
            That(committed.EquipmentSlotFlags == 49 && committed.Revision == character.Revision + 1 &&
                committed.Inventory.Find(ticket.Position)!.Core.Count == 1 &&
                committed.Inventory.Find(ticket.Position)!.Uid == character.Inventory.Find(ticket.Position)!.Uid,
                "同一库存事务扣券并OR开通位，保留原装备栏资格和源UID");
            Reject<InvalidOperationException>(() => InventoryService.Commit(repository, owner, character, result),
                "旧revision重复提交不能再次扣券或写入");
            var duplicate = InventoryCreatureSkinService.PlanUnlock(committed, 65, 33, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), template);
            That(!duplicate.Accepted && ReferenceEquals(InventoryService.Commit(repository, owner, committed, duplicate), committed),
                "已开通请求不新增库存事务、不消费余券");
            var reopened = new CharacterRepository(path);
            That(JsonSerializer.Serialize(reopened.LoadCharacter(owner, character.Slot)) == JsonSerializer.Serialize(committed) &&
                JsonSerializer.Serialize(reopened.LoadCharacter(owner, second.Slot)) == JsonSerializer.Serialize(second),
                "重开仓储恢复角色独立开通位，首角色开通不授权同账号另一角色");
            command.CommandText = "SELECT count(*) FROM inventory_audit_log WHERE action_name='open_creature_skin_slot'";
            That((long)command.ExecuteScalar()! == 1, "一次开通只留下实际一次扣券审计");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
