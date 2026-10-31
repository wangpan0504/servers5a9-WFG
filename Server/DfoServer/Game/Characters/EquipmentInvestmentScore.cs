using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Characters;

// 本服装备建设分，不是伤害预测或原版战斗力；不读取客户端面板上报。
internal sealed record InvestmentItem(long Uid, short Slot, int ItemId, byte Upgrade, byte AmplifyType,
    ushort AmplifyValue, byte Refinement, int ExpireTime, ushort Durability);
internal sealed record InvestmentSnapshot(ushort CharacterId, long CharacterRevision, long ObservedAt,
    ImmutableArray<InvestmentItem> Items, string? Error = null);
internal sealed record InvestmentSlotScore(short Slot, long? Uid, int? ItemId, int Weight,
    int? MinimumLevel, int? Rarity, byte Upgrade, byte Refinement, byte AmplifyType,
    int Base, int Enhancement, int Amplification, int Score, bool Partial, bool ScoreCapped,
    ImmutableArray<string> Notes);
internal sealed record InvestmentScoreResult(string Profile, ushort CharacterId, long CharacterRevision, long ObservedAt,
    string Status, int? Score, bool Partial, bool ScoreCapped, ImmutableArray<InvestmentSlotScore> Slots,
    string? Error = null);

internal static class EquipmentInvestmentScore
{
    public const string Profile = "equipment-investment-95/v1";
    public static ImmutableArray<short> Slots { get; } = [12, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 25];
    static readonly int[] RarityBonus = [0, 100, 250, 450, 800, 350, 650];
    static readonly int[] RefinementEquivalent = [0, 2, 3, 4, 5, 6, 8, 10, 12];

    // 只在原owner内捕获值；离开锁后不持有Character或可变数组，也不读取PVF。
    public static InvestmentSnapshot Capture(Character character, long utcSeconds)
    {
        InvestmentSnapshot Invalid(string error) => new(character.Id, character.Revision, utcSeconds, [], error);
        if (character.Id == 0 || character.Revision < 0 || utcSeconds < 0 || character.Inventory.Items.IsDefault)
            return Invalid("invalid_snapshot");
        var positions = new HashSet<InventorySlot>();
        var uids = new HashSet<long>();
        var rows = ImmutableArray.CreateBuilder<InvestmentItem>();
        foreach (var item in character.Inventory.Items)
        {
            if (item is null || item.Core is null || item.Uid <= 0 || !uids.Add(item.Uid) || !positions.Add(item.Position))
                return Invalid("invalid_or_duplicate_inventory_entry");
            if (item.Position.List != InventoryListType.Equipment || !Slots.Contains(item.Position.Index))
                continue;
            var core = item.Core;
            if (core.Kind != ItemKind.Equipment || core.ItemId <= 0 || core.ExpireTime < 0)
                return Invalid("invalid_equipped_item");
            rows.Add(new(item.Uid, item.Position.Index, core.ItemId, core.Upgrade, core.AmplifyType,
                core.AmplifyValue, core.GenuineUpgrade, core.ExpireTime, core.Durability));
        }
        return new(character.Id, character.Revision, utcSeconds, rows.ToImmutable());
    }

