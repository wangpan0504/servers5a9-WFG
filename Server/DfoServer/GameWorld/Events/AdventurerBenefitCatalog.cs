using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Events;

internal readonly record struct EventProfession(int Job, int GrowType)
{
    public bool Matches(byte job, ushort growType) => Job == job && (GrowType == -1 || GrowType == (growType & 15));
}

internal sealed record ProfessionPartyBuff(EventProfession Profession, int Members, int BuffId);

// 只解释本版本资源声明；活动启停、账号资格和在线实例由领域层负责。
internal sealed class AdventurerBenefitCatalog
{
    public const string ProfessionPath = "event/partybuffeventasjob.evt";
    public const string ReturningPath = "event/chn_event/200114_newreturnsystem/chn_returnsystem.evt";
    public const string ReturningEventPath = "event/chn_event/200114_newreturnsystem/chn_event_returnsystem.evt";
    public ImmutableArray<EventProfession> Professions { get; }
    public ImmutableArray<ProfessionPartyBuff> MemberBuffs { get; }
    public ImmutableArray<ProfessionPartyBuff> ProfessionBuffs { get; }
    public int ReturningPeriodSeconds { get; }
    public int ReturningBuff { get; }
    public int ExistingBuff { get; }
    readonly ImmutableDictionary<int, int> experienceRates;
    public ImmutableArray<uint> BuffIds => MemberBuffs.Concat(ProfessionBuffs).Select(row => (uint)row.BuffId)
        .Append((uint)ReturningBuff).Append((uint)ExistingBuff).Distinct().Order().ToImmutableArray();
    public int KillExperiencePercent(int buff) => experienceRates.GetValueOrDefault(buff);

    public AdventurerBenefitCatalog(PvfArchive pvf) : this(
        new(pvf.ReadTokens(ProfessionPath), pvf.ResolveString),
        new(pvf.ReadTokens(ReturningPath), pvf.ResolveString),
        new(pvf.ReadTokens(ReturningEventPath), pvf.ResolveString),
        new(pvf.ReadTokens("etc/variousbufflist.etc"), pvf.ResolveString), pvf.ResolveString) { }

    internal AdventurerBenefitCatalog(PvfTokenDocument profession, PvfTokenDocument returning, PvfTokenDocument promotion,
        PvfTokenDocument buffs, Func<int, string> resolve)
    {
        if (!profession.Diagnostics.IsEmpty || !returning.Diagnostics.IsEmpty || !promotion.Diagnostics.IsEmpty)
            throw new InvalidDataException("冒险家活动资源结构不完整");
        int[] targets = Numbers(profession, "target job and growtype");
        if (targets.Length == 0 || targets.Length % 2 != 0)
            throw new InvalidDataException("职业活动目标必须为职业与转职配对");
        var rows = ImmutableArray.CreateBuilder<EventProfession>();
        for (int at = 0; at < targets.Length; at += 2)
        {
            var target = new EventProfession(targets[at], targets[at + 1]);
            Validate(target);
            if (rows.Contains(target)) throw new InvalidDataException("职业活动目标重复");
            rows.Add(target);
        }
        Professions = rows.ToImmutable();
        MemberBuffs = BuffRows(profession, "party member buff info");
        ProfessionBuffs = BuffRows(profession, "party job buff info");
        ReturningPeriodSeconds = Scalar(returning, "benefit period");
        ReturningBuff = Scalar(promotion, "return buff index");
        ExistingBuff = Scalar(promotion, "existing buff index");
        if (ReturningPeriodSeconds <= 0 || ReturningBuff <= 0 || ExistingBuff <= 0)
            throw new InvalidDataException("回归福利期限或Buff索引无效");
        var rates = ImmutableDictionary.CreateBuilder<int, int>();
        foreach (uint id in BuffIds)
        {
            var definitions = buffs.Sections.Where(section => section.Name == "index" && section.Values is [{ Type: 0 } token] && token.Value == id).ToArray();
            if (definitions is not [var row]) throw new InvalidDataException("活动Buff不存在或重复: " + id);
            int rate = 0;
            foreach (var effect in row.Children.Where(field => field.Name == "effect"))
            {
                var names = effect.Children.Where(field => field.Name == "name").ToArray();
                if (names is not [{ Values: [{ Type: 6 } name] }]) throw new InvalidDataException("活动Buff效果名无效");
                if (resolve(name.Value) != "hunter_exp_rate") continue;
                var parameters = effect.Children.Where(field => field.Name == "param").ToArray();
                if (parameters is not [{ Values: [{ Type: 0, Value: >= 0 } value] }]) throw new InvalidDataException("活动击杀经验比例无效");
                rate = checked(rate + value.Value);
            }
            rates.Add((int)id, rate);
        }
        experienceRates = rates.ToImmutable();
    }

    static ImmutableArray<ProfessionPartyBuff> BuffRows(PvfTokenDocument document, string name)
    {
        int[] data = Numbers(document, name);
        if (data.Length % 4 != 0) throw new InvalidDataException("职业组队Buff必须为四字段记录");
        var rows = ImmutableArray.CreateBuilder<ProfessionPartyBuff>();
        for (int at = 0; at < data.Length; at += 4)
        {
            var row = new ProfessionPartyBuff(new(data[at], data[at + 1]), data[at + 2], data[at + 3]);
            Validate(row.Profession);
            if (row.Members is < 1 or > 4 || row.BuffId <= 0 ||
                rows.Any(previous => previous.Profession == row.Profession && previous.Members == row.Members))
                throw new InvalidDataException("职业组队Buff人数、索引或唯一性无效");
            rows.Add(row);
        }
        return rows.ToImmutable();
    }

    static void Validate(EventProfession profession)
    {
        if (profession.Job is < 0 or > 15 || profession.GrowType is < -1 or > 15)
            throw new InvalidDataException("活动职业或转职范围无效");
    }

    static int Scalar(PvfTokenDocument document, string name) => Numbers(document, name) is [var value] ? value
        : throw new InvalidDataException("活动单值字段宽度无效: " + name);

    static int[] Numbers(PvfTokenDocument document, string name)
    {
        var fields = document.Sections.Where(section => section.Name == name).ToArray();
        if (fields is not [var field] || field.Values.Any(token => token.Type != 0))
            throw new InvalidDataException("活动字段缺失、重复或类型无效: " + name);
        return field.Values.Select(token => token.Value).ToArray();
    }
}
