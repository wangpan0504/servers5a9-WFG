using System.Text.Json;
using System.Text.Json.Nodes;

internal static class PairManifest
{
    internal static readonly string[] RequiredCapabilities =
    [
        "recipient-selection", "effective-buff-state", "maintained-actions",
        "enchantress-puppet-availability", "revival-native-limits", "favoritism-stable-recipient",
        "player-escort-and-offense-qualification"
    ];

    // 配套合同必须逐能力引用真实客户端文件；不能只翻转 release_ready 或清空 pending。
    internal static void Write(string mapPath, string templatePath, string clientPath, string outputPath)
    {
        var maps = Read(mapPath, "support-apc-map-delta-v1");
        var templates = Read(templatePath, "support-apc-resource-delta-v1");
        var client = Read(clientPath, "support-apc-client-contract-v1");
        if (maps["release_ready"]?.GetValue<bool>() != false ||
            templates["release_ready"]?.GetValue<bool>() != false ||
            maps["requires_paired_aic_ai_key_and_client"]?.GetValue<bool>() != true ||
            templates["requires_paired_aic_ai_key_and_client"]?.GetValue<bool>() != true)
            throw new InvalidDataException("Expected unpaired generator outputs");

        var files = Array(client, "files");
        if (files.Count == 0) throw new InvalidDataException("Missing client evidence files");
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files)
        {
            string path = Text(item, "path"), expected = Text(item, "sha256");
            if (!Path.IsPathFullyQualified(path) || !filePaths.Add(Path.GetFullPath(path)) ||
                !File.Exists(path) || !string.Equals(MapMigration.Hash(File.ReadAllBytes(path)), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Client evidence file missing, duplicate or changed: " + path);
        }
        foreach (string name in new[] { "SupportBuffAi.h", "SupportBuffAiExpected.h", "SupportBuffQuery.h", "SupportApcIdentity.h", "SupportCompanion.cpp", "SupportCompanion.dll" })
            if (filePaths.Count(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidDataException("Missing or duplicate client source/binary pairing: " + name);
        var capabilities = Array(client, "capabilities");
        foreach (string required in RequiredCapabilities)
        {
            var matches = capabilities.Where(item => Text(item, "id") == required).ToArray();
            if (matches.Length != 1 || matches[0]?["implemented"]?.GetValue<bool>() != true ||
                string.IsNullOrWhiteSpace(Text(matches[0], "evidence")))
                throw new InvalidDataException("Client capability is not closed: " + required);
            var references = Array(matches[0]!.AsObject(), "files");
            if (references.Count == 0 || references.Any(item => item is null || !filePaths.Contains(Path.GetFullPath(item.GetValue<string>()))))
                throw new InvalidDataException("Client capability lacks paired evidence: " + required);
        }

        var entries = Array(templates, "entries");
        var replacements = Array(maps, "replacements");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in entries.Concat(replacements))
        {
            string path = Text(item, "path");
            if (!paths.Add(path)) throw new InvalidDataException("Duplicate resource in paired plan: " + path);
            if (item?["raw_file"] is JsonNode raw && !File.Exists(raw.GetValue<string>()))
                throw new FileNotFoundException("Missing raw resource", raw.GetValue<string>());
        }
        if (Array(templates, "dependencies").Count != 4 || replacements.Count == 0)
            throw new InvalidDataException("Missing template or MAP dependencies");
        foreach (var definition in TemplateDelta.Definitions)
        {
            string directory = definition.Target[..definition.Target.LastIndexOf('/')];
            if (!paths.Contains(definition.Target) || definition.Active.Any(id => !paths.Contains($"{directory}/key/support_{id}.key")) ||
                new[] { "event.ai", "destinationselect.ai", "action.ai", "movemethod.ai" }.Any(leaf => !paths.Contains(directory + "/ai/" + leaf)))
                throw new InvalidDataException("Incomplete executable template resources: " + definition.Target);
        }
        templates["replacements"] = replacements.DeepClone();
        templates["unknown_preserved"] = maps["unknown_preserved"]?.DeepClone();
        templates["client_contract"] = client.DeepClone();
        templates["closed_pending"] = templates["pending"]?.DeepClone();
        templates["pending"] = new JsonArray();
        templates["release_ready"] = true;
        string output = Path.GetFullPath(outputPath);
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, templates, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"PAIRED entries={entries.Count} maps={replacements.Count} capabilities={RequiredCapabilities.Length}; candidate only, no live write");
    }

    static JsonObject Read(string path, string schema)
    {
        var document = JsonNode.Parse(File.ReadAllBytes(path))?.AsObject() ?? throw new InvalidDataException("Missing plan: " + path);
        if (Text(document, "schema") != schema) throw new InvalidDataException("Unexpected plan schema: " + path);
        return document;
    }
    static JsonArray Array(JsonObject value, string name) => value[name]?.AsArray() ?? throw new InvalidDataException("Missing array: " + name);
    static string Text(JsonNode? value, string name) => value?[name]?.GetValue<string>() ?? throw new InvalidDataException("Missing text: " + name);
}
