using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal sealed record DisjointAdditional(int[] Items, double GreatDivisor, double NormalDivisor, double GreatChance, double NormalChance);
internal sealed record DisjointSoul(int ItemId, double LevelDivisor, double GreatChance, double NormalChance);
internal sealed record DisjointIndependent(int ItemId, int Probability, int Count);

internal sealed class DisjointCatalog
{
    public int CubeItemId { get; }
    public double CubeBase { get; }
    public double[] CubeMultipliers { get; }
    public DisjointAdditional?[] Additional { get; }
    public DisjointSoul?[] Souls { get; }
    public FrozenSet<int> ExcludedSouls { get; }
    public FrozenSet<int> SpecialResults { get; }
    public FrozenDictionary<int, DisjointIndependent[]> IndependentResults { get; }
    public FrozenDictionary<int, int> PieceResults { get; }
    public FrozenSet<int> EventResults { get; }
    public FrozenDictionary<int,int> OperationHopeResults { get; }

    public DisjointCatalog(PvfArchive pvf) : this(pvf.ReadText("etc/disjoint.etc"), pvf.ReadText("etc/epicpieceinfo.etc"),
        pvf.ReadText("etc/dungeonetc/operationhopedungeoninfo.etc")) { }

    internal DisjointCatalog(string text, string epicPieces, string? operationHope = null)
    {
        int[] hopeResults = operationHope is null ? [] : Integers(Block(operationHope,"disjoint item"));
        if (hopeResults.Length % 2 != 0 || operationHope is not null && hopeResults.Length == 0 ||
            hopeResults.Any(value=>value<=0) || hopeResults.Where((_,index)=>index%2==0).Distinct().Count()!=hopeResults.Length/2)
            throw new InvalidDataException("代号希望分解产物必须为唯一物品与正数量对");
        OperationHopeResults = Enumerable.Range(0,hopeResults.Length/2).ToFrozenDictionary(i=>hopeResults[i*2],i=>hopeResults[i*2+1]);
        var cubes = Regex.Matches(text, @"\[cube index\](.*?)\[/cube index\]", RegexOptions.Singleline)
            .Single(match => match.Groups[1].Value.Contains("[no element]", StringComparison.Ordinal));
        CubeItemId = Integers(cubes.Groups[1].Value).First();
        double[] cubeConstants = Numbers(Section(text, "cube creation const"));
        if (cubeConstants.Length != 8 || cubeConstants[0] <= 0 || cubeConstants.Any(value => value < 0))
            throw new InvalidDataException("PVF普通分解晶体参数无效");
        CubeBase = cubeConstants[0];
        CubeMultipliers = cubeConstants[1..];
        var additional = Rows(Section(text, "additional result"));
        var souls = Rows(Section(text, "additional result expand"));
        double[] constants = Numbers(Section(text, "additional result const"));
        double[] soulConstants = Numbers(Section(text, "additional result expand const"));
        Additional = new DisjointAdditional?[7];
        Souls = new DisjointSoul?[7];
        int at = 0, soulAt = 0;
        for (int rarity = 0; rarity < 7; rarity++)
        {
            // 空行只消耗一个0；本版异界灵魂行为空，不能让后面的传说行错位。
            double[] row = Take(constants, ref at, additional[rarity].Length == 0 ? 1 : 4);
            if (additional[rarity].Length != 0)
                Additional[rarity] = new(additional[rarity], row[0], row[1], row[2], row[3]);
            else if (row[0] != 0)
                throw new InvalidDataException("PVF分解空行常量无效");
            row = Take(soulConstants, ref soulAt, souls[rarity].Length == 0 ? 1 : 3);
            if (souls[rarity].Length == 1)
                Souls[rarity] = new(souls[rarity][0], row[0], row[1], row[2]);
            else if (souls[rarity].Length != 0 || row[0] != 0)
                throw new InvalidDataException("PVF分解灵魂行无效");
        }
        if (at != constants.Length || soulAt != soulConstants.Length)
            throw new InvalidDataException("PVF分解常量存在未消费字段");
        ExcludedSouls = Integers(Block(text, "exclude additional result expand")).ToFrozenSet();
        int[] events = Integers(Section(text, "event result"));
        int[] pieces = Integers(Block(epicPieces, "piece list"));
        if (events.Length % 3 != 0 || pieces.Length % 2 != 0)
            throw new InvalidDataException("PVF特殊分解索引无效");
        var independent = new Dictionary<int, DisjointIndependent[]>();
        foreach (Match match in Regex.Matches(Block(text, "independent result"), @"\[particular item\](.*?)\[/particular item\]", RegexOptions.Singleline))
        {
            int[] outputs = Integers(Block(match.Groups[1].Value, "output index"));
            var rows = outputs.Length % 3 == 0 ? Enumerable.Range(0, outputs.Length / 3)
                .Select(index => new DisjointIndependent(outputs[index * 3], outputs[index * 3 + 1], outputs[index * 3 + 2])).ToArray() : [];
            foreach (int input in Integers(Block(match.Groups[1].Value, "input index")))
                if (input <= 0 || !independent.TryAdd(input, rows))
                    throw new InvalidDataException("PVF独立分解输入无效或重复");
        }
        IndependentResults = independent.ToFrozenDictionary();
        EventResults = events.Where((_, index) => index % 3 == 0).ToFrozenSet();
        if (pieces.Any(value => value <= 0))
            throw new InvalidDataException("PVF分解碎片映射无效");
        PieceResults = Enumerable.Range(0, pieces.Length / 2).ToFrozenDictionary(index => pieces[index * 2], index => pieces[index * 2 + 1]);
        // 保留完整特殊输入索引供诊断；活动奖励的替代/追加语义尚未确认。
        SpecialResults = Regex.Matches(text, @"\[input index\](.*?)\[/input index\]", RegexOptions.Singleline)
            .SelectMany(match => Integers(match.Groups[1].Value))
            .Concat(events.Where((_, index) => index % 3 == 0))
            .Concat(pieces.Where((_, index) => index % 2 == 0)).ToFrozenSet();
    }

    static int[][] Rows(string section)
    {
        int[] values = Integers(section);
        var rows = new int[7][];
        int at = 0;
        for (int rarity = 0; rarity < rows.Length; rarity++)
        {
            if (at >= values.Length)
                throw new InvalidDataException("PVF分解奖励缺行");
            int count = values[at++];
            if (count < 0 || count > values.Length - at)
                throw new InvalidDataException("PVF分解奖励数量无效");
            rows[rarity] = values[at..(at + count)];
            if (rows[rarity].Any(id => id <= 0))
                throw new InvalidDataException("PVF分解奖励编号无效");
            at += count;
        }
        if (at != values.Length)
            throw new InvalidDataException("PVF分解奖励存在未消费字段");
        return rows;
    }

    static double[] Numbers(string section) => section.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();

    static double[] Take(double[] values, ref int at, int count)
    {
        if (count > values.Length - at)
            throw new InvalidDataException("PVF分解常量不足");
        double[] row = values[at..(at + count)];
        if (row.Any(value => !double.IsFinite(value) || value < 0))
            throw new InvalidDataException("PVF分解常量无效");
        at += count;
        return row;
    }
}
