using System.Collections.Immutable;

namespace DfoServer.Game.Currency;

// 余额和攻坚获得额度均属于账号钱包；消费余额不能返还当周获得额度。
internal sealed record AccountMaterialBalance(int Balance, int RaidWeek = 0, int RaidEarned = 0,
    ImmutableSortedDictionary<ushort, int>? Characters = null)
{
    public static AccountMaterialBalance Empty { get; } = new(0);
    public ImmutableSortedDictionary<ushort, int> CharacterEarned => Characters ?? ImmutableSortedDictionary<ushort, int>.Empty;
    public int EarnedBy(ushort character, int week) => week == RaidWeek ? CharacterEarned.GetValueOrDefault(character) : 0;
    public int EarnedIn(int week) => week == RaidWeek ? RaidEarned : 0;
    public bool Same(AccountMaterialBalance other) => Balance == other.Balance && RaidWeek == other.RaidWeek &&
        RaidEarned == other.RaidEarned && CharacterEarned.SequenceEqual(other.CharacterEarned);
    public void Validate()
    {
        if (Balance < 0 || RaidWeek < 0 || RaidEarned < 0 ||
            CharacterEarned.Any(pair => pair.Value <= 0) ||
            CharacterEarned.Sum(pair => (long)pair.Value) > RaidEarned ||
            RaidWeek == 0 && (RaidEarned != 0 || !CharacterEarned.IsEmpty))
            throw new InvalidDataException("账号材料余额或攻坚周额度无效");
    }
    public AccountMaterialBalance EarnRaid(ushort character, int week, int count)
    {
        if (week <= 0 || count < 0) throw new InvalidDataException("账号材料攻坚获得参数无效");
        var characters = week == RaidWeek ? CharacterEarned : ImmutableSortedDictionary<ushort, int>.Empty;
        if (count != 0) characters = characters.SetItem(character, checked(characters.GetValueOrDefault(character) + count));
        return this with { RaidWeek = week, RaidEarned = checked(EarnedIn(week) + count), Characters = characters };
    }
}
