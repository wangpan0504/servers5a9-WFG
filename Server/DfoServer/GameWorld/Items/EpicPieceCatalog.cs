using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal sealed record EpicPieceEntry(int Index, int EquipmentId, int PieceId);
internal sealed record EpicPieceRecipe(int EquipmentId, ImmutableArray<ItemMaterialCost> Costs);

internal sealed class EpicPieceCatalog
{
    public ImmutableArray<EpicPieceEntry> Entries { get; }
    public FrozenDictionary<int, EpicPieceEntry> ByPiece { get; }
    public FrozenDictionary<int, EpicPieceRecipe> Recipes { get; }
    public string OrderHash { get; }

    public EpicPieceCatalog(PvfArchive pvf) : this(pvf.ReadText("etc/epicpieceinfo.etc")) { }

    internal EpicPieceCatalog(string text)
    {
        int[] pairs = Integers(Block(text, "piece list"));
        if (pairs.Length == 0 || pairs.Length % 2 != 0 || pairs.Any(value => value <= 0))
            throw new InvalidDataException("PVF史诗碎片列表无效");
        Entries = Enumerable.Range(0, pairs.Length / 2).Select(index => new EpicPieceEntry(index, pairs[index * 2], pairs[index * 2 + 1])).ToImmutableArray();
        if (Entries.Select(entry => entry.EquipmentId).Distinct().Count() != Entries.Length ||
            Entries.Select(entry => entry.PieceId).Distinct().Count() != Entries.Length)
            throw new InvalidDataException("PVF史诗碎片列表存在重复编号");
        ByPiece = Entries.ToFrozenDictionary(entry => entry.PieceId);
        var byEquipment = Entries.ToDictionary(entry => entry.EquipmentId);
        var recipes = new Dictionary<int, EpicPieceRecipe>();
        foreach (Match match in Regex.Matches(Block(text, "equipment recipe info"), @"\[info\](.*?)\[/info\]", RegexOptions.Singleline))
        {
            int[] output = Integers(Section(match.Groups[1].Value, "output"));
            int[] costs = Integers(Block(match.Groups[1].Value, "recipe"));
            if (output.Length != 1 || !byEquipment.TryGetValue(output[0], out var entry) || costs.Length < 2 ||
                costs.Length % 2 != 0 || costs.Any(value => value <= 0) || costs[0] != entry.PieceId)
                throw new InvalidDataException("PVF史诗制作配方无效");
            var materials = Enumerable.Range(0, costs.Length / 2).Select(index => new ItemMaterialCost(costs[index * 2], costs[index * 2 + 1]))
                .GroupBy(cost => cost.ItemId).Select(group => new ItemMaterialCost(group.Key, checked((int)group.Sum(cost => (long)cost.Count)))).ToImmutableArray();
            if (!recipes.TryAdd(output[0], new(output[0], materials)))
                throw new InvalidDataException("PVF史诗制作配方重复");
        }
        Recipes = recipes.ToFrozenDictionary();
        // 数组按PVF原顺序存储；顺序变化时必须迁移，不能把旧余额套到另一个碎片编号。
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
            foreach (int value in pairs)
                writer.Write(value);
        OrderHash = Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
}
