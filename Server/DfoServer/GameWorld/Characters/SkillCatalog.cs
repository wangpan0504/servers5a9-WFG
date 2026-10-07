using PvfLib;

namespace DfoServer.GameWorld.Characters;

internal readonly record struct SkillPointTableEntry(int Level, int Value);

internal sealed class SkillCatalog
{
    private readonly int[] cumulativeSp;

    private SkillCatalog(SkillPointTableEntry[] sp, SkillPointTableEntry[] tp)
    {
        SpTable = Array.AsReadOnly(sp);
        TpTable = Array.AsReadOnly(tp);
        cumulativeSp = new int[sp.Length + 1];
        for (int i = 0; i < sp.Length; i++)
            cumulativeSp[i + 1] = checked(cumulativeSp[i] + sp[i].Value);
    }

    public IReadOnlyList<SkillPointTableEntry> SpTable { get; }

    // TP 仅保留原始资源，不把尚未闭合的累计规则或余额当作客户端合同。
    public IReadOnlyList<SkillPointTableEntry> TpTable { get; }

    public int GetLevelSpTotal(int level)
    {
        if (level < 1 || level >= cumulativeSp.Length)
            throw new ArgumentOutOfRangeException(nameof(level));
        // 028A6450 的两个迭代器界限为 begin+1 与 begin+(level+1)。
        return cumulativeSp[level];
    }

    public static SkillCatalog Load(PvfArchive pvf)
    {
        return Parse(pvf.ReadTokens("etc/sptable.etc"), pvf.ResolveString);
    }

    internal static SkillCatalog Parse(PvfToken[] tokens, Func<int, string> resolveString)
    {
        SkillPointTableEntry[] sp = ReadTable("sp table");
        SkillPointTableEntry[] tp = ReadTable("tp table");
        if (sp.Length == 0 || !sp.Select(entry => entry.Level).SequenceEqual(Enumerable.Range(1, sp.Length)))
            throw new InvalidDataException("SP 表必须从等级1开始连续排列");
        if (tp.Zip(tp.Skip(1)).Any(pair => pair.First.Level >= pair.Second.Level))
            throw new InvalidDataException("TP 表等级必须严格递增");
        return new SkillCatalog(sp, tp);

        SkillPointTableEntry[] ReadTable(string tag)
        {
            var entries = new List<SkillPointTableEntry>();
            bool found = false;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Type != 3 || resolveString(tokens[i].Value) != "[" + tag + "]")
                    continue;
                if (found)
                    throw new InvalidDataException("重复技能点表: " + tag);
                found = true;
                i++;
                while (i < tokens.Length && tokens[i].Type != 3)
                {
                    if (i + 1 >= tokens.Length || tokens[i].Type != 0 || tokens[i + 1].Type != 0 ||
                        tokens[i].Value < 1 || tokens[i + 1].Value < 0)
                        throw new InvalidDataException("无效技能点表整数对: " + tag);
                    entries.Add(new SkillPointTableEntry(tokens[i].Value, tokens[i + 1].Value));
                    i += 2;
                }

                if (i >= tokens.Length || resolveString(tokens[i].Value) != "[/" + tag + "]")
                    throw new InvalidDataException("技能点表结束标签缺失: " + tag);
            }

            if (!found)
                throw new InvalidDataException("技能点表缺失: " + tag);
            return entries.ToArray();
        }
    }
}
