using System.Text;
using System.Text.Json;
using PvfLib;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
if (args.Length < 2) throw new ArgumentException("Usage: inspect pvf path... | audit-migration before.pvf after.pvf current.pvf output.json");
if (args[0] == "inspect")
{
    using var archive = PvfArchive.Open(args[1]);
    Console.WriteLine($"ARCHIVE path={Path.GetFullPath(args[1])} files={archive.FileCount}");
    foreach (string path in args.Skip(2))
    {
        if (path == "--template-equipment")
        {
            var equipment = PvfItemIndex.Equipment(archive.ReadTokens("equipment/equipment.lst"), archive.ResolveString,
                resource => archive.FindFile(resource)?.DataType == 1);
            foreach (var definition in TemplateDelta.Definitions)
            {
                var tokens = archive.ReadTokens(definition.Source);
                int start = Array.FindIndex(tokens, token => token.Type == 3 && archive.ResolveString(token.Value) == "[equipment]") + 1;
                if (start == 0) throw new InvalidDataException("Missing source equipment");
                int end = start;
                while (end < tokens.Length && tokens[end].Type != 3) end++;
                if ((end - start) % 3 != 0) throw new InvalidDataException("Unsupported source equipment");
                for (int at = start; at < end; at += 3)
                {
                    int id = tokens[at].Value;
                    if (!equipment.TryGetValue(id, out string? equipmentPath))
                    {
                        Console.WriteLine($"UNREGISTERED_EQUIPMENT template={definition.Id} id={id}");
                        continue;
                    }
                    Console.WriteLine($"EQUIPMENT template={definition.Id} id={id} path={equipmentPath}");
                    Console.WriteLine(archive.ReadText(equipmentPath));
                }
            }
        }
        else if (path == "--buff-paths")
        {
            foreach (var file in archive.Files.Where(f => new[] { "forbiddencurse", "favoritism", "blessofcourage", "blessofprotection", "appendage" }.Any(n => f.Path.Contains(n, StringComparison.OrdinalIgnoreCase))))
                Console.WriteLine(file.Path);
        }
        else if (path == "--brooms")
        {
            foreach (var file in archive.Files.Where(f => f.Path.StartsWith("equipment/", StringComparison.Ordinal) && f.Path.Contains("/broom/", StringComparison.Ordinal) && f.Path.EndsWith(".equ", StringComparison.Ordinal)).Take(12))
                Console.WriteLine(file.Path);
        }
        else if (path == "--ids")
        {
            var tokens = archive.ReadTokens("aicharacter/aicharacter.lst");
            int[] wanted = [9986,74990,19973,20910,20911,20912];
            for (int at = 0; at < tokens.Length; at += 2)
                if (wanted.Contains(tokens[at].Value))
                    Console.WriteLine($"OCCUPIED id={tokens[at].Value} path={archive.ResolveString(tokens[at + 1].Value)}");
            Console.WriteLine("ID_CHECK " + string.Join(',', wanted));
        }
        else if (path == "--maps")
        {
            int old = 0, dedicated = 0, count = 0, malformed = 0;
            var counts = new Dictionary<int, int>();
            foreach (var map in archive.Files.Where(f => f.Path.StartsWith("map/", StringComparison.Ordinal) && f.Path.EndsWith(".map", StringComparison.Ordinal)))
            {
                try
                {
                    var rows = MapMigration.Rows(archive, map.Path);
                    old += rows.Count(r => r.Values[0] == 5409);
                    dedicated += rows.Count(r => r.Values[0] == 20900);
                    foreach (var row in rows) counts[row.Values[0]] = counts.GetValueOrDefault(row.Values[0]) + 1;
                    if (rows.Any(r => r.Values[0] == 5409) && old <= 3)
                        Console.WriteLine("SAMPLE " + map.Path + " " + JsonSerializer.Serialize(rows));
                    count++;
                }
                catch (InvalidDataException) { malformed++; }
            }
            Console.WriteLine($"MAPS count={count} malformed={malformed} old5409={old} wang20900={dedicated}");
            Console.WriteLine("TOPIDS " + JsonSerializer.Serialize(counts.OrderByDescending(p => p.Value).Take(12)));
        }
        else
        {
            Console.WriteLine("PATH " + path);
            Console.WriteLine(archive.Contains(path) ? archive.ReadText(path) : "MISSING");
        }
    }
}
else if (args[0] == "audit-migration" && args.Length == 5)
{
    using var before = PvfArchive.Open(args[1]);
    using var after = PvfArchive.Open(args[2]);
    using var current = PvfArchive.Open(args[3]);
    var report = MapMigration.Audit(before, after, current, args[1], args[2], args[3]);
    string target = Path.GetFullPath(args[4]);
    if (File.Exists(target)) throw new IOException("Output already exists: " + target);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.WriteAllText(target, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"AUDIT proven={report.Rows.Length} unknown={report.Unknown.Length} output={target}; source PVFs read-only");
}
else if (args[0] is "preview-maps" or "generate-maps" && args.Length == 4)
{
    using var source = PvfArchive.Open(args[1]);
    var audit = JsonSerializer.Deserialize<MigrationAudit>(File.ReadAllBytes(args[2]))
        ?? throw new InvalidDataException("Missing migration audit");
    var patches = MapDelta.Prepare(source, audit);
    if (args[0] == "generate-maps") MapDelta.Write(patches, audit.Unknown, args[3]);
    Console.WriteLine($"MAP_DELTA resources={patches.Count} changed={patches.Count(p => p.Changed)} unknownPreserved={audit.Unknown.Length} preview={args[0] == "preview-maps"}; source PVF read-only");
}
else if (args[0] == "audit-profile-delta" && args.Length == 5)
{
    ProfileDeltaAudit.Run(args[1], args[2], args[3], args[4]);
}
else if (args[0] == "prepare-female-beast-maps" && args.Length == 4)
{
    using var source = PvfArchive.Open(args[1]);
    FemaleRestoreDelta.WriteBeastMaps(source, args[2], args[3]);
}
else if (args[0] == "prepare-female-restore" && args.Length == 4)
{
    using var source = PvfArchive.Open(args[1]);
    FemaleRestoreDelta.Write(source, args[1], args[2], args[3]);
}
else if (args[0] == "export-support-map-eligibility" && args.Length == 4)
{
    SupportMapEligibilityExport.Write(args[1], args[2], args[3]);
}
else if (args[0] == "prepare-paired-companion-maps" && args.Length == 4)
{
    using var source = PvfArchive.Open(args[1]);
    PairedCompanionMapDelta.Write(source, args[1], args[2], args[3]);
}
else if (args[0] == "prepare-support-effective-tiers" && args.Length == 4)
{
    using var source = PvfArchive.Open(args[1]);
    SupportEffectiveTierDelta.Write(source, args[2], args[3]);
}
else if (args[0] == "prepare-support-natural-tiers" && args.Length == 4)
{
    using var source = PvfArchive.Open(args[1]);
    SupportNaturalTierDelta.Write(source, args[2], args[3]);
}
else if (args[0] == "prepare-support-auxiliary" && args.Length == 3)
{
    using var source = PvfArchive.Open(args[1]);
    SupportAuxiliaryDelta.Write(source, args[2]);
}
else if (args[0] == "prepare-support-behavior" && args.Length == 3)
{
    using var source = PvfArchive.Open(args[1]);
    SupportBehaviorDelta.Write(source, args[2]);
}
else if (args[0] == "prepare-combat" && args.Length == 3)
{
    using var source = PvfArchive.Open(args[1]);
    CombatDelta.Write(source, args[2]);
}
else if (args[0] == "prepare-overdrive" && args.Length == 4)
{
    using var source = PvfArchive.Open(args[1]);
    OverdriveDelta.Write(source, args[2], args[3]);
}
else if (args[0] == "prepare-templates" && args.Length == 3)
{
    using var source = PvfArchive.Open(args[1]);
    TemplateDelta.WritePlan(source, args[2]);
}
else if (args[0] == "pair-resources" && args.Length == 5)
{
    PairManifest.Write(args[1], args[2], args[3], args[4]);
}
else if (args[0] == "export-evidence" && args.Length == 4)
{
    using var source = PvfArchive.Open(args[1]);
    ResourceEvidence.Write(source, args[2], args[3]);
}
else throw new ArgumentException("Unknown command or invalid argument count");
