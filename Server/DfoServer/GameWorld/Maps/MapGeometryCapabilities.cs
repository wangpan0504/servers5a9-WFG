using System.Collections.ObjectModel;
using System.Text;
using PvfLib;

namespace DfoServer.GameWorld.Maps;

// 启动时固定受管副本的完整碰撞/导入闭包；HTTP只读取结果，不在请求内访问PVF。
internal static class MapGeometryCapabilities
{
    internal const int MaxNodes = 32768;

    public static IReadOnlyDictionary<string, byte[]> Build(PvfArchive pvf, MapGeometryConfig config, IEnumerable<string> townPaths) =>
        BuildCore(config, townPaths, path => pvf.Contains(path) || config.Aliases.ContainsKey(path),
            path => config.ReadTokens(pvf, path), id => config.ResolveString(id, pvf.ResolveString));

    // 同一装配算法可使用只读资源夹具，测试真实依赖图而不创建伪PVF归档。
    internal static IReadOnlyDictionary<string, byte[]> BuildCore(MapGeometryConfig config, IEnumerable<string> townPaths,
        Func<string, bool> contains, Func<string, PvfToken[]> read, Func<int, string> resolve)
    {
        var signatures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var tokens = new Dictionary<string, PvfToken[]>(StringComparer.Ordinal);
        PvfToken[] Read(string path)
        {
            if (!contains(path)) throw new InvalidDataException("Map geometry dependency missing: " + path);
            if (!tokens.TryGetValue(path, out var value)) tokens.Add(path, value = read(path));
            return value;
        }
        void Add(string path, string kind)
        {
            if (kinds.TryGetValue(path, out string? known))
            {
                if (known != kind) throw new InvalidDataException("Conflicting map geometry signature kind: " + path);
                return;
            }
            var data = Read(path);
            byte[] signature = kind switch
            {
                "DCG2" => MapGeometrySignature.ComputeDungeon(path, data, resolve),
                "DCT1" => MapGeometrySignature.ComputeTile(path, data, resolve),
                _ => MapGeometrySignature.Compute(path, data, resolve)
            };
            kinds.Add(path, kind); signatures.Add(path, signature);
        }
        foreach (string raw in townPaths.Concat(config.Paths).Distinct(StringComparer.Ordinal))
        {
            string path = Normalize(raw);
            Add(path, config.SignatureKind(path));
        }

        foreach (string root in config.Paths.Where(path => config.SignatureKind(path) == "DCG2"))
        {
            var states = new Dictionary<string, byte>(StringComparer.Ordinal);
            var pending = new Stack<(string Path, bool Exit, int Depth)>();
            pending.Push((root, false, 0));
            while (pending.TryPop(out var node))
            {
                if (node.Exit) { states[node.Path] = 2; continue; }
                byte state = states.GetValueOrDefault(node.Path);
                if (state == 1) throw new InvalidDataException("Recursive map geometry dependency: " + node.Path);
                if (state == 2) continue;
                if (states.Count >= MaxNodes || node.Depth > 512)
                    throw new InvalidDataException("Map geometry dependency graph budget");
                bool tile = node.Path.EndsWith(".til", StringComparison.Ordinal);
                if (!tile && !node.Path.EndsWith(".map", StringComparison.Ordinal))
                    throw new InvalidDataException("Unsupported map geometry dependency: " + node.Path);
                Add(node.Path, tile ? "DCT1" : "DCG2");
                states.Add(node.Path, 1);
                pending.Push((node.Path, true, node.Depth));
                if (tile) continue;
                var children = Dependencies(node.Path, Read(node.Path), resolve, contains).Distinct(StringComparer.Ordinal).ToArray();
                for (int i = children.Length - 1; i >= 0; --i) pending.Push((children[i], false, node.Depth + 1));
            }
        }
        return new ReadOnlyDictionary<string, byte[]>(signatures);
    }

    private static IEnumerable<string> Dependencies(string owner, PvfToken[] tokens, Func<int, string> resolve, Func<string, bool> contains)
    {
        for (int i = 0; i < tokens.Length; ++i)
        {
            if (tokens[i].Type != 3) continue;
            string tag = resolve(tokens[i].Value).ToLowerInvariant();
            if (tag is not ("[tile]" or "[tile files]" or "[import script]")) continue;
            int end = i + 1;
            while (end < tokens.Length && tokens[end].Type != 3) ++end;
            if (tag == "[tile files]")
            {
                if ((end - i - 1) % 2 != 0) throw new InvalidDataException("Map geometry tile definitions shape: " + owner);
                for (int at = i + 1; at < end; at += 2)
                {
                    if (tokens[at].Type != 0 || tokens[at].Value < 0 || tokens[at + 1].Type != 6)
                        throw new InvalidDataException("Map geometry tile definition identity: " + owner);
                    yield return TileReference(owner, resolve(tokens[at + 1].Value));
                }
            }
            else for (int at = i + 1; at < end; ++at)
            {
                if (tokens[at].Type != 6) throw new InvalidDataException("Map geometry dependency reference kind: " + owner);
                string name = resolve(tokens[at].Value);
                yield return tag == "[tile]" ? TileReference(owner, name) : ImportReference(owner, name, contains);
            }
            i = end - 1;
        }
    }

    private static string Reference(string owner, string name)
    {
        name = name.Replace('\\', '/').ToLowerInvariant();
        if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Any(char.IsControl))
            throw new InvalidDataException("Map geometry dependency reference");
        return Normalize(owner[..(owner.LastIndexOf('/') + 1)] + name);
    }

    private static string TileReference(string owner, string name)
    {
        string path = Reference(owner, name);
        if (!path.EndsWith(".til", StringComparison.Ordinal)) throw new InvalidDataException("Map geometry tile reference kind");
        return path;
    }

    private static string ImportReference(string owner, string name, Func<string, bool> contains)
    {
        string path = Reference(owner, name);
        if (!path.EndsWith(".map", StringComparison.Ordinal)) throw new InvalidDataException("Map geometry import reference kind");
        if (contains(path)) return path;
        path = Reference("map/root.map", name);
        if (contains(path)) return path;
        int slash = path.LastIndexOf('/');
        path = path[..(slash + 1)] + "(r)" + path[(slash + 1)..];
        if (!contains(path)) throw new InvalidDataException("Map geometry import missing: " + owner + " -> " + name);
        return path;
    }

    private static string Normalize(string path)
    {
        path = path.Replace('\\', '/').ToLowerInvariant();
        if (path.Length == 0 || path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl))
            throw new InvalidDataException("Map geometry resource path");
        var parts = new List<string>();
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count <= 1) throw new InvalidDataException("Map geometry dependency escapes map root");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        string result = string.Join('/', parts);
        if (!result.StartsWith("map/", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(result) > 480)
            throw new InvalidDataException("Map geometry resource path");
        return result;
    }
}
