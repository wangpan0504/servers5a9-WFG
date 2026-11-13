using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record SkinStorageState(ImmutableArray<int> Owned, int Selected = 0)
{
    public static SkinStorageState Empty { get; } = new([]);
    public ImmutableArray<SkinOwnership> DamageFonts { get; init; } = [];
    public int SelectedDamageFont { get; init; }
    public ImmutableArray<SkinOwnership> Cutscenes { get; init; } = [];
    public ImmutableArray<int> SelectedCutscenes { get; init; } = [];
    public ImmutableArray<SkinOwnership> PartyFrames { get; init; } = [];
    public int SelectedPartyFrame { get; init; }
    public ImmutableArray<SkinOwnership> Emoticons { get; init; } = [];
    public ImmutableArray<int> SelectedEmoticons { get; init; } = [];
    public const int RecentLimit = byte.MaxValue;
    public ImmutableArray<SkinAcquisition> Recent { get; init; } = [];
    public bool Same(SkinStorageState other) => ReferenceEquals(this, other) ||
        Selected == other.Selected && (Owned == other.Owned || Owned.SequenceEqual(other.Owned)) &&
        SelectedDamageFont == other.SelectedDamageFont && (DamageFonts == other.DamageFonts || DamageFonts.SequenceEqual(other.DamageFonts)) &&
        (Cutscenes == other.Cutscenes || Cutscenes.SequenceEqual(other.Cutscenes)) &&
        (SelectedCutscenes == other.SelectedCutscenes || SelectedCutscenes.SequenceEqual(other.SelectedCutscenes)) &&
        SelectedPartyFrame == other.SelectedPartyFrame && (PartyFrames == other.PartyFrames || PartyFrames.SequenceEqual(other.PartyFrames)) &&
        (Emoticons == other.Emoticons || Emoticons.SequenceEqual(other.Emoticons)) &&
        (SelectedEmoticons == other.SelectedEmoticons || SelectedEmoticons.SequenceEqual(other.SelectedEmoticons)) &&
        (Recent == other.Recent || Recent.SequenceEqual(other.Recent));

    // 最近记录随归属仓库一起提交；同一皮肤续期时移到前面，不无限累积历史。
    public SkinStorageState Acquired(IEnumerable<SkinStorageKey> keys, long acquiredAt) => this with
    {
        Recent = keys.Select(key => new SkinAcquisition(key, acquiredAt)).Concat(Recent)
            .DistinctBy(row => row.Key).Take(RecentLimit).ToImmutableArray()
    };
    public ImmutableArray<int> CutsceneSelection(long now)
    {
        var active = SelectedCutscenes.Where(id => id == 30000 || Cutscenes.Any(row => row.SkinId == id && row.Active(now))).ToImmutableArray();
        return active.IsEmpty ? [30000] : active;
    }
    public int DamageFontSelection(long now) => SelectedDamageFont > 1 &&
        DamageFonts.Any(row => row.SkinId == SelectedDamageFont && row.Active(now)) ? SelectedDamageFont : 1;
    public int PartyFrameSelection(long now) => SelectedPartyFrame != 0 && SelectedPartyFrame != 20000 &&
        PartyFrames.Any(row => row.SkinId == SelectedPartyFrame && row.Active(now)) ? SelectedPartyFrame : 20000;
    // 即时表情固定4个栏位，客户端按位置应用，回包必须保留空位0。
    public ImmutableArray<int> EmoticonSelection(long now)
    {
        var slots = SelectedEmoticons.IsDefault || SelectedEmoticons.Length > 4 ? [] : SelectedEmoticons;
        var active = slots.Select(id => id != 0 && Emoticons.Any(row => row.SkinId == id && row.Active(now)) ? id : 0)
            .Concat(Enumerable.Repeat(0, 4)).Take(4).ToImmutableArray();
        return active;
    }
    // 非武器类别的选择可以指向账号皮肤仓的条目，account参数提供并集一侧；账号仓自身不保存选择。
    public void Validate(SkinStorageState? account = null)
    {
        if (Recent.IsDefault || Recent.Length > RecentLimit || Recent.Any(row => row is null ||
                row.Key.Type > 4 || row.Key.SkinId <= 0 || row.AcquiredAt <= 0 || row.AcquiredAt > DateTimeOffset.MaxValue.UtcTicks) ||
            Recent.Select(row => row.Key).Distinct().Count() != Recent.Length)
            throw new InvalidDataException("皮肤最近获得记录无效");
        if (Owned.IsDefault || Owned.Length > ushort.MaxValue || Owned.Any(id => id <= 0) ||
            Owned.Distinct().Count() != Owned.Length || Selected != 0 && !Owned.Contains(Selected))
            throw new InvalidDataException("角色武器外观所有权或选中项无效");
        if (DamageFonts.IsDefault || DamageFonts.Length >= ushort.MaxValue ||
            DamageFonts.Any(row => row.SkinId <= 1 || row.ExpiresAt < 0 || row.ExpiresAt > GameWorld.Items.ItemExpiration.MaximumUnixTime) ||
            DamageFonts.Select(row => row.SkinId).Distinct().Count() != DamageFonts.Length ||
            SelectedDamageFont < 0 || SelectedDamageFont > 1 && !DamageFonts.Any(row => row.SkinId == SelectedDamageFont) &&
                account?.DamageFonts.Any(row => row.SkinId == SelectedDamageFont) != true)
            throw new InvalidDataException("角色伤害字体所有权、期限或选择无效");
        if (Cutscenes.IsDefault || Cutscenes.Length >= ushort.MaxValue ||
            Cutscenes.Any(row => row.SkinId <= 0 || row.SkinId == 30000 || row.ExpiresAt < 0 ||
                row.ExpiresAt > GameWorld.Items.ItemExpiration.MaximumUnixTime) ||
            Cutscenes.Select(row => row.SkinId).Distinct().Count() != Cutscenes.Length ||
            SelectedCutscenes.IsDefault || SelectedCutscenes.Length > 10 ||
            SelectedCutscenes.Distinct().Count() != SelectedCutscenes.Length ||
            SelectedCutscenes.Any(id => id != 30000 && !Cutscenes.Any(row => row.SkinId == id) &&
                account?.Cutscenes.Any(row => row.SkinId == id) != true))
            throw new InvalidDataException("技能插画所有权、期限或选择无效");
        if (PartyFrames.IsDefault || PartyFrames.Length >= ushort.MaxValue ||
            PartyFrames.Any(row => row.SkinId <= 0 || row.SkinId == 20000 || row.ExpiresAt < 0 ||
                row.ExpiresAt > GameWorld.Items.ItemExpiration.MaximumUnixTime) ||
            PartyFrames.Select(row => row.SkinId).Distinct().Count() != PartyFrames.Length ||
            SelectedPartyFrame < 0 || SelectedPartyFrame > 0 && SelectedPartyFrame != 20000 &&
                !PartyFrames.Any(row => row.SkinId == SelectedPartyFrame) &&
                account?.PartyFrames.Any(row => row.SkinId == SelectedPartyFrame) != true)
            throw new InvalidDataException("角色组队头像框所有权、期限或选择无效");
        if (Emoticons.IsDefault || Emoticons.Length >= ushort.MaxValue ||
            Emoticons.Any(row => row.SkinId <= 0 || row.ExpiresAt < 0 ||
                row.ExpiresAt > GameWorld.Items.ItemExpiration.MaximumUnixTime) ||
            Emoticons.Select(row => row.SkinId).Distinct().Count() != Emoticons.Length ||
            SelectedEmoticons.IsDefault || SelectedEmoticons.Length > 4 ||
            SelectedEmoticons.Any(id => id < 0) ||
            SelectedEmoticons.Where(id => id != 0).Distinct().Count() != SelectedEmoticons.Count(id => id != 0) ||
            SelectedEmoticons.Any(id => id != 0 && !Emoticons.Any(row => row.SkinId == id) &&
                account?.Emoticons.Any(row => row.SkinId == id) != true))
            throw new InvalidDataException("角色即时表情所有权、期限或选择无效");
    }
}

internal sealed record SkinAcquisition(SkinStorageKey Key, long AcquiredAt);

internal sealed record SkinOwnership(int SkinId, int ExpiresAt)
{
    public bool Active(long now) => ExpiresAt == 0 || ExpiresAt > now;
}
