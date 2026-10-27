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
