using DfoServer.GameWorld.Characters;
using PvfLib;

namespace DfoServer;

internal static class StarterSkillTests
{
    static readonly string[] Lists =
    [
        "swordmanskill.lst", "fighterskill.lst", "gunnerskill.lst", "mageskill.lst", "priestskill.lst",
        "atgunnerskill.lst", "thiefskill.lst", "atfighterskill.lst", "atmageskill.lst", "dsswordmanskill.lst",
        "creatormageskill.lst", "atswordmanskill.lst", "knight.lst", "demoniclancerskill.lst", "atpriestskill.lst",
        "gunbladerskill.lst"
    ];
    static readonly int[][] Active =
    [
        [5, 46, 169], [5, 46, 169], [4, 47, 169], [11, 12, 169], [1, 8, 169],
        [4, 47, 169], [1, 24, 169], [5, 46, 169], [1, 26, 169], [5, 8, 46, 108, 118, 119, 120, 121, 122, 123, 169],
        [250], [1, 8, 169], [1, 2, 169], [2, 3, 169], [1, 7, 169],
        [1, 50, 169]
    ];

    public static void Run(PvfArchive pvf, CharacterCatalog characters)
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("FAIL: " + name); }
        for (byte job = 0; job < 16; job++)
        {
            var files = Index(pvf, "skill/" + Lists[job]);
            var skills = characters.Get(job).Skills;
            var active = Active[job].ToHashSet();
            Check(skills.Select(s => (int)s.Id).Order().SequenceEqual(active.Concat(skills.Select(s => (int)s.Id).Where(id => !active.Contains(id))).Distinct().Order()),
                "job " + job + " active set is a subset of its starter skills");
            foreach (var skill in skills)
            {
                string kind = TypeOf(pvf, files[skill.Id]);
                bool expectActive = active.Contains(skill.Id);
                Check(kind == (expectActive ? "[active]" : "[passive]"),
                    "job " + job + " skill " + skill.Id + " is " + (expectActive ? "active" : "passive"));
            }
        }
        Console.WriteLine($"PASS {checks} starter skill type assertions");
    }

    static Dictionary<ushort, string> Index(PvfArchive pvf, string listPath)
    {
        var tokens = pvf.ReadTokens(listPath);
        var files = new Dictionary<ushort, string>();
        for (int i = 0; i + 1 < tokens.Length; i += 2)
        {
            if (tokens[i].Type != 0 || tokens[i + 1].Type != 6) continue;
            files[checked((ushort)tokens[i].Value)] = PvfArchive.NormalizePath("skill/" + pvf.ResolveString(tokens[i + 1].Value));
        }
        return files;
    }

    static string TypeOf(PvfArchive pvf, string path)
    {
        var tokens = pvf.ReadTokens(path);
        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Type == 3 && pvf.ResolveString(tokens[i].Value) == "[type]" && tokens[i + 1].Type == 6)
                return pvf.ResolveString(tokens[i + 1].Value);
        }
        throw new InvalidDataException("技能没有 [type]: " + path);
    }
}
