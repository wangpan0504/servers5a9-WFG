using System.Buffers.Binary;
using System.Text.Json;
using PvfLib;

internal sealed record RawDelta(string Path, byte[] Before, byte[] After)
{
    internal bool Changed => !Before.AsSpan().SequenceEqual(After);
}

internal static class MapDelta
{
    internal static List<RawDelta> Prepare(PvfArchive source, MigrationAudit audit)
    {
        if (audit.Schema != "support-apc-map-migration-v1") throw new InvalidDataException("Unsupported migration schema");
        if (audit.Rows.Select(r => r.MapPath).Distinct(StringComparer.Ordinal).Count() != audit.Rows.Length ||
            audit.Rows.Any(r => audit.Unknown.Any(u => u.MapPath == r.MapPath)))
            throw new InvalidDataException("Duplicate proven MAP or proven/unknown overlap");
        var result = new List<RawDelta>();
        foreach (var proven in audit.Rows)
        {
            if (proven.OldOrdinal < 0 || proven.NewOrdinal != proven.OldOrdinal ||
                proven.OldValues.Length != 6 || proven.NewValues.Length != 6 ||
                proven.OldValues[0] != 5409 || proven.NewValues[0] != 9986 ||
                !proven.OldValues.AsSpan(1).SequenceEqual(proven.NewValues.AsSpan(1)) ||
                proven.Faction != "[character]" || proven.Rank != "[normal]")
                throw new InvalidDataException("Invalid or obsolete migration tuple: " + proven.MapPath);
            var rows = MapMigration.Rows(source, proven.MapPath);
            var raw = source.ReadRaw(proven.MapPath);
            var tokens = source.ReadTokens(proven.MapPath);
            int start = Array.FindIndex(tokens, t => t.Type == 3 && source.ResolveString(t.Value) == "[ai character]") + 1;
            int end = start;
            while (end < tokens.Length && tokens[end].Type != 3) end++;
            if (start == 0 || end >= tokens.Length || source.ResolveString(tokens[end].Value) != "[/ai character]")
                throw new InvalidDataException("Invalid actor bounds: " + proven.MapPath);
            var oldRows = rows.Where(r => r.Values[0] == 5409).ToArray();
            var newRows = rows.Where(r => r.Values[0] == 9986).ToArray();
            bool patched = oldRows.Length == 0 && newRows.Length == 1;
            var selected = patched ? newRows : oldRows;
            int[] expected = patched ? proven.NewValues : proven.OldValues;
            if (selected.Length != 1 || !Matches(selected[0], proven.OldOrdinal, expected, proven) ||
                (!patched && newRows.Length != 0))
                throw new InvalidDataException("Legacy actor tuple or dedicated companion changed: " + proven.MapPath);
            byte[] desired;
            if (patched)
            {
                // 幂等回读必须重建原输入哈希，不能只见到新编号就信任其他改动。
                if (rows.Count < 3 || rows[^2].Values[0] != 74990 || rows[^1].Values[0] != 19973 ||
                    rows.Count(r => r.Values[0] == 74990) != 1 || rows.Count(r => r.Values[0] == 19973) != 1 ||
                    !CloneShape(rows[^2], proven) || !CloneShape(rows[^1], proven))
                    throw new InvalidDataException("Incomplete or conflicting dedicated MAP rows: " + proven.MapPath);
                byte[] normalized = new byte[raw.Length - 80];
                int removal = (end - 16) * 5;
                raw.AsSpan(0, removal).CopyTo(normalized);
                raw.AsSpan(end * 5).CopyTo(normalized.AsSpan(removal));
                BinaryPrimitives.WriteInt32LittleEndian(normalized.AsSpan((start + proven.OldOrdinal * 8) * 5 + 1), 5409);
                if (MapMigration.Hash(normalized) != proven.CurrentRawSha256)
                    throw new InvalidDataException("Unrelated MAP data changed: " + proven.MapPath);
                desired = raw;
            }
            else
            {
                if (MapMigration.Hash(raw) != proven.CurrentRawSha256 || rows.Any(r => r.Values[0] is 74990 or 19973))
                    throw new InvalidDataException("Audited MAP changed or dedicated IDs already present: " + proven.MapPath);
                int rowOffset = (start + proven.OldOrdinal * 8) * 5;
                desired = new byte[raw.Length + 80];
                raw.AsSpan(0, end * 5).CopyTo(desired);
                BinaryPrimitives.WriteInt32LittleEndian(desired.AsSpan(rowOffset + 1), 9986);
                for (int n = 0; n < 2; n++)
                {
                    raw.AsSpan(rowOffset, 40).CopyTo(desired.AsSpan(end * 5 + n * 40));
                    BinaryPrimitives.WriteInt32LittleEndian(desired.AsSpan(end * 5 + n * 40 + 1), n == 0 ? 74990 : 19973);
                }
                raw.AsSpan(end * 5).CopyTo(desired.AsSpan(end * 5 + 80));
            }
            result.Add(new(proven.MapPath, raw, desired));
        }
        return result;
    }

    static bool Matches(MapRow row, int ordinal, int[] values, ProvenMapRow proven) =>
        row.Ordinal == ordinal && row.Values.AsSpan().SequenceEqual(values) && row.Faction == proven.Faction && row.Rank == proven.Rank;
    static bool CloneShape(MapRow row, ProvenMapRow proven) =>
        row.Values.AsSpan(1).SequenceEqual(proven.OldValues.AsSpan(1)) && row.Faction == proven.Faction && row.Rank == proven.Rank;

    internal static void Write(List<RawDelta> patches, UnknownMapRow[] unknown, string outputPath)
    {
        string output = Path.GetFullPath(outputPath);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Output must not exist: " + output);
        string staging = output + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            var replacements = new List<object>();
            int ordinal = 0;
            foreach (var patch in patches.Where(p => p.Changed))
            {
                string name = (ordinal++).ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".raw";
                File.WriteAllBytes(Path.Combine(staging, name), patch.After);
                replacements.Add(new { path = patch.Path, raw_file = Path.Combine(output, name), data_type = 1,
                    expected_before_sha256 = MapMigration.Hash(patch.Before) });
            }
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(new
            {
                schema = "support-apc-map-delta-v1", replacements,
                unknown_preserved = unknown,
                requires_paired_aic_ai_key_and_client = true,
                release_ready = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Move(staging, output);
        }
        catch
        {
            // 失败保留隔离暂存供排查，不改写输入或既有候选。
            throw;
        }
    }
}
