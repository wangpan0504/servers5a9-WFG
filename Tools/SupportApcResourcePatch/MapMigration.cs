using System.Security.Cryptography;
using PvfLib;

internal sealed record MapRow(int Ordinal, int[] Values, string Faction, string Rank);
internal sealed record ProvenMapRow(string MapPath, int OldOrdinal, int[] OldValues, string Faction, string Rank,
    int NewOrdinal, int[] NewValues, string BeforeRawSha256, string AfterRawSha256, string CurrentRawSha256,
    string Evidence);
internal sealed record UnknownMapRow(string MapPath, MapRow Row, string Reason);
internal sealed record MigrationAudit(string Schema, string BeforePvf, string AfterPvf, string CurrentPvf,
    ProvenMapRow[] Rows, UnknownMapRow[] Unknown, string[] RejectedHistoricalMaps);

internal static class MapMigration
{
    internal static List<MapRow> Rows(PvfArchive archive, string path)
    {
        var tokens = archive.ReadTokens(path);
        var result = new List<MapRow>();
        int sections = 0;
        for (int at = 0; at < tokens.Length; at++)
        {
            if (tokens[at].Type != 3 || archive.ResolveString(tokens[at].Value) != "[ai character]") continue;
            if (++sections != 1) throw new InvalidDataException("Duplicate MAP actor section: " + path);
            int start = ++at;
            while (at < tokens.Length && tokens[at].Type != 3) at++;
            if (at >= tokens.Length || archive.ResolveString(tokens[at].Value) != "[/ai character]")
                throw new InvalidDataException("Unclosed MAP actor section: " + path);
            var rows = PvfAiCharacterScript.MapRows(tokens[start..at], archive.ResolveString);
            for (int ordinal = 0; ordinal < rows.Length; ordinal++)
                result.Add(new(ordinal, rows[ordinal].Values.ToArray(), rows[ordinal].Faction, rows[ordinal].Rank));
        }
        return result;
    }

    internal static MigrationAudit Audit(PvfArchive before, PvfArchive after, PvfArchive current,
        string beforePath, string afterPath, string currentPath)
    {
        var proven = new List<ProvenMapRow>();
        var unknown = new List<UnknownMapRow>();
        var rejected = new List<string>();
        var historical = new Dictionary<string, MapRow>(StringComparer.Ordinal);
        foreach (var file in after.Files.Where(f => IsMap(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (!before.Contains(file.Path)) continue;
            try
            {
                var old = Rows(before, file.Path);
                var added = Rows(after, file.Path);
                // 写入器有两条明确分支：已有王富贵只追加光兵；否则依次追加王富贵及光兵。
                bool hadWang = old.Any(r => r.Values[0] == 20900);
                if (old.Any(r => r.Values[0] == 5409) || added.Count != old.Count + (hadWang ? 1 : 2)) continue;
                var delta = added.Where(r => r.Values[0] == 5409).ToArray();
                var wang = added.Where(r => r.Values[0] == 20900).ToArray();
                if (delta.Length != 1 || wang.Length != 1 || delta[0].Ordinal != added.Count - 1 ||
                    (!hadWang && wang[0].Ordinal != added.Count - 2) ||
                    !ShapeEqual(delta[0], wang[0]) ||
                    !old.Select((row, index) => Same(row, added[index])).All(same => same))
                {
                    rejected.Add(file.Path);
                    continue;
                }
                historical.Add(file.Path, delta[0]);
            }
            catch (InvalidDataException) { rejected.Add(file.Path); }
        }
        foreach (var file in current.Files.Where(f => IsMap(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            List<MapRow> rows;
            try { rows = Rows(current, file.Path); }
            catch (InvalidDataException) { continue; }
            foreach (var row in rows.Where(r => r.Values[0] == 5409))
            {
                if (!historical.TryGetValue(file.Path, out var source) || !Same(source, row) ||
                    rows.Count(r => r.Values[0] == 5409) != 1)
                {
                    unknown.Add(new(file.Path, row, "No exact historical insertion proof at current ordinal and six fields"));
                    continue;
                }
                var replacement = row.Values.ToArray();
                replacement[0] = 9986;
                proven.Add(new(file.Path, row.Ordinal, row.Values, row.Faction, row.Rank,
                    row.Ordinal, replacement, Hash(before.ReadRaw(file.Path)), Hash(after.ReadRaw(file.Path)),
                    Hash(current.ReadRaw(file.Path)),
                    "historical before has no 5409; after preserves every original actor row and appends 5409 cloned from unique 20900 (also appended immediately before 5409 if absent); current ordinal, six fields, faction and rank match"));
            }
        }
        return new("support-apc-map-migration-v1", Path.GetFullPath(beforePath), Path.GetFullPath(afterPath),
            Path.GetFullPath(currentPath), proven.ToArray(), unknown.ToArray(), rejected.ToArray());
    }

    static bool IsMap(string path) => path.StartsWith("map/", StringComparison.Ordinal) && path.EndsWith(".map", StringComparison.Ordinal);
    static bool Same(MapRow a, MapRow b) => a.Ordinal == b.Ordinal && a.Values.AsSpan().SequenceEqual(b.Values) && a.Faction == b.Faction && a.Rank == b.Rank;
    static bool ShapeEqual(MapRow a, MapRow b) => a.Values.AsSpan(1).SequenceEqual(b.Values.AsSpan(1)) && a.Faction == b.Faction && a.Rank == b.Rank;
    internal static string Hash(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
}