    // 生产调用传共享ItemCatalog.Get；注入查询仅为确定性纯测试，不建立第二套目录或状态。
    public static InvestmentScoreResult Calculate(InvestmentSnapshot snapshot, Func<int, ItemTemplate> template, bool includeAdditionalSlots = false)
    {
        // v2.4扩展仍复用同一公式；旧v1入口只接受标准十二件。
        ImmutableArray<short> slots = includeAdditionalSlots ? [.. Slots, 13, 24, 30] : Slots;
        InvestmentScoreResult Invalid(string error) => new(Profile, snapshot.CharacterId, snapshot.CharacterRevision,
            snapshot.ObservedAt, "Invalid", null, false, false, [], error);
        if (snapshot.Error is not null) return Invalid(snapshot.Error);
        if (snapshot.CharacterId == 0 || snapshot.CharacterRevision < 0 || snapshot.ObservedAt < 0 || snapshot.Items.IsDefault ||
            snapshot.Items.Length > slots.Length || snapshot.Items.Any(item => item is null || item.Uid <= 0 || item.ItemId <= 0 ||
                !slots.Contains(item.Slot) || item.Upgrade > 31 || item.ExpireTime < 0) ||
            snapshot.Items.Select(item => item.Slot).Distinct().Count() != snapshot.Items.Length ||
            snapshot.Items.Select(item => item.Uid).Distinct().Count() != snapshot.Items.Length)
            return Invalid("invalid_snapshot");
        var rows = ImmutableArray.CreateBuilder<InvestmentSlotScore>();
        foreach (short slot in slots)
        {
            int weight = slot == 12 ? 2 : 1;
            var item = snapshot.Items.SingleOrDefault(item => item.Slot == slot);
            if (item is null)
            {
                rows.Add(new(slot, null, null, weight, null, null, 0, 0, 0, 0, 0, 0, 0, false, false, ["empty"]));
                continue;
            }
            if (item.ExpireTime > 0 && item.ExpireTime <= snapshot.ObservedAt)
            {
                rows.Add(new(slot, item.Uid, item.ItemId, weight, null, null, item.Upgrade, item.Refinement,
                    item.AmplifyType, 0, 0, 0, 0, false, false, ["expired_excluded"]));
                continue;
            }
            var notes = ImmutableArray.CreateBuilder<string>();
            bool partial = false, capped = false;
            ItemTemplate? definition;
            try { definition = template(item.ItemId); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FileNotFoundException or FormatException or OverflowException)
            { definition = null; }
            int baseScore = 0;
            if (definition is null)
            { partial = true; notes.Add("template_unavailable"); }
            else
            {
                if (definition.Id != item.ItemId || definition.Kind != ItemKind.Equipment ||
                    !(definition.EquipmentSlot == slot || includeAdditionalSlots && slot == 24 && definition.EquipmentSlot == 12))
                {
                    rows.Add(new(slot, item.Uid, item.ItemId, weight, definition.MinimumLevel, definition.Rarity,
                        item.Upgrade, item.Refinement, item.AmplifyType, 0, 0, 0, 0, true, false,
                        ["template_slot_or_kind_mismatch"]));
                    continue;
                }
                int rarity = definition.Rarity;
                if (rarity < 0 || rarity >= RarityBonus.Length)
                { partial = true; notes.Add("unknown_rarity"); }
                baseScore = 100 + 10 * Math.Min((int)definition.MinimumLevel, 95) +
                    (rarity >= 0 && rarity < RarityBonus.Length ? RarityBonus[rarity] : 0);
                if (definition.MinimumLevel > 95) { capped = true; notes.Add("level_capped_95"); }
            }
            int upgrade = Math.Min((int)item.Upgrade, 15);
            if (item.Upgrade > 15) { capped = true; notes.Add("upgrade_capped_15"); }
            int equivalent = upgrade;
            if (slot == 12 || includeAdditionalSlots && slot == 24)
            {
                equivalent = Math.Max(upgrade, RefinementEquivalent[Math.Min((int)item.Refinement, 8)]);
                if (item.Refinement > 8) { capped = true; notes.Add("refinement_capped_8"); }
            }
            else if (item.Refinement != 0) { partial = true; notes.Add("nonweapon_refinement_ignored"); }
            int enhancement = 20 * equivalent + 5 * Math.Max(equivalent - 10, 0) * Math.Max(equivalent - 10, 0);
            int amplification = 0;
            if (item.AmplifyType is >= 1 and <= 4 && item.AmplifyValue > 0)
                amplification = 10 * upgrade;
            else if (item.AmplifyType is >= 0x81 and <= 0x84 && item.AmplifyValue > 0)
                notes.Add("unpurified_amplification_excluded");
            else if (item.AmplifyType != 0 || item.AmplifyValue != 0)
            { partial = true; notes.Add("invalid_amplification_excluded"); }
            if (item.Durability == 0) notes.Add("broken_investment_retained");
            rows.Add(new(slot, item.Uid, item.ItemId, weight, definition?.MinimumLevel, definition?.Rarity,
                item.Upgrade, item.Refinement, item.AmplifyType, baseScore, enhancement, amplification,
                includeAdditionalSlots && definition is null ? 0 : weight * (baseScore + enhancement + amplification), partial, capped, notes.ToImmutable()));
        }
        var result = rows.ToImmutable();
        bool incomplete = result.Any(row => row.Partial);
        return new(Profile, snapshot.CharacterId, snapshot.CharacterRevision, snapshot.ObservedAt,
            incomplete ? "Partial" : "Complete", result.Sum(row => row.Score), incomplete,
            result.Any(row => row.ScoreCapped), result);
    }
}
