using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PvfLib;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
if (args.Length != 3 || args[0] is not ("generate" or "verify" or "generate-targets" or "verify-targets"))
    throw new ArgumentException("Usage: FiendwarResourcePatch generate base.pvf output-directory | verify base.pvf candidate.pvf | generate-targets base.pvf output-directory | verify-targets base.pvf raw-directory-or-candidate.pvf");
using var archive = PvfArchive.Open(args[1]);
if (args[0].EndsWith("-targets", StringComparison.Ordinal))
{
    TargetMarkPatch.Run(archive, args[2], args[0] == "verify-targets");
    return;
}
using var candidate = args[0] == "verify" ? PvfArchive.Open(args[2]) : null;
string output = Path.GetFullPath(args[2]);
if (candidate is null) Directory.CreateDirectory(output);
var replacements = new List<object>();
for (int player = 1; player <= 4; player++)
{
    string prefix = $"passiveobject/actionobject/fiendwar_kr/monster/boss_cosmofiend/icepattern{player}p/action/icepattern{player}p";
    string path = prefix + "_prison.act";
    var tokens = archive.ReadTokens(path);
    var raw = archive.ReadRaw(path);
    var behaviors = Blocks("BEHAVIOR").ToArray();
    var triggers = Blocks("TRIGGER").ToArray();
    var rescue = triggers.Where(block => block.Length > 1 && Tag(block[1]) == "[IF]" &&
        block.Any(token => token.Type == 6 && archive.ResolveString(token.Value) == "time") &&
        block.Any(token => token is { Type: 0, Value: 3000 })).ToArray();
    if (rescue.Length != 1 || behaviors.Length != 6 ||
        !behaviors[3].Select(Tag).SequenceEqual(new[] { "[BEHAVIOR]", "[SET ACTION]", "[CUSTOM]", null, "[NOW]", "[/BEHAVIOR]" }) ||
        behaviors[3][3] is not { Type: 0, Value: 2 } ||
        triggers.Any(block => block.Any(token => token is { Type: 0, Value: 10000 })))
        throw new InvalidDataException("Unsupported native prison behavior: " + path);
    var last = archive.ReadText(prefix + "_last.act");
    if (!last.Contains("[SET DAMAGE BOX]", StringComparison.Ordinal) || !last.Contains("[STAND]", StringComparison.Ordinal))
        throw new InvalidDataException("Native exit does not release its parent: " + path);
    var addition = rescue[0].ToArray();
    int delay = Array.FindIndex(addition, token => token is { Type: 0, Value: 3000 });
    addition[delay] = new(0, 10000);
    if (addition[^2] is not { Type: 0, Value: 2 }) throw new InvalidDataException("Unexpected rescue behavior target");
    addition[^2] = new(0, 3);
    byte[] patched = new byte[raw.Length + addition.Length * 5];
    raw.CopyTo(patched, 0);
    for (int i = 0; i < addition.Length; i++)
    {
        patched[raw.Length + i * 5] = addition[i].Type;
        BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan(raw.Length + i * 5 + 1), addition[i].Value);
    }
    if (candidate is not null)
    {
        if (!candidate.ReadRaw(path).AsSpan().SequenceEqual(patched) || candidate.ReadTokens(path).Length != tokens.Length + addition.Length)
            throw new InvalidDataException("Candidate does not contain the exact prison delta: " + path);
        Console.WriteLine($"VERIFY path={path} originalPrefixUnchanged=true addedTokens={addition.Length} releaseMs=10000");
        continue;
    }
    string target = Path.Combine(output, Path.GetFileName(path) + ".raw");
    File.WriteAllBytes(target, patched);
    replacements.Add(new { path, raw_file = target, expected_before_sha256 = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant() });
    Console.WriteLine($"PATCH path={path} originalBytes={raw.Length} addedTokens={addition.Length} releaseMs=10000 behavior=3 originalPrefixUnchanged=true");

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
if (candidate is null)
    File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new { replacements }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("PASS four team prison deltas; source PVF read-only; no whole-PVF hash");

string? Tag(PvfToken token) => token.Type == 3 ? archive.ResolveString(token.Value) : null;
