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

    private static readonly HashSet<string> DungeonTags = new(StringComparer.Ordinal)
    {
        "[pathgate pos]", "[pathgate recognize range]", "[dungeon movable area]", "[dungeon start area]",
        "[tile]", "[extended tile]", "[tile files]", "[tile map]", "[import script]", "[force map width]",
        "[ignore movable rect]", "[camera edge exception]"
    };
    private static readonly HashSet<string> TileTags = new(StringComparer.Ordinal) { "[pass type]" };
    internal static readonly HashSet<string> AnchorTags = new(StringComparer.Ordinal)
    {
        "[animation]", "[passive object]", "[monster]", "[ai character]", "[event monster position]", "[special passive object]"
    };
    public static byte[] CanonicalAnchors(string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve) =>
        ScalarCanonical("DCG3"u8,path,tokens,resolve,DungeonTags.Concat(Tags).Concat(AnchorTags).ToHashSet(StringComparer.Ordinal),false);

    public static byte[] ComputeAnchors(string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve) =>
        SHA256.HashData(CanonicalAnchors(path,tokens,resolve));

    // 副本标量段在下一个标签或EOF结束；不借用城镇DCG1必须闭合的块合同。
    public static byte[] CanonicalDungeon(string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve) =>
        ScalarCanonical("DCG2"u8, path, tokens, resolve, DungeonTags.Concat(Tags).ToHashSet(StringComparer.Ordinal), false);

    public static byte[] ComputeDungeon(string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve) =>
        SHA256.HashData(CanonicalDungeon(path,tokens,resolve));

    public static byte[] CanonicalTile(string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve) =>
        ScalarCanonical("DCT1"u8, path, tokens, resolve, TileTags, true);

    public static byte[] ComputeTile(string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve) =>
        SHA256.HashData(CanonicalTile(path,tokens,resolve));

    private static byte[] ScalarCanonical(ReadOnlySpan<byte> magic, string path, IReadOnlyList<PvfToken> tokens,
        Func<int, string> resolve, IReadOnlySet<string> tags, bool tile)
    {
        string Label(int at) => tokens[at].Type == 3 ? resolve(tokens[at].Value).ToLowerInvariant() : "";
        var fields = new List<(string Tag, int Occurrence, int Begin, int End)>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < tokens.Count; ++i)
        {
            string tag = Label(i);
            bool closed = !tile && (Tags.Contains(tag) || tags.Contains(tag) && AnchorTags.Contains(tag));
            if (!tags.Contains(tag) && !closed) continue;
            int end = i + 1;
            if (closed)
            {
                string closing = "[/" + tag[1..];
                while (end < tokens.Count && Label(end) != closing)
                {
                    if (Tags.Contains(Label(end))) throw new InvalidDataException("World geometry block missing closure");
                    ++end;
                }
                if (end == tokens.Count) throw new InvalidDataException("World geometry block missing closure");
            }
            else while (end < tokens.Count && tokens[end].Type != 3) ++end;
            if (tile && (end == i + 1 || (end - i - 1) % 14 != 0 ||
                Enumerable.Range(i + 1,end - i - 1).Any(at => tokens[at].Type != 0 || tokens[at].Value is < 0 or > 255)))
                throw new InvalidDataException("Tile pass grid shape/range");
            int occurrence = occurrences.GetValueOrDefault(tag);
            occurrences[tag] = occurrence + 1;
            fields.Add((tag,occurrence,i + 1,end));
            i = end - 1;
        }
        if (tile && fields.Count != 1) throw new InvalidDataException("Tile pass grid missing or duplicated");
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, new UTF8Encoding(false,true), true);
        void Text(string text) { byte[] utf8 = Encoding.UTF8.GetBytes(text); writer.Write(utf8.Length); writer.Write(utf8); }
        writer.Write(magic); Text(path); writer.Write(fields.Count);
        foreach (var field in fields.OrderBy(f => f.Tag,StringComparer.Ordinal).ThenBy(f => f.Occurrence))
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
}
