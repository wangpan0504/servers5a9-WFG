using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Dungeons;

internal sealed record DazzlementTowerBinding(int Slot, ushort? CharacterId, int LastEntryDay, string Name = "", byte Job = 0);
internal sealed record DazzlementTowerBindingNotice(DazzlementTowerAdmission Period, ImmutableArray<DazzlementTowerBinding> Bindings);
internal sealed record DazzlementTowerNotice(long Generation, long Epoch, Game.Characters.Character Character,
    DazzlementTowerBindingNotice Binding, byte UsedToday);
internal sealed record DazzlementTowerState(int Week, ImmutableArray<DazzlementTowerBinding> Bindings)
{
    // 国服2019-05规格：每账号每服务器每周最多绑定5个角色，每角色每天入场一次。
    public const int CharacterLimit = 5;

    public byte UsedToday(ushort character, DazzlementTowerAdmission period) =>
        Week == period.Week && Bindings.Any(row => row.CharacterId == character && row.LastEntryDay >= period.Day) ? (byte)1 : (byte)0;

    public string? Rejection(ushort character, DazzlementTowerAdmission admission)
    {
        if (Week > admission.Week) return "dazzlement-period-rollback";
        if (Week != admission.Week) return null;
        var bound = Bindings.SingleOrDefault(row => row.CharacterId == character);
        if (bound is not null)
            return bound.LastEntryDay >= admission.Day ? "dazzlement-daily-entry-exhausted" : null;
        return Bindings.Length >= CharacterLimit ? "dazzlement-weekly-bindings-exhausted" : null;
    }
}

internal sealed record DazzlementTowerAdmission(int Week, int Day)
{
    public static DazzlementTowerAdmission At(DateTimeOffset now) =>
        new(WeeklyDungeonPolicy.WeekKey(now), ShopPurchaseCount.Today(now));
}

internal sealed record DazzlementStashCard(byte Slot, int ItemId, int Count, bool Selected = false);

internal sealed record DazzlementTowerProgress(int Week, byte Floor, bool Cleared, byte NormalKeys, byte SpecialKeys,
    ImmutableArray<DazzlementStashCard> StashBox, bool RewardClaimed = false)
{
    public const byte MaxFloors = 7;
    public const int MaxStashCards = 7;
    public const int MaxSelectionLimit = 3;

    public static DazzlementTowerProgress Initial(int week) => new(week, 0, false, 0, 0, []);

    public int AllowedSelectionCount => Math.Clamp((NormalKeys > 0 ? 1 : 0) + Math.Min((int)SpecialKeys, 2), 0, MaxSelectionLimit);

    public DazzlementTowerProgress AdvanceFloor(int itemId = 0, int itemCount = 0, bool dropSpecialKey = false)
    {
        if (Floor >= MaxFloors || Cleared || RewardClaimed)
            throw new InvalidOperationException("眩惑之塔本周7层已通关或已领奖");
        byte nextFloor = checked((byte)(Floor + 1));
        bool cleared = nextFloor >= MaxFloors;
        byte normalKeys = checked((byte)Math.Min(MaxFloors, NormalKeys + 1));
        byte specialKeys = dropSpecialKey ? checked((byte)Math.Min(2, SpecialKeys + 1)) : SpecialKeys;
        var stash = StashBox;
        if (itemId > 0 && itemCount > 0 && stash.Length < MaxStashCards)
        {
            byte nextSlot = checked((byte)stash.Length);
            stash = stash.Add(new(nextSlot, itemId, itemCount));
        }
        return this with
        {
            Floor = nextFloor,
            Cleared = cleared,
            NormalKeys = normalKeys,
            SpecialKeys = specialKeys,
            StashBox = stash
        };
    }

    public DazzlementTowerProgress SelectRewards(ReadOnlySpan<byte> slots)
    {
        if (RewardClaimed)
            throw new InvalidOperationException("眩惑贮藏箱奖励已领取，不可改选");
        if (slots.Length > AllowedSelectionCount)
            throw new InvalidDataException("选择的眩惑贮藏箱奖励数量超过钥匙上限");
        var chosen = new HashSet<byte>();
        foreach (byte slot in slots)
        {
            if (!chosen.Add(slot) || !StashBox.Any(card => card.Slot == slot))
                throw new InvalidDataException("选择的眩惑贮藏箱槽位重复或不存在");
        }
        var updated = StashBox.Select(card => card with { Selected = chosen.Contains(card.Slot) }).ToImmutableArray();
        return this with { StashBox = updated };
    }

    public bool TryClaimWeeklyRewards(out DazzlementTowerProgress after, out ImmutableArray<DazzlementStashCard> claimed, out string? error)
    {
        after = this;
        claimed = [];
        if (RewardClaimed)
        {
            error = "dazzlement-weekly-reward-already-claimed";
            return false;
        }
        if (!Cleared || Floor < MaxFloors)
        {
            error = "dazzlement-weekly-floors-incomplete";
            return false;
        }
        var selected = StashBox.Where(card => card.Selected).ToImmutableArray();
        if (selected.Length > AllowedSelectionCount)
        {
            error = "dazzlement-weekly-selection-exceeded";
            return false;
        }
        claimed = selected;
        after = this with { RewardClaimed = true };
        error = null;
        return true;
    }
}
