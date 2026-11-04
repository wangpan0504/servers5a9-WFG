using PvfLib.Scripting;

namespace PvfLib;

public sealed record SharedEffectOutcome(int Index, int Weight, int RewardItemId, string DodgeAnimation, string Animation)
{
    public string? AdditionalAnimation { get; init; }
}

public sealed record SharedEffectConsumableFile(StackableFile Stackable, int? SharedEffect, int CoolGroup,
    int CooltimeMaintenance, IReadOnlyList<SharedEffectOutcome> Outcomes, IReadOnlyList<string> Errors)
{
    public static SharedEffectConsumableFile Parse(int id, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var errors = document.Diagnostics.ToList();
        int? Scalar(string name)
        {
            var sections = document.Sections.Where(row => row.Name == name).ToArray();
            if (sections.Length == 0) return null;
            if (sections is [{ Children.Length: 0, Values: [{ Type: 0 } value] }]) return value.Value;
            errors.Add(name);
            return null;
        }
        var outcomes = new List<SharedEffectOutcome>();
        var tables = document.Sections.Where(row => row.Name == "chn random image percent").ToArray();
        if (tables is [{ Children.Length: 0 } table] && table.Values.Length >= 5)
        {
            // 同一结果表采用两条或三条动画路径；额外路径只保留原文，不改变结果序号和奖励。
            int width = table.Values.Length > 5 && table.Values[5].Type == 6 ? 6 : 5;
            if (table.Values.Length % width != 0)
                errors.Add("chn random image percent");
            for (int at = 0; at + width <= table.Values.Length; at += width)
            {
                var row = table.Values.AsSpan(at, width);
                if (row[0].Type != 0 || row[1].Type != 0 || row[2].Type != 0 || row[3].Type != 6 || row[4].Type != 6 ||
                    width == 6 && row[5].Type != 6)
                {
                    errors.Add("chn random image percent");
                    break;
                }
                outcomes.Add(new(row[0].Value, row[1].Value, row[2].Value, resolve(row[3].Value), resolve(row[4].Value))
                { AdditionalAnimation = width == 6 ? resolve(row[5].Value) : null });
            }
        }
        else errors.Add("chn random image percent");
        return new(StackableFile.Parse(id, path, tokens, resolve), Scalar("shared effect"), Scalar("cooltime group") ?? -id,
            Scalar("cooltime maintenance") ?? 0, outcomes.AsReadOnly(), errors.AsReadOnly());
    }
}
