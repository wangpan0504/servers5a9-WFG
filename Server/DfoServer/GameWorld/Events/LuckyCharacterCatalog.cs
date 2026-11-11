using System.Collections.Immutable;
using System.Globalization;
using DfoServer.GameWorld.Characters;
using PvfLib;

namespace DfoServer.GameWorld.Events;

internal readonly record struct LuckyProfession(byte Job, byte GrowType);
internal sealed record LuckyProfessionSchedule(long Starts, long Ends, LuckyProfession Profession);

// 四叶草使用独立的原生幸运职业目录，不能借用职业组队活动的Buff表。
internal sealed class LuckyCharacterCatalog
{
    public const string Path = "etc/chn_activatecharacter.etc";
    public ImmutableArray<LuckyProfessionSchedule> Schedule { get; }
    public ImmutableDictionary<byte, ImmutableArray<LuckyProfession>> Professions { get; }
    public int KillExperiencePercent { get; }
    public int ClearExperiencePercent { get; }
    public int QuestExperiencePercent { get; }
    public int PartyExperiencePercent { get; }
    public int FatigueAccelerationPercent { get; }

    public LuckyCharacterCatalog(PvfArchive pvf)
    {
        var document = new PvfTokenDocument(pvf.ReadTokens(Path), pvf.ResolveString);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("幸运职业资源结构无效");
        var fields = document.Sections.Where(field => field.Name == "activate character schedule").ToArray();
        if (fields is not [var schedule] || schedule.Values.Length % 4 != 0)
            throw new InvalidDataException("幸运职业日程必须为起止时间和职业转职四字段");
        var rows = ImmutableArray.CreateBuilder<LuckyProfessionSchedule>();
        for (int at = 0; at < schedule.Values.Length; at += 4)
        {
            var values = schedule.Values;
            if (values[at].Type != 6 || values[at + 1].Type != 6 ||
                values[at + 2] is not { Type: 0, Value: >= 0 and <= 15 } job ||
                values[at + 3] is not { Type: 0, Value: >= 0 and <= 127 } grow)
                throw new InvalidDataException("幸运职业日程字段类型无效");
            long starts = Date(pvf.ResolveString(values[at].Value)), ends = Date(pvf.ResolveString(values[at + 1].Value));
            if (starts != 0 && ends <= starts || starts == 0 && ends != 0)
                throw new InvalidDataException("幸运职业日程期限无效");
            rows.Add(new(starts, ends, new((byte)job.Value, (byte)grow.Value)));
        }
        Schedule = rows.ToImmutable();
        var professions = ImmutableDictionary.CreateBuilder<byte, ImmutableArray<LuckyProfession>>();
        foreach (var (id, path) in CharacterCatalog.ReadJobList(pvf))
        {
            var growth = CharacterCatalog.ParseGrowthOptions(pvf.ReadText("character/" + path), (byte)id);
            var targets = ImmutableArray.CreateBuilder<LuckyProfession>();
            foreach (var grow in growth.GrowTypes)
            {
                targets.Add(new((byte)id, grow.Value));
                for (int awakening = 0; awakening < grow.Awakenings.Count; awakening++)
                    if (!string.IsNullOrWhiteSpace(grow.Awakenings[awakening]))
                        targets.Add(new((byte)id, checked((byte)(grow.Value | (awakening + 1) << 4))));
            }
            if (targets.Count == 0) throw new InvalidDataException("幸运职业缺少合法成长分支");
            professions.Add((byte)id, targets.Distinct().OrderBy(row => row.GrowType).ToImmutableArray());
        }
        Professions = professions.ToImmutable();
        KillExperiencePercent = Scalar("monster kill exp buff ratio");
        ClearExperiencePercent = Scalar("dungeon clear exp buff ratio");
        QuestExperiencePercent = Scalar("quest clear buff ratio");
        PartyExperiencePercent = Scalar("party play buff ratio");
        FatigueAccelerationPercent = Scalar("fatigue accel percent");
        int Scalar(string name)
        {
            var found = document.Sections.Where(field => field.Name == name).ToArray();
            return found is [{ Values: [{ Type: 0, Value: >= 0 } value] }] ? value.Value
                : throw new InvalidDataException("幸运职业比例字段无效: " + name);
        }
    }

    static long Date(string value) => value == "0000-00-00 00:00:00" ? 0 :
        DateTimeOffset.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date.ToUnixTimeSeconds()
            : throw new InvalidDataException("幸运职业日程日期无效");
}
