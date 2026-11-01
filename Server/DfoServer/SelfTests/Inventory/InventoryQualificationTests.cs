using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class InventoryQualificationTests
{
    // 同一业务断言覆盖实际输入、未变化目标和无关资产；不为不同入口各造一套文件库。
    internal static void CheckInputs(Character role, Func<Character, InventoryResult> operation, long now,
        params InventorySlot[] positions)
    {
        var original = operation(role);
        That(original.Accepted && original.Plan is not null, "期限回归先确认原合法业务仍被接受");
        long? originalDeadline = original.Plan!.CharacterPrecondition?.QualificationExpiresAt;
        int expires = checked((int)now + 10);
        foreach (var position in positions)
        {
            var item = role.Inventory.Find(position)!;
            var limited = role with { Inventory = role.Inventory with
                { Items = role.Inventory.Items.Replace(item, item with { Core = item.Core with { ExpireTime = expires } }) } };
            var result = operation(limited);
            long expected = Math.Min(originalDeadline ?? long.MaxValue, expires);
            That(result.Accepted && result.Plan!.CharacterPrecondition is { } guard &&
                guard.QualificationExpiresAt == expected && guard.Matches(limited, expected - 1) && !guard.Matches(limited, expected) &&
                !guard.Matches(limited with { Revision = limited.Revision + 1 }, expected - 1) &&
                !guard.Matches(limited with { Level = (byte)(limited.Level == 95 ? 94 : 95) }, expected - 1),
                "每个实际输入在到期等号及角色资格变化时拒绝旧计划，取原资格和本实例较早截止");
        }
        short freeSlot = checked((short)Enumerable.Range(0, role.Inventory.CargoCapacity)
            .First(index => role.Inventory.Find(new(InventoryListType.PersonalCargo, (short)index)) is null));
        var unrelated = new InventoryItem(9_000_000, new(InventoryListType.PersonalCargo, freeSlot),
            new ItemCore(3299, ItemKind.Material, 1) { ExpireTime = checked((int)now + 1) });
        var extra = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Add(unrelated) } };
        var unchanged = operation(extra);
        That(unchanged.Accepted && unchanged.Plan!.After.Find(unrelated.Position) == unrelated &&
            unchanged.Plan.CharacterPrecondition is { } qualification && qualification.QualificationExpiresAt == originalDeadline,
            "无关金库有限期物品不成为本操作截止，也不被消费或重写");
    }

    public static void RunCore()
    {
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var weapon = new ItemTemplate(100, ItemKind.Equipment, 12, 1, 10, 1, 0, 0, 1, "[trade]", ushort.MaxValue, 0, "武器");
        items.PrefillTemplates([new(100, weapon), new(200, weapon with { Id = 200, Kind = ItemKind.GuildMedal, EquipmentSlot = 32 }),
            new(3326, weapon with { Id = 3326, Kind = ItemKind.Material, EquipmentSlot = 0, StackLimit = 999 }),
            new(90000, weapon with { Id = 90000, Kind = ItemKind.GuardianGem, EquipmentSlot = 0, StackLimit = 999,
                GuardianGemEffects = ["physical attack"] })]);
        var table = new EquipmentRefinementCatalog("[separate upgrade max]\n2\n[table]\n2 1 10000 1 3 2 0 1\n[/table]\n" +
            "[item weights by grade]\n1 3326 1\n[/item weights by grade]\n[item weights by rarity]\n1\n[/item weights by rarity]");
        var clock = new ConsumableUseSessionTests.FakeClock();
        var refinement = new InventoryRefinementService(items, table, new Random(1), clock);
        Character seed = new(1, 0, "Qualify", 0, new(1, 0, 10, 10), null)
        {
            Level = 95,
            Inventory = new([new(1, new(InventoryListType.Main, 9), new(100, ItemKind.Equipment, 123) { Durability = 10 }),
                new(2, new(InventoryListType.Main, 121), new(3326, ItemKind.Material, 2)),
                new(3, new(InventoryListType.Equipment, 32), new(200, ItemKind.GuildMedal, 1)),
                new(4, new(InventoryListType.GuildMedal, 49), new(90000, ItemKind.GuardianGem, 2))], Gold: 1234)
        };
        var refineRequest = new RefinementRequest(new(InventoryListType.Main, 9), 100, 121, 0);
        var gemRequest = new GuardianGemUseRequest(200, 49, 90000, 0);
        InventoryResult Refine(Character role) => refinement.Refine(role, refineRequest, out _);
        InventoryResult Gem(Character role) => InventoryGuardianGemService.Plan(role, gemRequest, items,
            clock.GetUtcNow().ToUnixTimeSeconds(), clock);
        var weaponSlot = new InventorySlot(InventoryListType.Main, 9);
        var materialSlot = new InventorySlot(InventoryListType.Main, 121);
        var medalSlot = new InventorySlot(InventoryListType.Equipment, 32);
        var gemSlot = new InventorySlot(InventoryListType.GuildMedal, 49);
        CheckInputs(seed, Refine, clock.GetUtcNow().ToUnixTimeSeconds(), weaponSlot, materialSlot);
        CheckInputs(seed, Gem, clock.GetUtcNow().ToUnixTimeSeconds(), medalSlot, gemSlot);
        var source = seed.Inventory.Find(weaponSlot)!;
        var failedRole = seed with { Inventory = seed.Inventory with
            { Items = seed.Inventory.Items.Replace(source, source with { Core = source.Core with { GenuineUpgrade = 1 } }) } };
        var failed = Refine(failedRole);
        That(failed.Accepted && failed.Plan!.After.Find(weaponSlot) == failedRole.Inventory.Find(weaponSlot),
            "零成功权重锻造仍合法扣料，目标不产生字段差量");
        CheckInputs(failedRole, Refine, clock.GetUtcNow().ToUnixTimeSeconds(), weaponSlot, materialSlot);
        var embedded = Gem(seed).Plan!.After;
        var sameGem = seed with { Inventory = seed.Inventory with { Items = seed.Inventory.Items.Replace(
            seed.Inventory.Find(medalSlot)!, embedded.Find(medalSlot)!) } };
        That(Gem(sameGem).Accepted && !Gem(sameGem).Plan!.ChangedSlots.Contains(medalSlot), "同孔覆盖相同守护珠只消费一件");
        CheckInputs(sameGem, Gem, clock.GetUtcNow().ToUnixTimeSeconds(), medalSlot, gemSlot);

        string root = Path.Combine(Path.GetTempPath(), "s5a9-item-qualification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            int index = 0;
            foreach (var (operation, position) in new (Func<Character, InventoryResult>, InventorySlot)[]
                { (Refine, weaponSlot), (Refine, materialSlot), (Gem, medalSlot), (Gem, gemSlot) })
            {
                using var owner = owners.Acquire("qualify-" + index++, () => { });
                int expires = checked((int)clock.GetUtcNow().ToUnixTimeSeconds() + 10);
                var role = repository.Create(owner, new("Qualify" + index, seed.Position, null)
                {
                    Level = 95,
                    Inventory = seed.Inventory with { Items = seed.Inventory.Items.Select(item => item with
                        { Uid = 0, Core = item.Position == position ? item.Core with { ExpireTime = expires } : item.Core }).ToImmutableArray() }
                });
                var plan = operation(role).Plan!;
                That(plan.CharacterPrecondition!.Matches(role), "文件库竞态在规划时资格有效");
                clock.Advance(TimeSpan.FromSeconds(10));
                Reject<InventoryQualificationExpiredException>(() => repository.CommitInventory(owner, role, plan),
                    "文件库拒绝到期前规划而在到期等号提交的锻造或镶嵌");
                That(JsonSerializer.Serialize(repository.LoadCharacter(owner, role.Slot)) == JsonSerializer.Serialize(role),
                    "到期拒绝保留完整源、目标、UID、金币及revision，不留下部分消费");
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine("PASS 物品实际输入期限、无变化目标、无关资产及文件库提交竞态断言");
    }
}
