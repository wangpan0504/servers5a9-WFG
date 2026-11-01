using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using PvfLib;

internal static class TargetMarkPatch
{
    internal static void Run(PvfArchive source, string target, bool verify)
    {
        string output = Path.GetFullPath(target);
        bool rawDirectory = Directory.Exists(output);
        using var candidate = verify && !rawDirectory ? PvfArchive.Open(output) : null;
        var replacements = new List<object>();
        var patches = new List<(string Path, string RawName, byte[] Before, byte[] After)>();
        foreach (string mode in new[] { "fiendwar_kr", "fiendwar_g" })
        for (int slot = 1; slot <= 3; slot++)
        {
            string path = $"passiveobject/actionobject/{mode}/monster/boss_humantraitor/targetmark/action/targetchange{slot + 1}.act";
            var tokens = source.ReadTokens(path);
            var raw = source.ReadRaw(path);
            int[] checks = Enumerable.Range(0, tokens.Length).Where(index =>
                tokens[index].Type == 3 && source.ResolveString(tokens[index].Value) == "[CHECK INDEX EXCLUDE DEAD]").ToArray();
            if (checks.Length != 2 || checks.Any(index => index + 1 >= tokens.Length) ||
                tokens[checks[0] + 1] != new PvfToken(0, slot) || tokens[checks[1] + 1] != new PvfToken(0, 0) ||
                raw.Length != tokens.Length * 5)
                throw new InvalidDataException("Unsupported native target selection: " + path);
            for (int branch = 0; branch < checks.Length; branch++)
            {
                int at = checks[branch];
                string? Tag(int index) => index >= 0 && index < tokens.Length && tokens[index].Type == 3
                    ? source.ResolveString(tokens[index].Value) : null;
                if (at + 5 >= tokens.Length || Tag(at - 1) != "[PARTY TARGET]" ||
                    Tag(at + 2) != "[/PARTY TARGET]" || Tag(at + 3) != "[CHECKED NO]" ||
                    Tag(at + 4) != (branch == 0 ? "[>]" : "[=]") || tokens[at + 5] != new PvfToken(0, 0))
                    throw new InvalidDataException("Native target presence predicates changed: " + path);
            }
            string text = source.ReadText(path);
            if (!text.Contains("[SET MOVE PARENT]", StringComparison.Ordinal) ||
                !text.Contains("[CUSTOM]", StringComparison.Ordinal) ||
                tokens.Count(token => token.Type == 3 && source.ResolveString(token.Value) == "[ON SET ACTION]") != 2)
                throw new InvalidDataException("Native target selection branches changed: " + path);
            // 成功和失败必须检查同一个目标；不能用队伍首槽代替刚刚选中的槽。
            byte[] patched = raw.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan((checks[1] + 1) * 5 + 1), slot);
            patches.Add((path, $"{mode}_targetchange{slot + 1}.raw", raw, patched));
        }
        if (!verify) Directory.CreateDirectory(output);
        foreach (var patch in patches)
        {
            string rawPath = Path.Combine(output, patch.RawName);
            if (verify)
            {
                byte[] actual = candidate is not null ? candidate.ReadRaw(patch.Path) : File.ReadAllBytes(rawPath);
                if (!actual.AsSpan().SequenceEqual(patch.After))
                    throw new InvalidDataException("Candidate does not contain the exact target retry delta: " + patch.Path);
                Console.WriteLine($"VERIFY path={patch.Path} onlySelectedSlotFallbackChanged=true");
            }
            else
            {
                File.WriteAllBytes(rawPath, patch.After);
                replacements.Add(new { path = patch.Path, raw_file = rawPath,
                    expected_before_sha256 = Convert.ToHexString(SHA256.HashData(patch.Before)).ToLowerInvariant() });
                Console.WriteLine($"PATCH path={patch.Path} onlySelectedSlotFallbackChanged=true");
            }
        }
        if (!verify)
            File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new { replacements },
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS six team/guide target retry deltas; source PVF read-only; no whole-PVF hash");
    }
}
