using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Game.Adventure;

internal sealed record AdventureMember(ushort Id, ushort Slot, string Name, byte Job, byte Grow, byte Level, uint Proficiency, long Revision)
{
    public byte ProfessionGrow => AdventureCatalog.ProfessionGrow(Job, Grow);
}
internal sealed record AdventureState(long Revision, string Name, int CreatedDate, int Level, ulong Experience, decimal Remainder,
    ulong CapsuleExperience, ulong BraveExperience, ImmutableArray<uint> Points, int Month)
{
    public ImmutableArray<AdventureSelection> Representatives { get; init; } = [];
    public static AdventureState Initial(string account, DateTimeOffset now) => new(0, account.Length <= 16 ? account : account[..16],
        Date(now), 1, 0, 0, 0, 0, [0, 0, 0, 0, 0], Date(now) / 100);
    public static int Date(DateTimeOffset now) => int.Parse(now.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
    public void Validate(AdventureCatalog catalog)
    {
        if (Revision < 0 || string.IsNullOrWhiteSpace(Name) || Name.Length > 16 || CharacterName.Encoding.GetByteCount(Name) > 32 ||
            Level < 1 || Level > catalog.MaximumLevel || Remainder is < 0 or >= 1 || Experience > long.MaxValue ||
            Level < catalog.MaximumLevel && Experience >= catalog.Experience[Level + 1] ||
            CapsuleExperience > catalog.CapsuleCapacity || Points.Length != 5 ||
            Points.Where((count, index) => catalog.Shops.TryGetValue((byte)index, out var shop) && count > shop.Maximum).Any() ||
            Representatives.Any(row => !catalog.Professions.ContainsKey((row.Job, row.Grow))) ||
            Representatives.Select(row => (row.Job, row.Grow)).Distinct().Count() != Representatives.Length)
            throw new InvalidDataException("账号冒险团持久状态非法");
    }
}
internal sealed record AdventureSnapshot(AdventureState State, ImmutableArray<AdventureMember> Members)
{
    public ImmutableArray<AdventureMember> Representatives(AdventureCatalog catalog)
    {
        var result = ImmutableArray.CreateBuilder<AdventureMember>();
        foreach (var group in Members.Where(row => catalog.Eligible(row.Job, row.Grow, row.Level))
            .GroupBy(row => (row.Job, Grow: row.ProfessionGrow)).OrderBy(group => group.Key.Job).ThenBy(group => group.Key.Grow))
        {
            var selected = State.Representatives.FirstOrDefault(row => row.Job == group.Key.Job && row.Grow == group.Key.Grow);
            if (State.Representatives.Any(row => row.Job == group.Key.Job && row.Grow == group.Key.Grow) && selected.CharacterId is null) continue;
            result.Add(group.FirstOrDefault(row => row.Id == selected.CharacterId) ?? group.First());
        }
        return result.ToImmutable();
    }
}
