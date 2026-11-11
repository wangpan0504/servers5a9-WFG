using System.Collections.Immutable;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 领域与隔离持久化夹具。真实ID用于核对选奖；合成模板不代表真实分类或期限。
internal static partial class BattlePassRewardTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    static readonly int[] RewardIds = [15, 490705120, 490705122, 490705101,
        490705130, 490705163, 490705127, 490705131, 490705132, 490705111, 490705134];

    public static void RunCore()
    {
        int before = Count;
        var fixture = new Fixture();
        var role = fixture.Role();
        var ordinary = fixture.State(4, 0);
        var basic = fixture.Service.Plan(role, ordinary, 0, 0, 4);
        var basicPlan = basic.Plan ?? throw new InvalidOperationException("level4 ordinary reward has no plan");
        That(basic.Accepted && basic.Error == 0 && basicPlan.Reward == new BattlePassRewardKey(0, 4) &&
            basicPlan.Before == ordinary && basicPlan.Inventory.Before == role.Inventory &&
            basicPlan.Inventory.Reason == "battle_pass_reward" && basicPlan.Inventory.CharacterPrecondition is { } precondition &&
            precondition.MatchesIdentity(role) && !precondition.MatchesIdentity(role with { Id = 2 }) &&
            !precondition.MatchesIdentity(role with { Revision = role.Revision + 1 }),
            "ordinary level4 claim keeps character identity/revision, inventory and authoritative season preconditions");
        That(Totals(basicPlan.Inventory.After).SequenceEqual(new[] { (15, 1) }) &&
            basicPlan.After.Claims.SetEquals([new(0, 4)]) &&
            basicPlan.After.TotalExperience == ordinary.TotalExperience && basicPlan.After.PassType == 0 &&
            basicPlan.After.SeasonKey == ordinary.SeasonKey && basicPlan.After.Revision == ordinary.Revision,
            "level4 ordinary gives exactly its whole track and only adds that claim; planning does not commit a version");
        byte[] expected = new byte[20]; expected[0] = 0x08;
        That(basicPlan.After.ToClaimBytes().SequenceEqual(expected) && !ordinary.Claims.Any() && !role.Inventory.Items.Any(),
            "ordinary level4 sets byte0 bit3 only and leaves all input snapshots unchanged");

        Refuse(role, ordinary, 0, 1, 4, "ordinary pass cannot claim premium track");
        Refuse(role, fixture.State(4, 2), 0, 1, 4, "PassType2 is not silently treated as premium");
        var premium = fixture.State(4, 1) with { Claims = basicPlan.After.Claims };
        var gold = fixture.Service.Plan(role, premium, 0, 1, 4).Plan ??
            throw new InvalidOperationException("level4 premium reward has no plan");
        That(Totals(gold.Inventory.After).SequenceEqual(new[] { (490705101, 3), (490705122, 1) }) &&
            gold.After.Claims.SetEquals([new(0, 4), new(1, 4)]), "premium level4 gives both resource rewards and preserves the other track claim");
        expected[10] = 0x08;
        That(gold.After.ToClaimBytes().SequenceEqual(expected), "premium level4 sets byte10 bit3 without losing byte0");
        Refuse(role, gold.After, 0, 1, 4, "duplicate premium claim is refused without a second grant");
        Refuse(role, basicPlan.After, 0, 0, 4, "duplicate ordinary claim is refused without a second grant");

        Refuse(role, ordinary, 0, 0, 0, "level0 is invalid");
        Refuse(role, fixture.State(80, 1), 0, 0, 81, "level81 is invalid even at terminal progress");
        Refuse(role, ordinary, 0, 0, uint.MaxValue, "uint key cannot wrap to a legal level");
        Refuse(role, ordinary, 0, 2, 4, "track2 is invalid");
        Refuse(role, ordinary, 2, 0, 4, "unknown action2 does not return empty success");
        Refuse(role, fixture.State(3, 1), 0, 0, 4, "character level does not override authoritative pass experience");
        Refuse(role, fixture.State(1, 0), 0, 0, 1, "empty level1 ordinary track cannot set a claim");
        Refuse(role, ordinary with { TotalExperience = -1 }, 0, 0, 4, "negative authoritative experience is refused");
        Refuse(role, ordinary with { SeasonKey = "" }, 0, 0, 4, "empty season cannot create cross-season claims");

        var terminal = fixture.State(80, 1);
        var free80 = fixture.Service.Plan(role, terminal, 0, 0, 80).Plan ?? throw new InvalidOperationException("level80 ordinary plan missing");
        var gold80 = fixture.Service.Plan(role, terminal, 0, 1, 80).Plan ?? throw new InvalidOperationException("level80 premium plan missing");
        That(Totals(free80.Inventory.After).SequenceEqual(new[] { (490705127, 1), (490705130, 1), (490705163, 1) }),
            "level80 ordinary keeps all three rewards");
        That(Totals(gold80.Inventory.After).SequenceEqual(new[] { (490705111, 1), (490705131, 1), (490705132, 1), (490705134, 1) }),
            "level80 premium keeps all four rewards");
        expected = new byte[20]; expected[9] = 0x80;
        That(free80.After.ToClaimBytes().SequenceEqual(expected), "level80 ordinary uses byte9 bit7");
        expected = new byte[20]; expected[19] = 0x80;
        That(gold80.After.ToClaimBytes().SequenceEqual(expected), "level80 premium uses byte19 bit7");

        // 仅剩一格消耗品位：首奖入包、次奖转溢出邮件，整轨领取键仍一次记完。
        var blocked = fixture.Role() with { Inventory = FilledConsumables(55) };
        string blockedBefore = System.Text.Json.JsonSerializer.Serialize(blocked);
        var shortPremium = fixture.Service.Plan(blocked, fixture.State(4, 1), 0, 1, 4);
        var shortPlan = shortPremium.Plan ?? throw new InvalidOperationException("one-slot premium overflow plan missing");
        That(shortPremium.Accepted && shortPremium.Error == 0 &&
            Totals(shortPlan.Inventory.After).SequenceEqual(new[] { (999001, 55 * 999), (490705122, 1) }) &&
            Totals(shortPlan.Inventory.OverflowItems).SequenceEqual(new[] { (490705101, 3) }) &&
            shortPlan.After.Claims.SetEquals([new BattlePassRewardKey(1, 4)]) &&
            shortPlan.Inventory.OverflowMailSender == BattlePassRewardService.OverflowMailSender &&
            shortPlan.Inventory.OverflowMailText == BattlePassRewardService.OverflowMailText &&
            System.Text.Json.JsonSerializer.Serialize(blocked) == blockedBefore,
            "one-slot short premium claim mails the remainder and still marks the whole track");
        var packed = fixture.Role() with { Inventory = FilledConsumables(56) };
        var packedPlan = fixture.Service.Plan(packed, fixture.State(4, 1), 0, 1, 4).Plan ??
            throw new InvalidOperationException("full-page premium overflow plan missing");
        That(Totals(packedPlan.Inventory.After).SequenceEqual(new[] { (999001, 56 * 999) }) &&
            Totals(packedPlan.Inventory.OverflowItems).SequenceEqual(new[] { (490705101, 3), (490705122, 1) }) &&
            packedPlan.After.Claims.SetEquals([new BattlePassRewardKey(1, 4)]),
            "full consumable page single claim mails both premium rewards and still consumes the claim");
        var incomplete = new Fixture(omitItem: 490705101);
        var missing = incomplete.Service.Plan(role, incomplete.State(4, 1), 0, 1, 4);
        That(!missing.Accepted && missing.Plan is null && role.Inventory.Items.IsEmpty,
            "missing second template refuses all rewards and claim instead of partially granting first");
        var wallet = new Fixture(walletTrack: true);
        var capped = role with { Inventory = role.Inventory with { ReviveCoins = int.MaxValue } };
        var saturated = wallet.Service.Plan(capped, wallet.State(4, 0), 0, 0, 4);
        That(!saturated.Accepted && saturated.Plan is null && capped.Inventory.ReviveCoins == int.MaxValue,
            "currency reward cannot saturate silently and consume a claim without its whole count");
        RunBatchCore();
        Persistence();
        OverflowPersistence();
        Console.WriteLine($"PASS BattlePassRewardTests core ({Count - before} checks)");

        void Refuse(Character actor, BattlePassSeasonState state, byte action, byte track, uint level, string name)
        {
            var stateBefore = state;
            var inventoryBefore = actor.Inventory;
            var result = fixture.Service.Plan(actor, state, action, track, level);
            That(!result.Accepted && result.Error != 0 && result.Plan is null && actor.Inventory == inventoryBefore && state == stateBefore, name);
        }
    }

    static (int ItemId, int Count)[] Totals(InventorySnapshot inventory) => Totals(inventory.Items);

    static (int ItemId, int Count)[] Totals(IEnumerable<InventoryItem> items) => items
        .GroupBy(item => item.Core.ItemId).Select(group => (group.Key, group.Sum(item => item.Core.Count)))
        .OrderBy(row => row.Key).ToArray();

    static InventorySnapshot FilledConsumables(int slots) => new(Enumerable.Range(65, slots).Select(slot =>
        new InventoryItem(slot + 1, new(InventoryListType.Main, (short)slot), new(999001, ItemKind.Consumable, 999)))
        .ToImmutableArray());

    sealed class Fixture
    {
        public BattlePassCatalog Catalog { get; }
        public ItemCatalog Items { get; } = new(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        public BattlePassRewardService Service { get; }
        public GoldLimitCatalog GoldLimits { get; } = new("[gold limit from level]\n0 1000000\n[/gold limit from level]");
        public EpicPieceCatalog Pieces { get; } = new("[piece list]\n50000 50001\n[/piece list]");
        public Fixture(int? omitItem = null, bool walletTrack = false)
        {
            Catalog = CatalogFixture(walletTrack);
            Items.PrefillTemplates(RewardIds.Append(999001).Where(id => id != omitItem).Select(id =>
                new KeyValuePair<int, ItemTemplate>(id, new(id, ItemKind.Consumable, 0, 1, 0, 999, 1, 0, 0,
                    "[free]", ushort.MaxValue, 0, "battle-pass domain fixture"))));
            Service = new(Catalog, Items, GoldLimits, Pieces, new FixedClock());
        }
        public Character Role() => new(1, 0, "PassDomain", 7, new(1, 0, 10, 10), null) { Level = 95 };
        public BattlePassSeasonState State(int level, byte type) => new("fixture-season-a",
            checked((long)Catalog.TotalExperienceToReach(level)), type, ImmutableHashSet<BattlePassRewardKey>.Empty, 11);
    }

    sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    static BattlePassCatalog CatalogFixture(bool walletTrack)
    {
        var source = new List<object>
        {
            "[ui path]", "UI/DomainFixture.xui", "[mission]", 1, 1, 1, 0, "fixture", -1, "[/mission]",
            "[daily mission]", 1, "[/daily mission]", "[weekly mission]", 1, "[/weekly mission]",
            "[season mission]", 1, "[/season mission]"
        };
        for (int level = 1; level <= 80; level++)
        {
            source.AddRange(new object[] { "[level reward]", level });
            source.AddRange(level switch
            {
                4 => new object[] { walletTrack ? 1 : 15, 1, 0, 490705122, 1, 1, 490705101, 3, 1 },
                80 => new object[] { 490705130, 1, 0, 490705163, 1, 0, 490705127, 1, 0,
                    490705131, 1, 1, 490705132, 1, 1, 490705111, 1, 1, 490705134, 1, 1 },
                _ => new object[] { 490705120, 1, 1 }
            });
            source.Add("[/level reward]");
        }
        source.Add("[exp table]");
        for (int level = 1; level <= 80; level++) source.AddRange(new object[] { level, level == 80 ? 0 : 1300 });
        source.AddRange(new object[] { "[/exp table]", "[tier table]", 1, 80, "[/tier table]",
            "[postal]", 1, 2, 3, 4, "[/postal]", "[package index]", 10, "[/package index]",
            "[certificate index]", 11, "[/certificate index]", "[buy restrict code]", 12, "[/buy restrict code]",
            "[golden mission exp ratio]", 3, "[cera consumption exp percentage]", 50 });
        var strings = new List<string>();
        PvfToken Token(object value)
        {
            if (value is int integer) return new(0, integer);
            string text = (string)value;
            strings.Add(text);
            return new(text.StartsWith('[') ? (byte)3 : (byte)6, strings.Count - 1);
        }
        return BattlePassCatalog.Parse(source.Select(Token).ToArray(), index => strings[index]);
    }
}
