using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Network.Builders.Inventory;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonGrowthRepositoryTests
{
    public static void RunEquipmentGrowthResources(PvfArchive pvf)
    {
        var items = new ItemCatalog(pvf, PvfScript.List(pvf.ReadText("equipment/equipment.lst")),
            PvfScript.List(pvf.ReadText("stackable/stackable.lst")), 0);
        RunEquipmentGrowth(TownCatalog.Load(pvf), pvf, CharacterCatalog.Load(pvf, items), items);
    }

    static void RunEquipmentGrowth(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemCatalog? items = null)
    {
        int checks = Count;
        items ??= new(pvf, PvfScript.List(pvf.ReadText("equipment/equipment.lst")), PvfScript.List(pvf.ReadText("stackable/stackable.lst")), 0);
        string root = Path.Combine(Path.GetTempPath(), "S5A9-equipment-growth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("equipment-growth", () => { });
            var learning = Testing.SelfTestCatalogs.SkillLearning(pvf);
            var growth = Testing.SelfTestCatalogs.Growth(pvf);
            var actor = repository.Create(owner, SkillRepositoryTests.Fixture("GrowWeapon", towns, characters, learning));
            var position = new InventorySlot(InventoryListType.Equipment, 12);
            var core = InventoryRewardGrantService.Create(items.Get(108030498)) with
                { Extension90 = 1792, Extension92 = 7, Attr = 12, EnchantCardId = 100000201, EmblemSocketCount = 1, Emblem1 = 42, TradeRestriction = 1 };
            actor = repository.CommitInventory(owner, actor, new(actor.Inventory, actor.Inventory with
                { Items = actor.Inventory.Items.Where(item => item.Position != position).Append(new InventoryItem(0, position, core)).ToImmutableArray() }, "growth_setup"));
            var original = actor.Inventory.Find(position)!;
            var (run, authority) = CreateRun(actor);
            var pending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 20, 1)).Pending!;
            var frozen = EquipmentGrowthPlan.Create(actor, run, pending, items, DateTimeOffset.UtcNow)!;
            That(frozen.Changes is [{ Gain: 2 }] && frozen.Inventory.After.Find(position)!.Core == core with { ItemId = 108030499, Extension90 = 0 },
                "一阶满经验自动晋阶，钳经验、不收费并保留完整打造");
            var reward = new DungeonExperienceReward("fixture-equipment", GameWorld.Dungeons.DungeonKillExperienceCatalog.ResourcePath,
                pending.Monster.MonsterId, pending.Monster.CandidateLevel, 1);
            Reject<InvalidDataException>(() => repository.CommitDungeonGrowth(owner, actor, run, pending, reward, growth,
                equipmentGrowth: frozen with { EventId = Guid.NewGuid() }), "成长规划不能挪到另一死亡事件");
            var clock = new EquipmentGrowthClock(DateTimeOffset.UtcNow);
            var timedActor = actor with { Inventory = actor.Inventory with { Items = actor.Inventory.Items.Replace(original,
                original with { Core = original.Core with { ExpireTime = checked((int)clock.GetUtcNow().AddSeconds(1).ToUnixTimeSeconds()) } }) } };
            var timed = EquipmentGrowthPlan.Create(timedActor, run, pending, items, clock.GetUtcNow(), clock)!;
            clock.Now = clock.Now.AddSeconds(1);
            Reject<InventoryQualificationExpiredException>(() => repository.CommitDungeonGrowth(owner, timedActor, run, pending,
                reward, growth, equipmentGrowth: timed), "装备在规划后到期不能提交成长");
            That(repository.LoadCharacter(owner, actor.Slot).Inventory.Find(position) == original,
                "过期规划不写装备和死亡回执");
            Sql("CREATE TRIGGER fail_equipment_growth BEFORE INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'equipment growth rollback'); END");
            Reject<SqliteException>(() => repository.CommitDungeonGrowth(owner, actor, run, pending, reward, growth, equipmentGrowth: frozen),
                "装备成长后的真实末段SQL故障");
            That(repository.LoadCharacter(owner, actor.Slot).Inventory.Find(position) == original && run.IsPendingDeath(pending),
                "晚期回滚恢复装备完整核心和UID，死亡仍可重试");
            Sql("DROP TRIGGER fail_equipment_growth");
            var committed = repository.CommitDungeonGrowth(owner, actor, run, pending, reward, growth, equipmentGrowth: frozen);
            var replay = repository.CommitDungeonGrowth(owner, actor, run, pending, reward, growth, equipmentGrowth: frozen);
            That(committed.Character.Inventory.Find(position)!.Uid == original.Uid && committed.Character.Revision == actor.Revision + 1 &&
                replay.Duplicate && replay.EquipmentChanges.SequenceEqual(committed.EquipmentChanges) &&
                replay.Character.Inventory.Find(position) == committed.Character.Inventory.Find(position),
                "击杀与装备成长只推进一次revision，旧事实重放不重复晋阶");
            var reopened = new CharacterRepository(path).LoadCharacter(owner, actor.Slot);
            That(reopened.Inventory.Find(position) == committed.Character.Inventory.Find(position), "仓储重建恢复已晋阶实例");
            byte[] changed = EquipmentGrowthPackets.State([committed.Character.Inventory.Find(position)!], true);
            That(changed.Length == 12 && changed[0] == 1 && changed[1] == 1 && changed[2] == 3 &&
                BitConverter.ToInt32(changed, 5) == 108030499 && changed[11] == 7, "481模板更换保留实例状态的独立字节合同");
            actor = repository.CommitInventory(owner, reopened, new(reopened.Inventory, reopened.Inventory with
                { Items = reopened.Inventory.Items.Replace(reopened.Inventory.Find(position)!, original with
                    { Core = core with { ItemId = 108030500, Extension90 = 4828 } }) }, "growth_stage3_setup"));
            var third = EquipmentGrowthPlan.Create(actor, run, pending, items, DateTimeOffset.UtcNow)!;
            var capped = third.Inventory.After.Find(position)!;
            That(capped.Core.ItemId == 108030500 && capped.Core.Extension90 == 4830 &&
                EquipmentGrowthPlan.Create(actor with { Inventory = third.Inventory.After }, run, pending, items, DateTimeOffset.UtcNow) is null,
                "第三阶经验封顶后等待卷轴，不能自动跳过消耗");
            That(EquipmentGrowthPackets.Experience(capped, 2).SequenceEqual(new byte[] { 3,12,0,2,0,222,18,7 }),
                "480经验增量和总量独立字节合同");
            var inBag = actor with { Inventory = actor.Inventory with { Items = actor.Inventory.Items.Select(item => item.Position == position
                ? item with { Position = new(InventoryListType.Main, 9) } : item).ToImmutableArray() } };
            That(EquipmentGrowthPlan.Create(inBag, run, pending, items, DateTimeOffset.UtcNow) is null &&
                EquipmentGrowthPlan.Create(actor with { Level = 95 }, run, pending, items, DateTimeOffset.UtcNow) is null,
                "背包闲置装备与超过十级差敌人不刷成长经验");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} 龙跃成长、自动晋阶与同事务回滚断言");

        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }

    sealed class EquipmentGrowthClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
