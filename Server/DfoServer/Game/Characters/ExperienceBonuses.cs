using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using System.Collections.Immutable;

namespace DfoServer.Game.Characters;

internal enum ExperienceEvent { Kill, Clear, Quest }
internal sealed record ExperienceBonusRate(ExperienceBonusSource Source, decimal Percent);
internal sealed record GuildExperienceEntitlement(uint GuildId, long JoinedAtUnix, int Percent, long ExpiresUnix);
// 只保存某次奖励已经冻结的分项；不持有第二份在线资格，也不负责开通尚未接入的系统。
internal sealed record ExperienceBonuses(uint Ordinary, ImmutableArray<ExperienceBonusPart> Parts)
{
    public GuildExperienceEntitlement? GuildEntitlement { get; init; }
    public uint Extra => checked((uint)Parts.Aggregate(0UL, (total, part) => total + part.Amount));
    public uint Total => checked(Ordinary + Extra);
    public uint Amount(ExperienceBonusSource source) => checked((uint)Parts.Where(part => part.Source == source)
        .Aggregate(0UL, (total, part) => total + part.Amount));

    public ExperienceBonuses Scale(ushort multiplier) => new(checked(Ordinary * multiplier),
        Parts.Select(part => part with { Amount = checked(part.Amount * multiplier) }).ToImmutableArray())
        { GuildEntitlement = GuildEntitlement };

    // df_game_r sumGainedEachExp：组队分量为本人份额减去除人数倍率后的基础，不再额外发一遍经验。
    // 只拆本次击杀的普通份额；装备、契约、秘药、公会等来源仍保留自己的分类和计算基数。
    public ExperienceBonuses SplitPartyDisplay(float multiplier)
    {
        if (!float.IsFinite(multiplier) || multiplier < 0)
            throw new InvalidDataException("组队经验显示倍率无效");
        if (multiplier <= 1 || Ordinary == 0) return this;
        if (Parts.Any(part => part.Source == ExperienceBonusSource.Party))
            throw new InvalidDataException("组队经验分量不能重复提供");
        uint basis = (uint)((double)Ordinary / multiplier);
        return this with { Ordinary = basis, Parts = Parts.Add(new(ExperienceBonusSource.Party, Ordinary - basis))
            .OrderBy(part => part.Source).ToImmutableArray() };
    }

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
        // 仅映射匹配ARAD已证的独立分量；Equipment/BlackDiamond/Avatar/Creature等仍在基础余项中。
        // Bonuses的ID由客户端角色Buff目录解释，不是物品ID或dstr号；不能用它伪造装备、活动或师徒标签。
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

    // NOTI37的普通余项保留冒险团与评分，确保绝对经验通知完整分摊；NOTI35按独立字段展示冒险团。
    // 未冻结分项的历史结算返回 null，由调用方按旧口径展示。
    public static ExperienceBonuses? FromClearReward(DungeonClearExperienceReward reward) =>
        reward.BonusParts.IsDefault ? null : new(
            checked(reward.BaseExperience + reward.AdventureExperience + reward.RankExperience), reward.BonusParts);

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
    public GuildExperienceEntitlement? GuildEntitlement { get; init; }
}

// 公会及后续其他系统在原奖励规划时提供不可变资格快照；组队击杀分量由正式实例拆分，不重复提供比例。
// 禁止在这里消费费用、随机、写库或等待网络；空快照不授予任何资格。
internal interface IExperienceBonusProvider
{
    ExperienceBonusContext Capture(ExperienceBonusRequest request);
}
