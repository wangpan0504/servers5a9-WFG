using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Inventory.Services;

// 装扮预设：容量由扩展券 action216 累加；0x0639 在空槽上等价于「用当前穿戴创建」，已有槽只改名。
// 选择预设按服务端保存的实例UID重新穿脱，客户端只提交槽位、不提交部位数据。
internal sealed class AvatarPresetService(ItemCatalog catalog, PremiumContractCatalog? contracts = null,
    TownCatalog? towns = null, TimeProvider? time = null)
{
    public const uint AddCapacityAction = 216;
    const ushort Rejection = 19;

    public TimeProvider Clock { get; } = time ?? TimeProvider.System;

    public ActionConsumableDefinition? Definition(int itemId) => itemId > 0 ? catalog.ActionConsumable(itemId) : null;

    // 与里程券同一口径：副本内不是城镇，城镇里再区分赛丽亚房间。
    public string Place(Character character, bool inTown) => !inTown ? "[dungeon]" :
        towns?.IsRoom(character.Position, QuestEligibility.Completed(character)) == true ? "[seria room]" : "[village]";

    // 1号橱窗（槽0）是基础预设：玩家当前的穿戴默认就是第一套装扮，登录时按真实穿戴落库并记为当前橱窗，
    // 之后的换装/漂染实时回写，玩家不需要先点一次。
    // 名字一律由客户端显示：新建/空槽都用空名，客户端把空名换成自己的默认标签；服务端不自造名字。
    // 已有槽0的存档不凭空改写内容，只把旧构建自动写入的默认名（装扮预设N/新装扮预设N）清空一次。
    public InventoryResult PlanEnsureBase(Character character)
    {
        var state = character.Inventory.AvatarPresets;
        if (state.Find(0) is { Name: var name })
        {
            var cleared = ClearLegacyDefaults(state);
            if (state.ActiveSlot is null)
            {
                // 从未选过橱窗的旧存档：当前穿戴即1号橱窗，按登录时的真实穿戴补存一次并记为当前橱窗。
                var current = new AvatarPreset(0, (cleared ?? state).Find(0)?.Name ?? name, Capture(character.Inventory));
                try { current.Validate(); }
                catch (InvalidDataException) { return InventoryResult.Reject(Rejection); }
                return new(true, 0, new(character.Inventory, character.Inventory with
                    { AvatarPresets = (cleared ?? state).With(current) with { ActiveSlot = 0 } }, "avatar_preset_base_active"));
            }
            if (cleared is null)
                return new(true, 0, new(character.Inventory, character.Inventory, "avatar_preset_base_present"));
            return new(true, 0, new(character.Inventory, character.Inventory with { AvatarPresets = cleared },
                "avatar_preset_base_name"));
        }
        if (state.Capacity == 0) return InventoryResult.Reject(Rejection);
        var preset = new AvatarPreset(0, string.Empty, Capture(character.Inventory));
        try { preset.Validate(); }
        catch (InvalidDataException) { return InventoryResult.Reject(Rejection); }
        return new(true, 0, new(character.Inventory,
            character.Inventory with { AvatarPresets = state.With(preset) with { ActiveSlot = 0 } }, "avatar_preset_base"));
    }

    public InventoryResult PlanName(Character character, byte slot, string name)
    {
        var state = character.Inventory.AvatarPresets;
        if (slot >= state.Capacity) return InventoryResult.Reject(Rejection);
        // 空格创建沿用当前穿戴；已有槽改名保留原部位，避免改名顺手覆盖外观。
        var parts = state.Find(slot)?.Parts ?? Capture(character.Inventory);
        var preset = new AvatarPreset(slot, name, parts);
        try { preset.Validate(); }
        catch (InvalidDataException) { return InventoryResult.Reject(Rejection); }
        var after = character.Inventory with { AvatarPresets = state.With(preset) };
        return new(true, 0, new(character.Inventory, after, "avatar_preset_name", new(character)));
    }

    public InventoryResult PlanSelect(Character character, byte slot)
    {
        var state = character.Inventory.AvatarPresets;
        if (slot >= state.Capacity) return InventoryResult.Reject(Rejection);
        // 从未保存过的新格等价于空预设：客户端首次选择新橱窗的语义就是“解除所有装扮”，
        // 选中后该槽立即成为当前橱窗，后续穿戴变化实时回写。
        var preset = state.Find(slot) ?? new AvatarPreset(slot, string.Empty, []);
        // 先按UID确认每个部位仍是同一件时装；任何一件被出售或替换都整笔拒绝，不穿一半。
        foreach (var part in preset.Parts)
            if (Find(character.Inventory, part.ItemUid) is not { } item || item.Core.ItemId != part.ItemId ||
                item.Core.Kind != ItemKind.Avatar)
                return InventoryResult.Reject(Rejection);
        var working = character;
        long? deadline = null;
        var targets = preset.Parts.ToDictionary(part => part.Slot);
        var worn = working.Inventory.Items.Where(item => item.Position.List == InventoryListType.Equipment &&
            item.Position.Index is >= 0 and <= 11 && item.Avatar is not null).ToArray();
        foreach (var item in worn)
        {
            if (targets.TryGetValue((byte)item.Position.Index, out var part) && part.ItemUid == item.Uid) continue;
            var empty = FindEmptyAvatarSlot(working.Inventory);
            if (empty is null) return InventoryResult.Reject(Rejection);
            var step = Move(working, item.Position, item.Core.ItemId, empty.Value);
            if (!step.Accepted) return step;
            working = working with { Inventory = step.Plan!.After };
            Merge(step.Plan.CharacterPrecondition);
        }
        foreach (var part in preset.Parts)
        {
            var item = Find(working.Inventory, part.ItemUid)!;
            var target = new InventorySlot(InventoryListType.Equipment, part.Slot);
            if (item.Position == target) continue;
            var step = Move(working, item.Position, item.Core.ItemId, target);
            if (!step.Accepted) return step;
            working = working with { Inventory = step.Plan!.After };
            Merge(step.Plan.CharacterPrecondition);
        }
        // 应用预设按快照回写克隆外观：物品可能被旧逻辑清过，或玩家在别的预设下改过外观/染色。
        foreach (var part in preset.Parts)
            working = working with { Inventory = RestoreCloneAppearance(working.Inventory, part) };
        // 选中后按本次实际穿戴重新采样部位（换装可能改写克隆/清空外观字段），并记录当前橱窗。
        var presets = state.With(preset with { Parts = Capture(working.Inventory) }) with { ActiveSlot = slot };
        try { presets.Validate(); }
        catch (InvalidDataException) { return InventoryResult.Reject(Rejection); }
        // 装扮栏必须留一格空位才能逐个卸下；每个位置来自上一步提交后的快照。
        return new(true, 0, new(character.Inventory, working.Inventory with { AvatarPresets = presets }, "avatar_preset_select",
            new InventoryCharacterPrecondition(character, deadline, Clock)));

        void Merge(InventoryCharacterPrecondition? precondition)
        {
            if (precondition?.QualificationExpiresAt is not { } expires) return;
            deadline = deadline is null ? expires : Math.Min(deadline.Value, expires);
        }
    }

    public InventoryResult PlanExchange(Character character, byte from, byte to)
    {
        var state = character.Inventory.AvatarPresets;
        if (from >= state.Capacity || to >= state.Capacity) return InventoryResult.Reject(Rejection);
        if (from == to) return new(true, 0, new(character.Inventory, character.Inventory, "avatar_preset_exchange", new(character)));
        if (state.Find(from) is not { } source) return InventoryResult.Reject(Rejection);
        var after = state.Without(from);
        // 目标槽为空时等价于移动；两个槽都有内容才是互换。槽位是身份，内容不改名。
        if (state.Find(to) is { } destination) after = after.Without(to).With(destination with { Slot = from });
        after = after.With(source with { Slot = to });
        // 当前橱窗跟着内容走：拖动改变顺序后，玩家仍在同一个预设上实时保存。
        byte? active = state.ActiveSlot;
        if (active == from) active = to;
        else if (active == to) active = from;
        after = after with { ActiveSlot = active };
        return new(true, 0, new(character.Inventory, character.Inventory with { AvatarPresets = after },
            "avatar_preset_exchange", new(character)));
    }

    public InventoryResult PlanCapacity(Character character, InventorySlot source, ActionConsumableDefinition? definition, bool inTown)
    {
        var state = character.Inventory.AvatarPresets;
        if (definition is not { Label: "[add avatar preset]", Parameters: [] } ||
            state.Capacity >= AvatarPresetState.MaximumCapacity)
            return InventoryResult.Reject(Rejection);
        var paid = InventoryActionConsumableService.Plan(character, source, definition, Place(character, inTown), Clock);
        if (!paid.Accepted || paid.Plan is not { } plan) return paid;
        var after = plan.After with
        {
            AvatarPresets = plan.After.AvatarPresets with { Capacity = (byte)(plan.After.AvatarPresets.Capacity + 1) }
        };
        return new(true, 0, new(character.Inventory, after, "avatar_preset_capacity", plan.CharacterPrecondition));
    }

    // 已选中橱窗后的穿戴变化实时回写：统一移动入口在规划成功后调用；预设自身的换装走 PlanSelect，不经过这里。
    public static InventoryResult SyncActive(InventoryResult result)
    {
        if (!result.Accepted || result.Plan is not { Changed: true } plan) return result;
        var state = plan.After.AvatarPresets;
        if (state.ActiveSlot is not { } active || state.Find(active) is not { } preset) return result;
        var parts = Capture(plan.After);
        if (preset.Parts.SequenceEqual(parts)) return result;
        var after = plan.After with { AvatarPresets = state.With(preset with { Parts = parts }) };
        return result with { Plan = plan.WithAfter(after) };
    }

    // 只匹配旧构建原样写入的两个默认名；玩家自定义名字不会命中，保持原样。清空后客户端显示自己的默认标签。
    static AvatarPresetState? ClearLegacyDefaults(AvatarPresetState state)
    {
        AvatarPresetState? after = null;
        foreach (var preset in state.Presets)
        {
            if (preset.Name != $"装扮预设{preset.Slot + 1}" && preset.Name != $"新装扮预设{preset.Slot + 1}") continue;
            after = (after ?? state).With(preset with { Name = string.Empty });
        }
        return after;
    }

    static ImmutableArray<AvatarPresetPart> Capture(InventorySnapshot inventory) =>
        inventory.Items.Where(item => item.Position.List == InventoryListType.Equipment &&
            item.Position.Index is >= 0 and <= 11 && item.Avatar is not null)
            .OrderBy(item => item.Position.Index)
            .Select(item => new AvatarPresetPart((byte)item.Position.Index, item.Uid, (uint)item.Core.ItemId,
                (uint)item.Avatar!.ClearAvatarId, item.Avatar.Color1, item.Avatar.Color2,
                [item.Avatar.Socket0, item.Avatar.Socket1, item.Avatar.Socket2, item.Avatar.Socket3, item.Avatar.Socket4],
                item.Avatar.ClearAvatarSourceUid))
            .ToImmutableArray();

    // 只回写快照里有外观编号的部位；快照为0表示当时没有复制外观，不能反过来清掉物品已有的外观。
    // 来源 UID 必须仍指向装扮背包中同模板的原件，否则留0；物品自身已有的有效来源在本轮没有快照来源时保留，
    // 让旧存档（没有 ClearAvatarSourceUid 字段）在物品已保留来源时不会因回写而丢掉来源表。
    static InventorySnapshot RestoreCloneAppearance(InventorySnapshot inventory, AvatarPresetPart part)
    {
        if (part.ClearAvatarId == 0) return inventory;
        var clearAvatarId = (int)part.ClearAvatarId;
        long storedSource = ValidCloneSource(inventory, part.ClearAvatarId, part.ClearAvatarSourceUid)
            ? part.ClearAvatarSourceUid : 0;
        return inventory with
        {
            Items = inventory.Items.Select(item =>
                item.Uid == part.ItemUid && item.Avatar is { } avatar
                    ? item with
                    {
                        Avatar = avatar with
                        {
                            ClearAvatarId = clearAvatarId,
                            ClearAvatarSourceUid = storedSource > 0 ? storedSource :
                                avatar.ClearAvatarId == clearAvatarId &&
                                ValidCloneSource(inventory, part.ClearAvatarId, avatar.ClearAvatarSourceUid)
                                    ? avatar.ClearAvatarSourceUid : 0,
                            Color1 = part.Color1,
                            Color2 = part.Color2
                        }
                    }
                    : item).ToImmutableArray()
        };
    }

    static bool ValidCloneSource(InventorySnapshot inventory, uint clearAvatarId, long uid) => uid > 0 &&
        inventory.Items.Any(item => item.Uid == uid && item.Core.ItemId == clearAvatarId &&
            item.Avatar is not null && item.Position.List == InventoryListType.Avatar);

    // 同一UID必须唯一命中一件时装；重复UID是数据错误，不能挑一件继续穿。
    static InventoryItem? Find(InventorySnapshot inventory, long uid)
    {
        var matches = inventory.Items.Where(item => item.Uid == uid && item.Core.Kind == ItemKind.Avatar).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    static InventorySlot? FindEmptyAvatarSlot(InventorySnapshot inventory)
    {
        var range = ItemSlotBoundService.Range(ItemKind.Avatar, inventory);
        for (short index = range.Start; index <= range.End; index++)
        {
            var position = new InventorySlot(InventoryListType.Avatar, index);
            if (inventory.Find(position) is null) return position;
        }
        return null;
    }

    InventoryResult Move(Character character, InventorySlot source, int itemId, InventorySlot destination) =>
        InventoryMoveService.Plan(character, new InventoryMoveRequest(source, itemId, 1, destination, 0, 0, 0, 0, 0),
            catalog, Clock.GetUtcNow().ToUnixTimeSeconds(), contracts);
}
