using System.Security.Cryptography;
using System.Text;
using PvfLib;

namespace DfoServer.GameWorld.Maps;

internal static class MapGeometrySignature
{
    private static readonly HashSet<string> Tags = new(StringComparer.Ordinal) { "[town movable area]", "[virtual movable area]", "[npc]" };

    // DCG1规范化实际生效的完整几何块，包含条件子段；忽略池ID、标签大小写及块间排列。
    public static byte[] Canonical(string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        string Label(int at) => tokens[at].Type == 3 ? resolve(tokens[at].Value).ToLowerInvariant() : "";
        var fields = new List<(string Tag, int Occurrence, int Begin, int End)>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < tokens.Count; ++i)
        {
            string tag = Label(i);
            if (!Tags.Contains(tag)) continue;
            string closing = "[/" + tag[1..];
            int end = i + 1;
            while (end < tokens.Count && Label(end) != closing)
            {
                if (Tags.Contains(Label(end))) throw new InvalidDataException("World geometry block missing closure");
                ++end;
            }
            if (end == tokens.Count) throw new InvalidDataException("World geometry block missing closure");
            int occurrence = occurrences.GetValueOrDefault(tag);
            occurrences[tag] = occurrence + 1;
            fields.Add((tag, occurrence, i + 1, end));
            i = end;
        }
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true), true);
        void Text(string text) { byte[] utf8 = Encoding.UTF8.GetBytes(text); writer.Write(utf8.Length); writer.Write(utf8); }
        writer.Write("DCG1"u8); Text(path); writer.Write(fields.Count);
        foreach (var field in fields.OrderBy(f => f.Tag, StringComparer.Ordinal).ThenBy(f => f.Occurrence))
        {
            Text(field.Tag); writer.Write(field.Occurrence); writer.Write(field.End - field.Begin);
            for (int i = field.Begin; i < field.End; ++i)
            {
                var token = tokens[i]; writer.Write((uint)token.Type);
                if (token.Type == 3) Text(Label(i));
                else if (token.Type == 6) Text(resolve(token.Value));
                else writer.Write(token.Value);
            }
        }
        return bytes.ToArray();
    }
    public static byte[] Compute(string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve) => SHA256.HashData(Canonical(path,tokens,resolve));
}
