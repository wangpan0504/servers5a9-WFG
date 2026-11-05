using System.Collections.Immutable;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BattlePassRewardTests
{
    static void RunBatchCore()
    {
        var fixture = new Fixture();
        var role = fixture.Role();
        var ordinary = fixture.State(4, 0);
        var basic = fixture.Service.Plan(role, ordinary, 1, 0, 4).Plan ??
            throw new InvalidOperationException("ordinary batch plan missing");
        That(basic.Rewards.SequenceEqual([new BattlePassRewardKey(0, 4)]) &&
            Totals(basic.Inventory.After).SequenceEqual(new[] { (15, 1) }),
            "batch type0 includes only nonempty unlocked ordinary rewards");
        That(basic.Reward == basic.Rewards[0] && basic.After.Claims.SetEquals(basic.Rewards) &&
            basic.After.TotalExperience == ordinary.TotalExperience && basic.After.PassType == 0 &&
            basic.After.Revision == ordinary.Revision && basic.Inventory.Before == role.Inventory && role.Inventory.Items.IsEmpty,
            "batch preserves original input, metadata and one representative key without committing a revision");
        var premium = fixture.State(4, 1);
        BattlePassRewardKey[] expected = [new(1, 1), new(1, 2), new(1, 3), new(0, 4), new(1, 4)];
        var all = fixture.Service.Plan(role, premium, 1, 1, 4).Plan ??
            throw new InvalidOperationException("premium batch plan missing");
        That(all.Rewards.SequenceEqual(expected) && all.After.Claims.SetEquals(expected),
            "batch stable level/track order includes both eligible tracks and every earlier nonempty key");
        That(Totals(all.Inventory.After).SequenceEqual(new[] { (15, 1), (490705101, 3), (490705120, 3), (490705122, 1) }),
            "batch retains repeated reward IDs across levels and every row of same-level multi-reward track");
        byte[] bits = new byte[20]; bits[0] = 8; bits[10] = 15;
        That(all.After.ToClaimBytes().SequenceEqual(bits), "batch full20B has exact basic4 and premium1..4 bits");
        var partial = premium with { Claims = ImmutableHashSet.Create(new BattlePassRewardKey(1, 2), new(0, 4)) };
        var remainder = fixture.Service.Plan(role, partial, 1, 1, 4).Plan ??
            throw new InvalidOperationException("partial batch plan missing");
        That(remainder.Rewards.SequenceEqual(new[] { new BattlePassRewardKey(1, 1), new(1, 3), new(1, 4) }) &&
            remainder.After.Claims.SetEquals(expected) &&
            Totals(remainder.Inventory.After).SequenceEqual(new[] { (490705101, 3), (490705120, 2), (490705122, 1) }),
            "batch excludes both already-claimed tracks without losing earlier claim bits or repeating rewards");
        var type2 = fixture.Service.Plan(role, fixture.State(4, 2), 1, 2, 4).Plan ??
            throw new InvalidOperationException("type2 ordinary batch plan missing");
        That(type2.Rewards.SequenceEqual([new BattlePassRewardKey(0, 4)]) &&
            Totals(type2.Inventory.After).SequenceEqual(new[] { (15, 1) }),
            "type2 authoritative snapshot remains basic-only and never acquires premium privileges");
        var staleLevel = fixture.Service.Plan(role, premium, 1, 1, 3).Plan ??
            throw new InvalidOperationException("stale lower view plan missing");
        That(staleLevel.Rewards.SequenceEqual(expected) && staleLevel.After.Claims.SetEquals(expected) &&
            Totals(staleLevel.Inventory.After).SequenceEqual(new[] { (15, 1), (490705101, 3), (490705120, 3), (490705122, 1) }),
            "stale lower view level grants the full authoritative set and does not stop at the old level");
        var deluxe = fixture.State(40, 1) with { Claims = ImmutableHashSet.Create(new BattlePassRewardKey(0, 4)) };
        var retro = fixture.Service.Plan(role, deluxe, 1, 0, 4).Plan ??
            throw new InvalidOperationException("deluxe stale claim plan missing");
        BattlePassRewardKey[] deluxeKeys = Enumerable.Range(1, 40).Select(level => new BattlePassRewardKey(1, level)).ToArray();
        That(retro.Rewards.SequenceEqual(deluxeKeys) && retro.After.Claims.SetEquals(deluxeKeys.Append(new BattlePassRewardKey(0, 4))) &&
            retro.After.PassType == 1 && retro.After.TotalExperience == deluxe.TotalExperience && retro.After.Revision == deluxe.Revision &&
            Totals(retro.Inventory.After).SequenceEqual(new[] { (490705101, 3), (490705120, 39), (490705122, 1) }),
            "deluxe purchase keeps the old button type and level, then one click grants every unclaimed premium reward through level 40");
        var fresh = fixture.Service.Plan(role, deluxe, 1, 1, 40).Plan ??
            throw new InvalidOperationException("fresh deluxe claim plan missing");
        That(fresh.Rewards.SequenceEqual(retro.Rewards) && fresh.After.Claims.SetEquals(retro.After.Claims),
            "a refreshed type1 level40 click grants the same deluxe set as the stale button");
        Refuse(role, retro.After, 1, 0, 4, "deluxe replay refuses without duplicate rewards or claims");
        Refuse(role, premium, 1, 2, 4, "client type above authoritative pass cannot raise the premium track");
        Refuse(role, premium, 1, 0, 5, "client higher view level cannot raise eligible level");
        Refuse(role, premium, 1, 1, 5, "matching type with a higher view level cannot raise eligible level");
        Refuse(role, premium, 1, 1, uint.MaxValue, "full claim unsigned key cannot wrap");
        Refuse(role, fixture.State(1, 0), 1, 0, 1, "empty basic reward set refuses without empty success");
        Refuse(role, all.After, 1, 1, 4, "complete replay refuses without duplicate rewards or claims");
        var terminal = fixture.State(80, 1) with { Claims = Enumerable.Range(1, 79)
            .Select(level => new BattlePassRewardKey(1, level)).Append(new(0, 4)).ToImmutableHashSet() };
        var last = fixture.Service.Plan(role, terminal, 1, 1, 80).Plan ??
            throw new InvalidOperationException("terminal batch plan missing");
        That(last.Rewards.SequenceEqual(new[] { new BattlePassRewardKey(0, 80), new(1, 80) }) &&
            Totals(last.Inventory.After).SequenceEqual(new[] { (490705111, 1), (490705127, 1), (490705130, 1),
                (490705131, 1), (490705132, 1), (490705134, 1), (490705163, 1) }),
            "terminal batch grants every three-plus-four row at80 and no claimed earlier level");
        That(last.After.ToClaimBytes()[9] == 128 && last.After.ToClaimBytes()[19] == 255,
            "terminal full bitmap keeps previous premium bits and sets both track80 bits");
        var blocked = role with { Inventory = new(Enumerable.Range(65, 55).Select(slot =>
            new InventoryItem(slot + 1, new(InventoryListType.Main, (short)slot), new(999001, ItemKind.Consumable, 999))).ToImmutableArray()) };
        Refuse(blocked, premium, 1, 1, 4, "late batch capacity shortage rolls back all earlier reward grants");
        var missing = new Fixture(omitItem: 490705101);
        var absent = missing.Service.Plan(role, missing.State(4, 1), 1, 1, 4);
        That(!absent.Accepted && absent.Plan is null && role.Inventory.Items.IsEmpty,
            "missing final row template refuses entire multi-level batch before any claims");
        var wallet = new Fixture(walletTrack: true);
        var capped = role with { Inventory = role.Inventory with { ReviveCoins = int.MaxValue } };
        var saturated = wallet.Service.Plan(capped, wallet.State(4, 1), 1, 1, 4);
        That(!saturated.Accepted && saturated.Plan is null && capped.Inventory.ReviveCoins == int.MaxValue,
            "batch refuses a saturated currency instead of swallowing it among other successful rewards");

        void Refuse(Character actor, BattlePassSeasonState state, byte action, byte track, uint level, string label)
        {
            var inventory = actor.Inventory;
            var claims = state.Claims;
            var result = fixture.Service.Plan(actor, state, action, track, level);
            That(!result.Accepted && result.Error != 0 && result.Plan is null &&
                ReferenceEquals(actor.Inventory, inventory) && ReferenceEquals(state.Claims, claims), label);
        }
    }
}
