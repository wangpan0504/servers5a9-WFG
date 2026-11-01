using System.Buffers.Binary;
using PvfLib;

internal static class RevivalPatch
{
    const string IgnoreDiehard = "[ignore diehard]";
    const string TeamDirectory = "dungeon/fiendwar_kr/fiendwar_kr/";

    internal static List<ResourceDelta> Prepare(PvfArchive source, bool allowPatched = false)
    {
        // 从当前归档的完整字符串项查找，不能绑定固定偏移或使用私有池反射。
        if (!source.TryFindStringValue(IgnoreDiehard, out int tagOffset) ||
            source.ResolveString(tagOffset) != IgnoreDiehard)
            throw new InvalidDataException("Native revival tag is missing from the source string pool");

        var index = PvfScript.List(source.ReadText("dungeon/dungeon.lst"));
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var patches = new List<ResourceDelta>();
        byte[] addition = new byte[10];
        addition[0] = 3;
        BinaryPrimitives.WriteInt32LittleEndian(addition.AsSpan(1), tagOffset);
        addition[5] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(addition.AsSpan(6), 1);

        for (int id = 291100339; id <= 291100364; id++)
        {
            // 只认列表登记，不能用副本编号猜测文件名，也不能扫入旧模板或引导。
            if (!index.TryGetValue(id, out string? relativePath))
                throw new InvalidDataException("Missing registered team dungeon: " + id);
            string path = PvfArchive.NormalizePath("dungeon/" + relativePath);
            if (!path.StartsWith(TeamDirectory, StringComparison.Ordinal) ||
                !path.EndsWith(".dgn", StringComparison.Ordinal) || !paths.Add(path))
                throw new InvalidDataException("Unexpected registered team dungeon path: " + id);

            var tokens = source.ReadTokens(path);
            byte[] raw = source.ReadRaw(path);
            if (raw.Length != tokens.Length * 5)
                throw new InvalidDataException("Unsupported native dungeon token encoding: " + path);

            int[] existing = Find(IgnoreDiehard);
            if (Find("[zombie dungeon]").Length != 0 || Find("[disable rebirth]").Length != 0 ||
                Find("[ignore diehard except revive other]").Length != 0)
                throw new InvalidDataException("Native dungeon revival flags changed: " + path);

            int expectedCoins = id is 291100339 or 291100364 ? 8 : 4;
            int[] coins = Find("[coin limit]");
            if (coins.Length != 1 || coins[0] + 1 >= tokens.Length ||
                tokens[coins[0] + 1] != new PvfToken(0, expectedCoins) ||
                coins[0] + 2 < tokens.Length && tokens[coins[0] + 2].Type != 3)
                throw new InvalidDataException("Native team coin limit changed: " + path);

            if (existing.Length != 0)
            {
                // 幂等只接受本工具完整追加的末尾标签，不接受重复、缺值或未知变体。
                if (!allowPatched || existing.Length != 1 || existing[0] != tokens.Length - 2 ||
                    raw.Length < addition.Length || !raw.AsSpan(raw.Length - addition.Length).SequenceEqual(addition))
                    throw new InvalidDataException("Unsupported existing revival delta: " + path);
                patches.Add(new(path, $"fiendwar_team_{id}_revival.raw", raw, raw.ToArray()));
            }
            else
            {
                // 原始字节完整保留，只追加已证实由死亡资格分支消费的整数开关。
                byte[] patched = new byte[raw.Length + addition.Length];
                raw.CopyTo(patched, 0);
                addition.CopyTo(patched, raw.Length);
                patches.Add(new(path, $"fiendwar_team_{id}_revival.raw", raw, patched));
            }

            int[] Find(string tag) => Enumerable.Range(0, tokens.Length).Where(at =>
                tokens[at].Type == 3 && source.ResolveString(tokens[at].Value) == tag).ToArray();
        }
        return patches;
    }
}
