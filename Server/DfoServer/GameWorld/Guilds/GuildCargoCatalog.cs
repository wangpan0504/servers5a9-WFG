using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Guilds;

internal sealed class GuildCargoCatalog
{
    readonly PvfArchive pvf;
    readonly IReadOnlyDictionary<int, string> equipment;
    readonly IReadOnlyDictionary<int, string> stackable;
    readonly ConcurrentDictionary<int, bool> allowed = new();
    public IReadOnlySet<uint> StorageNpcs { get; }

    public GuildCargoCatalog(PvfArchive pvf, IReadOnlyDictionary<int, string> equipment,
        IReadOnlyDictionary<int, string> stackable)
    {
        this.pvf = pvf;
        this.equipment = equipment;
        this.stackable = stackable;
        var npcs = PvfScript.List(pvf.ReadText("npc/npc.lst"));
        if (!npcs.TryGetValue(99, out string? path))
            throw new InvalidDataException("公会仓库NPC99缺失");
        string role = PvfScript.Block(pvf.ReadText("npc/" + path), "role");
        StorageNpcs = Regex.IsMatch(role, @"^\s*`\[guild storage\]`\s+0\s*$", RegexOptions.IgnoreCase)
            ? new HashSet<uint> { 99 } : throw new InvalidDataException("公会仓库NPC99用途不匹配");
    }

    public bool Allows(int id) => allowed.GetOrAdd(id, Load);

    bool Load(int id)
    {
        bool equip = equipment.TryGetValue(id, out string? path);
        if (!equip && !stackable.TryGetValue(id, out path)) return false;
        var script = equip ? EquipmentFile.ReadScript(pvf.ReadText, "equipment/" + path) :
            new PvfDocument(pvf.ReadText("stackable/" + path));
        return AllowsScript(script, equip);
    }

    internal static bool AllowsScript(PvfDocument script, bool equipment)
    {
        if (script.Has("packagable") && (!script.TryGetInt32("packagable", out int value) || value == 0)) return false;
        if (Regex.Matches(script.Section("item category"), "`([^`]+)`").Any(match =>
                match.Groups[1].Value.Trim().Trim('[', ']').Equals("packagable", StringComparison.OrdinalIgnoreCase))) return false;
        if (equipment && script.Has("cash") && (!script.TryGetInt32("cash", out int cash) || cash > 0)) return false;
        return true;
    }

    public static bool IsHall(string path) => Towns.TownCatalog.IsGuildHallMap(path);
}
