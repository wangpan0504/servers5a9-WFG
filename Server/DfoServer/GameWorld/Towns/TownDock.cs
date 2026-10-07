using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.GameWorld.Towns;

internal sealed record DockStop(int Region, int Phase, Location Point, int Level, int Quest);
internal sealed record TownDock(int OpenQuest, DockStop[] Stops, TownPhase[] Shifts)
{
    public DockStop[] Active(IReadOnlySet<int> completed)
    {
        int phase = 0;
        foreach (var shift in Shifts)
            if (completed.Contains(shift.Quest))
                phase = shift.Index;
        return Stops.GroupBy(s => s.Region).Select(g => g.SingleOrDefault(s => s.Phase == phase) ?? g.Single(s => s.Phase == 0)).ToArray();
    }

    public bool IsRoute(Location source, Location target) => Stops.Any(s => SameArea(s.Point, source)) && Stops.Any(s => SameArea(s.Point, target));
    public string Admission(Location source, Location target, int level, IReadOnlySet<int> completed)
    {
        if (!IsRoute(source, target))
            return "route-not-in-pvf";
        var stops = Active(completed);
        var from = stops.SingleOrDefault(s => SameArea(s.Point, source));
        var to = stops.SingleOrDefault(s => SameArea(s.Point, target));
        if (from is null || to is null)
            return "dock-phase-inactive";
        if (level < from.Level || level < to.Level)
            return "dock-level-required";
        return new[]
        {
            OpenQuest,
            from.Quest,
            to.Quest
        }.All(completed.Contains) ? "ready" : "dock-quest-required";
    }

    static bool SameArea(Location a, Location b) => a.Town == b.Town && a.Area == b.Area;
    public static TownDock Load(PvfArchive pvf)
    {
        string text = pvf.ReadText("etc/dock/docksysteminfo.etc");
        int[] phases = Integers(Block(text, "phase shift"));
        var shifts = new List<TownPhase>();
        if (phases.Length % 3 != 0)
            throw new InvalidDataException("Dock phase width");
        for (int i = 0; i < phases.Length; i += 3)
        {
            // Matching Client: 1/6359/6; town17 excludes completed6359 and town70 requires it.
            if (phases[i] <= 0 || phases[i + 1] <= 0 || phases[i + 2] != 6)
                throw new InvalidDataException("Unverified dock phase condition");
            shifts.Add(new(phases[i], phases[i + 1]));
        }

        var stops = new List<DockStop>();
        foreach (Match region in Regex.Matches(text, @"\[portal region\](.*?)\[/portal region\]", RegexOptions.Singleline))
        {
            string body = region.Groups[1].Value;
            int id = (int)Number(body, "region index");
            var variants = Regex.Matches(body, @"\[phase shift dock info\]\s*(\d+)(.*?)\[/phase shift dock info\]", RegexOptions.Singleline);
            Add(Regex.Replace(body, @"\[phase shift dock info\].*?\[/phase shift dock info\]", "", RegexOptions.Singleline), 0);
            foreach (Match variant in variants)
                Add(variant.Groups[2].Value, int.Parse(variant.Groups[1].Value));
            void Add(string value, int phase)
            {
                int[] point = Integers(Section(value, "portal area"));
                if (point.Length != 4 || id <= 0 || phase != 0 && !shifts.Any(s => s.Index == phase))
                    throw new InvalidDataException("Dock stop shape");
                stops.Add(new(id, phase, new(checked((byte)point[0]), checked((byte)point[1]), checked((ushort)point[2]), checked((ushort)point[3])), (int)Number(value, "open level condition", 1), (int)Number(value, "open questindex condition")));
            }
        }

        int open = (int)Number(text, "open questindex condition");
        if (open <= 0 || stops.Count == 0 || stops.Any(s => s.Level < 1 || s.Quest <= 0) || stops.Select(s => (s.Region, s.Phase)).Distinct().Count() != stops.Count || stops.GroupBy(s => s.Region).Any(g => g.Count(s => s.Phase == 0) != 1))
            throw new InvalidDataException("Invalid dock catalog");
        return new(open, stops.ToArray(), shifts.ToArray());
    }
}
