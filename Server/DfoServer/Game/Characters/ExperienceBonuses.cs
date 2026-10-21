using DfoServer.GameWorld.Characters;
using System.Collections.Immutable;

namespace DfoServer.Game.Characters;

internal enum ExperienceEvent { Kill, Clear, Quest }
internal sealed record ExperienceBonusRate(ExperienceBonusSource Source, decimal Percent);
// 只保存某次奖励已经冻结的分项；不持有第二份在线资格，也不负责开通尚未接入的系统。
internal sealed record ExperienceBonuses(uint Ordinary, ImmutableArray<ExperienceBonusPart> Parts)
{
    public uint Extra => checked((uint)Parts.Aggregate(0UL, (total, part) => total + part.Amount));
    public uint Total => checked(Ordinary + Extra);
    public uint Amount(ExperienceBonusSource source) => checked((uint)Parts.Where(part => part.Source == source)
        .Aggregate(0UL, (total, part) => total + part.Amount));

    public ExperienceBonuses Scale(ushort multiplier) => new(checked(Ordinary * multiplier),
        Parts.Select(part => part with { Amount = checked(part.Amount * multiplier) }).ToImmutableArray());

    // 封顶只分配实际到账，先基础后按固定来源顺序分配；所有显示分量之和不超过本次实际经验。
    public ExperienceBonuses Applied(uint gained)
    {
        if (gained > Total) throw new InvalidDataException("实际经验超过冻结奖励");
        uint ordinary = Math.Min(Ordinary, gained), remaining = gained - ordinary;
        var parts = ImmutableArray.CreateBuilder<ExperienceBonusPart>();
        foreach (var part in Parts)
        {
            uint amount = Math.Min(part.Amount, remaining);
            if (amount != 0) parts.Add(part with { Amount = amount });
            remaining -= amount;
        }
        return new(ordinary, parts.ToImmutable());
    }

    public CharacterExperienceDisplay Display(uint gained)
    {
        var actual = Applied(gained);
        uint A(ExperienceBonusSource source) => actual.Amount(source);
        return CharacterExperienceDisplay.Ordinary with
        {
            GrowthContractExperience = A(ExperienceBonusSource.GrowthContract),
            PotionExperience = checked(A(ExperienceBonusSource.Potion) + A(ExperienceBonusSource.QuestPotion)),
            BuffExperience = A(ExperienceBonusSource.Buff),
            PartyExperience = A(ExperienceBonusSource.Party), InternetCafeExperience = A(ExperienceBonusSource.InternetCafe),
            GuildExperience = A(ExperienceBonusSource.Guild), FatigueBurningExperience = A(ExperienceBonusSource.FatigueBurning),
            FatigueBuffExperience = A(ExperienceBonusSource.FatigueBuff), SeriaBlessingExperience = A(ExperienceBonusSource.SeriaBlessing),
            SeriaWelcomeExperience = A(ExperienceBonusSource.SeriaWelcome), FastGrowthExperience = A(ExperienceBonusSource.FastGrowth)
        };
    }

    public static ExperienceBonuses Calculate(uint ordinary, uint bonusBasis, IEnumerable<ExperienceBonusRate> rates)
    {
        var parts = ImmutableArray.CreateBuilder<ExperienceBonusPart>();
        var seen = new HashSet<ExperienceBonusSource>();
        foreach (var rate in rates.OrderBy(rate => rate.Source))
        {
            if (!Enum.IsDefined(rate.Source) || rate.Percent < 0 || !seen.Add(rate.Source))
                throw new InvalidDataException("经验加成来源重复或比例无效");
            decimal amount = decimal.Floor(bonusBasis * rate.Percent / 100);
            if (amount > uint.MaxValue) throw new InvalidDataException("经验加成超出u32范围");
            if (amount != 0) parts.Add(new(rate.Source, (uint)amount));
        }
        var result = new ExperienceBonuses(ordinary, parts.ToImmutable());
        _ = result.Total;
        return result;
    }
}

internal sealed record ExperienceBonusRequest(Character Character, ExperienceEvent Event, uint DungeonId,
    long UnixSeconds, bool PremiumPcRoom, string EquipmentModule = "")
{
    public long UnixMilliseconds { get; init; }
}

internal sealed record ExperienceBonusContext
{
    public static ExperienceBonusContext Empty { get; } = new();
    public ImmutableArray<ExperienceBonusRate> Rates { get; init; } = [];
    public int PartyMembers { get; init; } = 1;
    public bool PartyHasAvatar { get; init; }
}

// 后续组队、公会、冒险团及活动owner在原奖励规划时提供不可变资格快照。
// 禁止在这里消费费用、随机、写库或等待网络；空快照不授予任何资格。
internal interface IExperienceBonusProvider
{
    ExperienceBonusContext Capture(ExperienceBonusRequest request);
}
