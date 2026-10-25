using System.Globalization;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal enum GuildCompoundKind
{
    GuardianGem,
    GuildMedal
}

// A zero terminal rate means no grade upgrade; the domain decides same-grade medal recombination.
internal sealed record GuildCompoundRecipe(int Grade, int MaxCount, int RatePerMaterial)
{
    public bool Compoundable => RatePerMaterial > 0 && MaxCount is >= 2 and <= 4;

    public bool Accepts(int count) => count is >= 2 and <= 4 && count <= MaxCount;

    public int SuccessRateFor(int count) =>
        Accepts(count) ? (int)Math.Min(GuildCompoundLadder.RateDenominator, (long)RatePerMaterial * count) : 0;
}

internal sealed class GuildCompoundLadder
{
    public const int RateDenominator = 10_000;
    readonly GuildCompoundRecipe[] rows;

    GuildCompoundLadder(GuildCompoundRecipe[] rows) => this.rows = rows;

    public IReadOnlyList<GuildCompoundRecipe> Rows => rows;

    public GuildCompoundRecipe? Recipe(int grade) => Array.Find(rows, row => row.Grade == grade);

    // 只有可合成且存在下一档时才产出；末档或 0 率档返回 null。
    public int? NextGrade(int grade)
    {
        int index = Array.FindIndex(rows, row => row.Grade == grade);
        return index < 0 || !rows[index].Compoundable || index + 1 >= rows.Length ? null : rows[index + 1].Grade;
    }

    public static GuildCompoundLadder Parse(string text, string tag)
    {
        string section = Section(text, tag);
        if (section.Length == 0)
            throw new InvalidDataException($"PVF公会合成缺少{tag}段");
        string[] tokens = section.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 6 || tokens.Length % 3 != 0)
            throw new InvalidDataException($"PVF公会合成{tag}行宽无效");
        var rows = new GuildCompoundRecipe[tokens.Length / 3];
        for (int index = 0; index < rows.Length; index++)
        {
            if (!int.TryParse(tokens[index * 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int grade) ||
                !int.TryParse(tokens[index * 3 + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maximum) ||
                !int.TryParse(tokens[index * 3 + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rate))
                throw new InvalidDataException($"PVF公会合成{tag}含非整数项");
            if (grade < 0 || maximum is < 2 or > 4 || rate is < 0 or > RateDenominator)
                throw new InvalidDataException($"PVF公会合成{tag}第{index + 1}行范围无效");
            if (index > 0 && grade <= rows[index - 1].Grade)
                throw new InvalidDataException($"PVF公会合成{tag}品级未严格升序");
            rows[index] = new(grade, maximum, rate);
        }
        for (int index = 0; index < rows.Length; index++)
        {
            bool terminal = index == rows.Length - 1;
            if (terminal && rows[index].RatePerMaterial != 0)
                throw new InvalidDataException($"PVF公会合成{tag}末档概率必须为0");
            if (!terminal && rows[index].RatePerMaterial == 0)
                throw new InvalidDataException($"PVF公会合成{tag}非末档概率必须为正");
        }
        return new(rows);
    }
}

internal sealed class GuildDungeonCompoundCatalog
{
    public const string RootPath = "etc/guilddungeoncompound.etc";
    readonly GuildCompoundLadder guardianGems;
    readonly GuildCompoundLadder guildMedals;

    public GuildDungeonCompoundCatalog(PvfArchive archive) : this(archive.ReadText) { }

    internal GuildDungeonCompoundCatalog(Func<string, string> read)
    {
        string text = read(RootPath);
        guardianGems = GuildCompoundLadder.Parse(text, "compound info");
        // PVF 原文勋章段结束标记实际拼作 [/guid flag compound info]；Section 以下一段头截断，拼写不参与匹配。
        guildMedals = GuildCompoundLadder.Parse(text, "guild flag compound info");
    }

    public GuildCompoundLadder Ladder(GuildCompoundKind kind) =>
        kind == GuildCompoundKind.GuildMedal ? guildMedals : guardianGems;
}
