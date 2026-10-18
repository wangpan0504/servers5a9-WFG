using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Skills;

internal enum SkillSwitchInventoryRequestKind : byte
{
    SetSkill,
    SetItem,
    RemoveItem
}

internal readonly record struct SkillSwitchInventoryRequest(
    SkillSwitchInventoryRequestKind Kind,
    ushort SkillId,
    byte EquipmentPart,
    byte ListType,
    ushort Slot);

internal readonly record struct SkillSwitchItemBinding(byte EquipmentPart, long ItemUid, int ItemId)
{
    public void Validate()
    {
        if (EquipmentPart > SkillSwitchInventoryState.MaximumEquipmentPart || ItemUid <= 0 || ItemId <= 0)
            throw new InvalidDataException("Buff强化登记绑定无效");
    }
}

internal readonly record struct LocatedSkillSwitchItem(
    SkillSwitchItemBinding Binding,
    byte ListType,
    ushort Slot,
    InventoryItem Item);

internal sealed record SkillSwitchInventoryState(ushort SkillId, ImmutableArray<SkillSwitchItemBinding> Items)
{
    public const byte MaximumEquipmentPart = 35;
    public const byte SkillSentinelPart = 36;
    public const byte EmptyListType = 47;
    public const ushort EmptySlot = ushort.MaxValue;

    public static SkillSwitchInventoryState Empty { get; } = new(0, []);

    public void Validate()
    {
        if (SkillId == ushort.MaxValue || Items.Length > MaximumEquipmentPart + 1 ||
            Items.Select(item => item.EquipmentPart).Distinct().Count() != Items.Length ||
            Items.Select(item => item.ItemUid).Distinct().Count() != Items.Length ||
            !Items.OrderBy(item => item.EquipmentPart).SequenceEqual(Items))
            throw new InvalidDataException("Buff强化登记状态无效");
        foreach (SkillSwitchItemBinding item in Items)
            item.Validate();
    }

    public bool Same(SkillSwitchInventoryState other) =>
        SkillId == other.SkillId && Items.SequenceEqual(other.Items);

    public SkillSwitchInventoryState WithSkill(ushort skillId)
    {
        if (skillId == ushort.MaxValue)
            throw new InvalidDataException("Buff强化指定技能无效");
        return skillId == SkillId ? this : this with { SkillId = skillId };
    }

    public SkillSwitchInventoryState WithBinding(SkillSwitchItemBinding binding)
    {
        binding.Validate();
        if (Items.Any(item => item.ItemUid == binding.ItemUid && item.EquipmentPart != binding.EquipmentPart))
            throw new InvalidDataException("同一物品不能登记到多个Buff强化部位");
        var items = Items.Where(item => item.EquipmentPart != binding.EquipmentPart)
            .Append(binding).OrderBy(item => item.EquipmentPart).ToImmutableArray();
        var result = this with { Items = items };
        result.Validate();
        return result;
    }

    public SkillSwitchInventoryState WithoutBinding(byte equipmentPart)
    {
        if (equipmentPart > MaximumEquipmentPart)
            throw new InvalidDataException("Buff强化登记部位无效");
        var items = Items.Where(item => item.EquipmentPart != equipmentPart).ToImmutableArray();
        return items.Length == Items.Length ? this : this with { Items = items };
    }

    public SkillSwitchInventoryState Reconcile(InventorySnapshot inventory, IReadOnlyList<CharacterSkill> skills)
    {
        Validate();
        if (SkillId != 0 && !skills.Any(skill => skill.Id == SkillId && skill.Level != 0))
            return Empty;
        var byUid = inventory.Items.Where(item => item.Uid > 0 && IsCharacterContainer(item.Position.List))
            .GroupBy(item => item.Uid).ToDictionary(group => group.Key, group => group.ToArray());
        var retained = Items.Where(binding => byUid.TryGetValue(binding.ItemUid, out InventoryItem[]? matches) &&
            matches.Length == 1 && matches[0].Core.ItemId == binding.ItemId)
            .ToImmutableArray();
        return retained.Length == Items.Length ? this : this with { Items = retained };
    }

    public ImmutableArray<LocatedSkillSwitchItem> Locate(InventorySnapshot inventory)
    {
        Validate();
        var byUid = inventory.Items.Where(item => item.Uid > 0 && IsCharacterContainer(item.Position.List))
            .GroupBy(item => item.Uid).ToDictionary(group => group.Key, group => group.ToArray());
        var located = ImmutableArray.CreateBuilder<LocatedSkillSwitchItem>();
        foreach (var binding in Items)
        {
            if (!byUid.TryGetValue(binding.ItemUid, out InventoryItem[]? matches) || matches.Length != 1 ||
                matches[0].Core.ItemId != binding.ItemId || matches[0].Position.Index < 0)
                continue;
            var item = matches[0];
            located.Add(new(binding, (byte)item.Position.List, checked((ushort)item.Position.Index), item));
        }
        return located.ToImmutable();
    }

    public static bool IsCharacterContainer(InventoryListType list) => list is
        InventoryListType.Main or InventoryListType.Avatar or InventoryListType.PersonalCargo or
        InventoryListType.Equipment or InventoryListType.Pet or InventoryListType.GuildMedal;
}

internal enum SkillSwitchInventoryFailure : byte
{
    None,
    UnsupportedState,
    SkillNotLearned,
    SkillNotSupported,
    ItemMissing,
    ItemStacked,
    ItemExpired,
    ItemPartMismatch,
    ItemLocationInvalid,
    DuplicateItem
}

internal sealed record SkillSwitchInventoryPlan(
    Character Before,
    SkillSwitchInventoryState After,
    SkillSwitchInventoryRequest? Request,
    string Reason,
    InventoryItem? ExpectedItem = null)
{
    public bool Changed => !Before.SkillSwitchInventory.Same(After);
}

internal sealed record SkillSwitchInventoryReceipt(
    Character Character,
    SkillSwitchInventoryState Previous,
    long Generation,
    long SelectionEpoch,
    DfoServer.Game.Session.SessionPhase Phase);

internal sealed record SkillSwitchInventoryResult(
    SkillSwitchInventoryFailure Failure,
    SkillSwitchInventoryPlan? Plan = null,
    SkillSwitchInventoryReceipt? Receipt = null)
{
    public bool Accepted => Failure == SkillSwitchInventoryFailure.None;
}
