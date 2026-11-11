using System.Collections.Immutable;
using DfoServer.GameWorld.Events;

namespace DfoServer.Game.Events;

internal sealed record AdventurerBenefitConfiguration(int? ProfessionJob, ImmutableDictionary<string, long> ReturningUntil)
{
    public bool AllProfessions { get; init; }
    public bool? LuckyEnabled { get; init; }
    public static AdventurerBenefitConfiguration Default { get; } = new(null, ImmutableDictionary<string, long>.Empty) { LuckyEnabled = false };
}
internal sealed record AdventurerBenefitMember(string Account, byte Job, ushort GrowType);
internal sealed record AdventurerBenefitSelection(ImmutableArray<uint> Buffs, int KillExperiencePercent, int LuckyPartyClearExperiencePercent = 0);

// 幸运职业覆盖与账号福利期限独立保存；职业组队Buff仍使用自己的资源目标及人数档位。
internal sealed class AdventurerBenefitService(AdventurerBenefitCatalog catalog, AdventurerBenefitConfiguration initial,
    Action<AdventurerBenefitConfiguration> persist)
{
    readonly object gate = new();
    readonly ImmutableArray<LuckyProfession> allProfessions = catalog.Lucky is { } luckyCatalog
        ? luckyCatalog.Professions.OrderBy(row => row.Key).SelectMany(row => row.Value).ToImmutableArray() : [];
    AdventurerBenefitConfiguration configuration = Validate(initial);
    public AdventurerBenefitConfiguration Configuration => Volatile.Read(ref configuration);
    public AdventurerBenefitCatalog Catalog => catalog;

    internal static AdventurerBenefitConfiguration Validate(AdventurerBenefitConfiguration value)
    {
        if (value.ProfessionJob is < 0 or > 15 || value.ReturningUntil is null ||
            value.ReturningUntil.Any(row => row.Key.Length is < 1 or > 64 ||
                row.Key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) || row.Value is <= 0 or > uint.MaxValue))
            throw new InvalidDataException("冒险家活动设置包含无效职业、账号或期限");
        return value;
    }

    public void SetProfession(byte job)
    {
        if (job > 15 || catalog.Lucky is not { } lucky || !lucky.Professions.ContainsKey(job))
            throw new InvalidDataException("幸运职业资源未装配或目标职业无效");
        lock (gate) Commit(configuration with { ProfessionJob = job, AllProfessions = false, LuckyEnabled = true });
    }
    public void SetAllProfessions(bool enabled = true)
    {
        if (catalog.Lucky is not { Professions.Count: > 0 })
            throw new InvalidDataException("幸运职业资源未装配");
        lock (gate)
        {
            if (configuration.AllProfessions == enabled && configuration.LuckyEnabled == enabled && configuration.ProfessionJob is null) return;
            Commit(configuration with { ProfessionJob = null, AllProfessions = enabled, LuckyEnabled = enabled });
        }
    }
    public ImmutableArray<LuckyProfession> LuckyProfessions(long now, AdventurerBenefitConfiguration? snapshot = null)
    {
        if (catalog.Lucky is not { } lucky) return [];
        var current = snapshot ?? Configuration;
        if (current.LuckyEnabled == false) return [];
        if (current.AllProfessions) return allProfessions;
        return current.ProfessionJob is { } job ? lucky.Professions[(byte)job] : lucky.Schedule
            .Where(row => row.Starts > 0 && row.Starts <= now && now < row.Ends)
            .Select(row => row.Profession).Distinct().OrderBy(row => row.Job).ThenBy(row => row.GrowType).ToImmutableArray();
    }
    public bool IsLucky(byte job, ushort growType, long now) => LuckyProfessions(now).Contains(new(job, (byte)(growType & 127)));
    public long SetReturning(string account, long now)
    {
        lock (gate)
        {
            if (ReturningUntil(account, now) is > 0 and var active) return active;
            long until = checked(now + catalog.ReturningPeriodSeconds);
            Commit(configuration with { ReturningUntil = configuration.ReturningUntil.SetItem(account, until) });
            return until;
        }
    }
    void Commit(AdventurerBenefitConfiguration next)
    {
        Validate(next);
        persist(next);
        Volatile.Write(ref configuration, next);
    }
    public long ReturningUntil(string account, long now) => Configuration.ReturningUntil.TryGetValue(account, out long until) && until > now ? until : 0;

    public AdventurerBenefitSelection Select(AdventurerBenefitMember self, IReadOnlyList<AdventurerBenefitMember> members, long now)
    {
        if (members.Count is < 1 or > 4 || members.Select(member => member.Account).Distinct(StringComparer.Ordinal).Count() != members.Count ||
            !members.Contains(self)) throw new InvalidDataException("活动队伍快照无效");
        var current = Configuration;
        static bool Target(AdventurerBenefitMember member, EventProfession target) => target.Matches(member.Job, member.GrowType);
        var buffs = ImmutableArray.CreateBuilder<uint>();
        foreach (var target in catalog.Professions)
        {
            int count = members.Count(member => Target(member, target));
            var table = Target(self, target) ? catalog.ProfessionBuffs : catalog.MemberBuffs;
            var row = table.SingleOrDefault(row => row.Profession == target && row.Members == count);
            if (row is not null) buffs.Add(checked((uint)row.BuffId));
        }
        // 现役队友必须与有效回归账号同一副本；单人不享受组队活动Buff。
        if (members.Count >= 2 && members.Any(member => current.ReturningUntil.TryGetValue(member.Account, out long until) && until > now))
            buffs.Add(checked((uint)(current.ReturningUntil.TryGetValue(self.Account, out long expiry) && expiry > now
                ? catalog.ReturningBuff : catalog.ExistingBuff)));
        var result = buffs.Distinct().Order().ToImmutableArray();
        var lucky = LuckyProfessions(now, current);
        int partyPercent = members.Any(member => member.Account != self.Account &&
            lucky.Contains(new(member.Job, (byte)(member.GrowType & 127)))) ? catalog.Lucky!.PartyExperiencePercent : 0;
        return new(result, result.Sum(id => catalog.KillExperiencePercent(checked((int)id))), partyPercent);
    }
}
