using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record SkinStorageState(ImmutableArray<int> Owned, int Selected = 0)
{
    public static SkinStorageState Empty { get; } = new([]);
    public ImmutableArray<SkinOwnership> DamageFonts { get; init; } = [];
    public int SelectedDamageFont { get; init; }
    public ImmutableArray<SkinOwnership> Cutscenes { get; init; } = [];
    public ImmutableArray<int> SelectedCutscenes { get; init; } = [];
    public bool Same(SkinStorageState other) => Selected == other.Selected && Owned.SequenceEqual(other.Owned) &&
        SelectedDamageFont == other.SelectedDamageFont && DamageFonts.SequenceEqual(other.DamageFonts) &&
        Cutscenes.SequenceEqual(other.Cutscenes) && SelectedCutscenes.SequenceEqual(other.SelectedCutscenes);
    public ImmutableArray<int> CutsceneSelection(long now)
    {
        var active = SelectedCutscenes.Where(id => id == 30000 || Cutscenes.Any(row => row.SkinId == id && row.Active(now))).ToImmutableArray();
        return active.IsEmpty ? [30000] : active;
    }
    public int DamageFontSelection(long now) => SelectedDamageFont > 1 &&
        DamageFonts.Any(row => row.SkinId == SelectedDamageFont && row.Active(now)) ? SelectedDamageFont : 1;
    public void Validate()
    {
        if (Owned.IsDefault || Owned.Length > ushort.MaxValue || Owned.Any(id => id <= 0) ||
            Owned.Distinct().Count() != Owned.Length || Selected != 0 && !Owned.Contains(Selected))
            throw new InvalidDataException("角色武器外观所有权或选中项无效");
        if (DamageFonts.IsDefault || DamageFonts.Length >= ushort.MaxValue ||
            DamageFonts.Any(row => row.SkinId <= 1 || row.ExpiresAt < 0 || row.ExpiresAt > GameWorld.Items.ItemExpiration.MaximumUnixTime) ||
            DamageFonts.Select(row => row.SkinId).Distinct().Count() != DamageFonts.Length ||
            SelectedDamageFont < 0 || SelectedDamageFont > 1 && !DamageFonts.Any(row => row.SkinId == SelectedDamageFont))
            throw new InvalidDataException("角色伤害字体所有权、期限或选择无效");
        if (Cutscenes.IsDefault || Cutscenes.Length >= ushort.MaxValue ||
            Cutscenes.Any(row => row.SkinId <= 0 || row.SkinId == 30000 || row.ExpiresAt < 0 ||
                row.ExpiresAt > GameWorld.Items.ItemExpiration.MaximumUnixTime) ||
            Cutscenes.Select(row => row.SkinId).Distinct().Count() != Cutscenes.Length ||
            SelectedCutscenes.IsDefault || SelectedCutscenes.Length > 10 ||
            SelectedCutscenes.Distinct().Count() != SelectedCutscenes.Length ||
            SelectedCutscenes.Any(id => id != 30000 && !Cutscenes.Any(row => row.SkinId == id)))
            throw new InvalidDataException("技能插画所有权、期限或选择无效");
    }
}

internal sealed record SkinOwnership(int SkinId, int ExpiresAt)
{
    public bool Active(long now) => ExpiresAt == 0 || ExpiresAt > now;
}
