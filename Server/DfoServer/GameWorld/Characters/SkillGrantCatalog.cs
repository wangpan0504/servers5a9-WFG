using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Characters;

internal sealed record FixedSkillRule(int Interval, int Add, bool AppliesContract);

// CHR 的创角、转职和累计觉醒赠技；不根据当前存档推断免费等级。
internal sealed class SkillGrantCatalog(Dictionary<(byte Job, byte Growth), CharacterSkill[]> entries)
{
    public IEnumerable<(byte Job, ushort Id)> SkillIds => entries.SelectMany(entry =>
        entry.Value.Select(skill => (entry.Key.Job, skill.Id))).Distinct();
    public IReadOnlyList<CharacterSkill> Get(byte job, byte growth) =>
        entries.TryGetValue((job, growth), out var skills) ? Array.AsReadOnly(skills) : throw new NotSupportedException("普通成长索引未定义");

    public static SkillGrantCatalog Load(PvfArchive pvf)
    {
        var entries = new Dictionary<(byte, byte), CharacterSkill[]>();
        foreach (var job in List(pvf.ReadText("character/character.lst")))
        {
            string text = pvf.ReadText("character/" + job.Value);
            var stages = Regex.Matches(text, @"\[growtype (\d+)\]");
            int initialStart = text.IndexOf("[initial value]", StringComparison.Ordinal);
            var initial = Read(text[initialStart..stages[0].Index], "skill");
            for (int first = 0; first < stages.Count; first++)
            {
                int end = first + 1 < stages.Count ? stages[first + 1].Index : text.Length;
                string stage = text[stages[first].Index..end];
                var awakenings = Regex.Matches(stage, @"\[awakening (\d+)\]");
                for (int second = 0; second <= (first == 0 && job.Key is not (9 or 10) ? 0 : 2); second++)
                {
                    var free = initial.ToDictionary(skill => skill.Id, skill => skill.Level);
                    if (first > 0)
                        Merge(Read(stage[..(awakenings.Count == 0 ? stage.Length : awakenings[0].Index)], "skill"));
                    for (int awakeningIndex = 0; awakeningIndex < awakenings.Count; awakeningIndex++)
                    {
                        Match awakening = awakenings[awakeningIndex];
                        int index = int.Parse(awakening.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        if (index > second) continue;
                        // 仅数字阶段是边界；[awakening skill] 属于当前阶段，不能在赠技之前截断。
                        int next = awakeningIndex + 1 < awakenings.Count ? awakenings[awakeningIndex + 1].Index : stage.Length;
                        Merge(Read(stage[awakening.Index..next], "awakening skill"));
                    }
                    entries.Add((checked((byte)job.Key), checked((byte)(first | second << 4))),
                        free.OrderBy(pair => pair.Key).Select(pair => new CharacterSkill(pair.Key, pair.Value)).ToArray());
                    void Merge(IEnumerable<CharacterSkill> skills)
                    {
                        foreach (var skill in skills) free[skill.Id] = Math.Max(free.GetValueOrDefault(skill.Id), skill.Level);
                    }
                }
            }
        }
        return new(entries);

        static CharacterSkill[] Read(string text, string tag)
        {
            int[] values = Integers(Block(text, tag));
            if (values.Length % 2 != 0) throw new InvalidDataException("CHR技能赠送必须为ID/等级对");
            return Enumerable.Range(0, values.Length / 2).Select(index =>
                new CharacterSkill(checked((ushort)values[index * 2]), checked((byte)values[index * 2 + 1])))
                .Where(skill => skill.Level > 0).ToArray();
        }
    }
}
