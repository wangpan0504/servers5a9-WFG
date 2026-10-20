using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Items;

internal sealed record EquipmentTextureChangeGroup(int ItemId, byte Material, ImmutableArray<int> Variants);

// etc/equipmenttexturechange.etc：每个 [group] 含 1 个输入与 5 个输出，输出顺序固定为
// 布甲/皮甲/轻甲/重甲/板甲，同族 5 件互为转换目标。整表在装配期校验，运行期只做查表。
internal sealed class EquipmentTextureChangeCatalog
{
    public const string ResourcePath = "etc/equipmenttexturechange.etc";
    // equipment.lst 把材质写成路径目录段：character/common/pants/cloth/…。
    static readonly string[] MaterialDirectories = ["cloth", "leather", "larmor", "harmor", "plate"];

    readonly FrozenDictionary<int, EquipmentTextureChangeGroup> groups;

    public EquipmentTextureChangeCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> equipment)
        : this(archive.Contains(ResourcePath) ? archive.ReadText(ResourcePath)
            : throw new InvalidDataException("PVF缺少材质转换表: " + ResourcePath), equipment) { }

    internal EquipmentTextureChangeCatalog(string text, IReadOnlyDictionary<int, string> equipment)
    {
        var parsed = new Dictionary<int, EquipmentTextureChangeGroup>();
        int index = 0;
        foreach (Match match in Regex.Matches(text, @"\[group\](.*?)\[/group\]", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            index++;
            int[] input = Integers(Section(match.Groups[1].Value, "input"));
            int[] output = Integers(Block(match.Groups[1].Value, "output"));
            if (input.Length != 1 || output.Length != MaterialDirectories.Length || input[0] <= 0 ||
                output.Any(id => id <= 0) || output.Distinct().Count() != output.Length || !output.Contains(input[0]))
                throw new InvalidDataException($"PVF材质转换组无效: 第{index}组");
            var variants = output.ToImmutableArray();
            for (int material = 0; material < variants.Length; material++)
            {
                int id = variants[material];
                // 输出位置同时决定材质：路径目录段必须与位置一致，否则转换会静默换错材质。
                if (!equipment.TryGetValue(id, out string? path) || MaterialOf(path) != material)
                    throw new InvalidDataException($"PVF材质转换输出顺序无效: {id}");
                if (parsed.TryGetValue(id, out var existing) && !existing.Variants.SequenceEqual(variants))
                    throw new InvalidDataException($"PVF材质转换族冲突: {id}");
                parsed[id] = new(id, checked((byte)material), variants);
            }
        }
        if (index == 0)
            throw new InvalidDataException("PVF材质转换表为空");
        groups = parsed.ToFrozenDictionary();
    }

    public bool TryGet(int itemId, out EquipmentTextureChangeGroup group) => groups.TryGetValue(itemId, out group!);

    static int MaterialOf(string path)
    {
        for (int material = 0; material < MaterialDirectories.Length; material++)
            if (path.Contains("/" + MaterialDirectories[material] + "/", StringComparison.OrdinalIgnoreCase))
                return material;
        return -1;
    }
}
