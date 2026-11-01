using System.Buffers.Binary;
using PvfLib;

internal static class PrisonPatch
{
    internal static List<ResourceDelta> Prepare(PvfArchive archive, bool allowPatched = false)
    {
        var patches = new List<ResourceDelta>();
        for (int player = 1; player <= 4; player++)
        {
            string prefix = $"passiveobject/actionobject/fiendwar_kr/monster/boss_cosmofiend/icepattern{player}p/action/icepattern{player}p";
            string path = prefix + "_prison.act";
            var tokens = archive.ReadTokens(path);
            var raw = archive.ReadRaw(path);
            byte[] before = raw;
            var behaviors = Blocks("BEHAVIOR").ToArray();
            var triggers = Blocks("TRIGGER").ToArray();
            var rescue = triggers.Where(block => block.Length > 1 && Tag(block[1]) == "[IF]" &&
                block.Any(token => token.Type == 6 && archive.ResolveString(token.Value) == "time") &&
                block.Any(token => token is { Type: 0, Value: 3000 })).ToArray();
            if (rescue.Length != 1 || behaviors.Length != 6 ||
                !behaviors[3].Select(Tag).SequenceEqual(new[] { "[BEHAVIOR]", "[SET ACTION]", "[CUSTOM]", null, "[NOW]", "[/BEHAVIOR]" }) ||
                behaviors[3][3] is not { Type: 0, Value: 2 } || raw.Length != tokens.Length * 5)
                throw new InvalidDataException("Unsupported native prison behavior: " + path);
            var last = archive.ReadText(prefix + "_last.act");
            if (!last.Contains("[SET DAMAGE BOX]", StringComparison.Ordinal) || !last.Contains("[STAND]", StringComparison.Ordinal))
                throw new InvalidDataException("Native exit does not release its parent: " + path);
            var addition = rescue[0].ToArray();
            int delay = Array.FindIndex(addition, token => token is { Type: 0, Value: 3000 });
            addition[delay] = new(0, 10000);
            if (addition[^2] is not { Type: 0, Value: 2 }) throw new InvalidDataException("Unexpected rescue behavior target");
            addition[^2] = new(0, 3);
            byte[] encodedAddition = new byte[addition.Length * 5];
            for (int i = 0; i < addition.Length; i++)
            {
                encodedAddition[i * 5] = addition[i].Type;
                BinaryPrimitives.WriteInt32LittleEndian(encodedAddition.AsSpan(i * 5 + 1), addition[i].Value);
            }
            var existing = triggers.Where(block => block.Any(token => token is { Type: 0, Value: 10000 })).ToArray();
            if (existing.Length != 0)
            {
                if (!allowPatched || existing.Length != 1 || raw.Length <= encodedAddition.Length ||
                    !raw.AsSpan(raw.Length - encodedAddition.Length).SequenceEqual(encodedAddition))
                    throw new InvalidDataException("Unsupported existing prison delta: " + path);
                // 只认可本工具追加的完整末尾触发器，不凭相同时长或单个标签跳过未知修改。
                raw = raw[..^encodedAddition.Length];
            }
            byte[] patched = new byte[raw.Length + addition.Length * 5];
            raw.CopyTo(patched, 0);
            encodedAddition.CopyTo(patched, raw.Length);
            patches.Add(new(path, Path.GetFileName(path) + ".raw", before, patched));

            IEnumerable<PvfToken[]> Blocks(string name)
            {
                for (int at = 0; at < tokens.Length; at++)
                {
                    if (Tag(tokens[at]) != $"[{name}]") continue;
                    // [TRIGGER]编号[ON/OFF]是行为内指令，不是触发器定义。
                    if (name == "TRIGGER" && at + 1 < tokens.Length && tokens[at + 1].Type == 0) continue;
                    int end = Array.FindIndex(tokens, at + 1, token => Tag(token) == $"[/{name}]");
                    if (end < 0) throw new InvalidDataException("Unterminated native block: " + name);
                    yield return tokens[at..(end + 1)];
                    at = end;
                }
            }
        }
        return patches;

        string? Tag(PvfToken token) => token.Type == 3 ? archive.ResolveString(token.Value) : null;
    }
}
