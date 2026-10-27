using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryEchonEquipmentService(ItemCatalog items, EchonEquipmentCatalog rules, EpicPieceCatalog pieces)
{
    public InventoryResult Upgrade(Character character, short slot, DateTimeOffset? utcNow = null) =>
        Change(character, slot, null, utcNow ?? DateTimeOffset.UtcNow);
    public InventoryResult ChangeOption(Character character, EchonOptionRequest request, DateTimeOffset? utcNow = null) =>
        Change(character, request.Slot, request, utcNow ?? DateTimeOffset.UtcNow);

    InventoryResult Change(Character character, short slot, EchonOptionRequest? request, DateTimeOffset now)
    {
        var source = character.Inventory.Find(new(InventoryListType.Main, slot));
        ushort error = Eligibility(source, now, out var template);
        if (error != 0) return InventoryResult.Reject(error);
        var individual = template!.Echon!.Rules;
        var state = EchonEquipmentState.Read(source!.Core);
        ImmutableArray<ItemMaterialCost> costs;
        if (request is not null)
        {
            byte current = request.Position == 0 ? state.First : state.Second;
            if (request.Position > 1 || individual.ReadOnlyOptions.Contains(current) || individual.ReadOnlyOptions.Contains(request.Option) ||
                !individual.OptionCosts.TryGetValue((request.Option, state.Grade), out costs) ||
                (request.Position == 0 ? state.First : state.Second) == request.Option) return InventoryResult.Reject(1);
            state = request.Position == 0 ? state with { First = request.Option } : state with { Second = request.Option };
        }
        else
        {
            if (individual.IsModern) return InventoryResult.Reject(11);
            if (state.Grade >= 3) return InventoryResult.Reject(7);
            if (!individual.UpgradeCosts.TryGetValue((byte)(state.Grade + 1), out costs))
                return InventoryResult.Reject(11);
            state = state with { Grade = (byte)(state.Grade + 1) };
        }
        var paid = InventoryMaterialConsumptionService.Plan(character.Inventory, costs, pieces, now);
        if (!paid.Accepted) return InventoryResult.Reject(8);
        var after = paid.Plan!.After with { Items = paid.Plan.After.Items.Replace(source,
            source with { Core = state.Apply(source.Core) }) };
        return new(true, 0, new(character.Inventory, after, request is null ? "echon_upgrade" : "echon_option",
            Qualification(character, after)));
    }

    public InventoryResult AssignSet(Character character, EchonSetRequest request, DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;
        if (request.Slots.Length is < 3 or > 6) return InventoryResult.Reject(6);
        if (request.Options.Length is < 1 or > 4) return InventoryResult.Reject(3);
        if (request.Slots.Distinct().Count() != request.Slots.Length) return InventoryResult.Reject(9);
        var selected = new List<InventoryItem>();
        var positions = new HashSet<byte>();
        var armorTypes = new HashSet<int>();
        int armorCount = 0, grades = 0;
        byte index = 0;
        foreach (short slot in request.Slots)
        {
            var source = character.Inventory.Find(new(InventoryListType.Main, slot));
            ushort error = Eligibility(source, now, out var template);
            if (error != 0) return InventoryResult.Reject(error);
            var definition = template!.Echon!;
            if (definition.Rules.IsModern || definition.SetIndex == 0) return InventoryResult.Reject(5);
            if (!positions.Add(template.EquipmentSlot)) return InventoryResult.Reject(9);
            if (index != 0 && index != definition.SetIndex) return InventoryResult.Reject(6);
            index = definition.SetIndex;
            grades += EchonEquipmentState.Read(source!.Core).Grade;
            if (template.EquipmentSlot is >= 14 and <= 18)
            {
                armorCount++;
                armorTypes.Add(definition.ArmorType);
            }
            selected.Add(source);
        }
        if (armorCount == 5 && (armorTypes.Count != 1 || armorTypes.Contains(-1))) return InventoryResult.Reject(10);
        byte grade = (byte)(grades / selected.Count);
        var cost = rules.SetCost(index, grade, selected.Count, request.Options);
        if (cost is null) return InventoryResult.Reject(4);
        // 同一角色同一part-set索引只有一条定义；重赋予时以旧定义的材料成本折抵。
        var previous = Records(character.Inventory).Where(set => set.Index == index).ToArray();
        if (previous.Length > 1) return InventoryResult.Reject(6);
        var costs = cost.Value;
        if (previous is [var old])
        {
            var oldCost = rules.SetCost(old.Index, old.Grade, old.Parts, old.Options);
            if (oldCost is null) return InventoryResult.Reject(6);
            var deduction = oldCost.Value.ToDictionary(row => row.ItemId,
                row => checked((int)(row.Count * rules.Sets[index].Discount)));
            costs = costs.Select(row => row with { Count = Math.Max(0, row.Count - deduction.GetValueOrDefault(row.ItemId)) })
                .Where(row => row.Count > 0).ToImmutableArray();
        }
        var paid = InventoryMaterialConsumptionService.Plan(character.Inventory, costs, pieces, now);
        if (!paid.Accepted) return InventoryResult.Reject(8);
        byte Option(int at) => at < request.Options.Length ? request.Options[at] : (byte)0;
        var set = new EchonSet(index, (byte)selected.Count, Option(0), Option(1), Option(2), Option(3), grade);
        var slots = selected.Select(item => item.Position).ToHashSet();
        var after = paid.Plan!.After with
        {
            Items = paid.Plan.After.Items.Select(item =>
            {
                if (slots.Contains(item.Position)) return item with
                {
                    Core = (EchonEquipmentState.Read(item.Core) with { SetIndex = index }).Apply(item.Core) with { EchonSet = set }
                };
                if (item.Position.List != InventoryListType.AccountCargo && item.Core.EchonSet?.Index == index)
                    return item with { Core = (EchonEquipmentState.Read(item.Core) with { SetIndex = 0 }).Apply(item.Core) with { EchonSet = null } };
                return item;
            }).ToImmutableArray()
        };
        return new(true, 0, new(character.Inventory, after, "echon_set", Qualification(character, after)));
    }

    ushort Eligibility(InventoryItem? item, DateTimeOffset now, out ItemTemplate? template)
    {
        template = null;
        if (item is null || item.Core.Kind != ItemKind.Equipment) return 2;
        if (item.Core.EquipmentLockId != 0 || item.Core.IsRental || item.IsExpired(now.ToUnixTimeSeconds())) return 11;
        template = items.Get(item.Core.ItemId);
        if (template.Echon is not { } definition) return 2;
        var state = EchonEquipmentState.Read(item.Core);
        return (definition.Rules.IsModern ? state.Grade == 0 : state.Grade <= 3) &&
            (state.First == 0 || definition.Rules.Options.Contains(state.First)) &&
            (state.Second == 0 || definition.Rules.Options.Contains(state.Second)) &&
            state.Reserved == 0 && (state.SetIndex == 0 && item.Core.EchonSet is null ||
                item.Core.EchonSet is { } set && set.Index == state.SetIndex && set.Index == template.Echon.SetIndex) ? (ushort)0 : (ushort)11;
    }

    static InventoryCharacterPrecondition Qualification(Character character, InventorySnapshot after)
    {
        var remaining = after.Items.ToDictionary(item => item.Position);
        long? expiry = character.Inventory.Items.Where(item => !remaining.TryGetValue(item.Position, out var current) || current.Core != item.Core)
            .Where(item => item.ExpireTime > 0).Select(item => (long?)item.ExpireTime).Min();
        return new(character, expiry, requirePosition: true);
    }

    public static EchonSet[] Records(InventorySnapshot inventory) => inventory.Items
        .Where(item => item.Position.List != InventoryListType.AccountCargo && item.Core.EchonSet is not null)
        .Select(item => item.Core.EchonSet!).Distinct().OrderBy(set => set.Index).ToArray();

    public static InventoryReward[] DisjointRewards(ItemCore core, ItemTemplate template)
    {
        if (template.Echon is not { } definition) return [];
        var state = EchonEquipmentState.Read(core);
        if (!definition.Rules.GradeWeights.Any(row => row.Grade == state.Grade)) throw new InvalidDataException("无效艾肯分解品级");
        return new[] { state.First, state.Second }.SelectMany(option =>
                definition.Rules.Disjoint.GetValueOrDefault((option, state.Grade), []))
            .GroupBy(row => row.ItemId).Select(group => new InventoryReward(group.Key, checked(group.Sum(row => row.Count)))).ToArray();
    }
}
