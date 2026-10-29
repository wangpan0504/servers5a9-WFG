using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Skills;
using System.Text.RegularExpressions;
using PvfLib;
using static PvfLib.PvfScript;

namespace DfoServer.Development;
// Explicit offline Lv95 Ghostblade preparation, separate from ordinary character creation.
internal sealed record CharacterPreparation(
    int[] MainQuests,
    int[] MapQuests,
    int[] JobQuests,
    int[] CompletedQuests,
    CharacterSkill[] Skills,
    int[] ObsoleteMapReferences,
    string[] MissingMaps)
{
    public CharacterLearningState InitialLearning { get; init; } = CharacterLearningState.Uninitialized;

    public NewCharacter ToCharacter(string name, TownCatalog towns)
    {
        Validate();
        return new(name, towns.Room, towns.InitialReturn)
        {
            Job = 0,
            GrowType = 0x25,
            Level = 95,
            AcceptedStoryLevel = 95,
            DungeonState = CharacterDungeonState.NewOrdinary,
            InitialLearning = InitialLearning,
            CompletedQuests = [.. CompletedQuests],
            Skills = [.. Skills]
        };
    }

    public void Validate()
    {
        InitialLearning.Validate();
        if (MainQuests.Length == 0 || JobQuests.Length == 0 ||
            CompletedQuests.Any(q => q is <= 0 or >= 30000) ||
            !CompletedQuests.SequenceEqual(CompletedQuests.Distinct().Order()) ||
            !MainQuests.Concat(MapQuests).Concat(JobQuests).All(CompletedQuests.Contains) ||
            Skills.Length is 0 or > 255 || Skills.Any(s => s.Level == 0) ||
            Skills.Select(s => s.Id).Distinct().Count() != Skills.Length || !InitialLearning.Initialized ||
            InitialLearning.Trees.Any(tree => !tree.Skills.Select(s => new CharacterSkill(s.Id, s.Level)).OrderBy(s => s.Id)
                .SequenceEqual(Skills.OrderBy(s => s.Id))))
            throw new InvalidDataException("Invalid Client Ghostblade preparation");
    }

    public static CharacterPreparation Load(PvfArchive pvf)
    {
        string chr = pvf.ReadText("character/" + List(pvf.ReadText("character/character.lst"))[0]);
        string[] names = Regex.Matches(Section(chr, "growtype name"), "`([^`]+)`").Select(m => m.Groups[1].Value).ToArray();
        if (names.Length != 6 || names[5] != "剑影")
            throw new InvalidDataException("Ghostblade job identity changed");
        string initial = chr[chr.IndexOf("[initial value]", StringComparison.Ordinal)..chr.IndexOf("[growtype 1]", StringComparison.Ordinal)];
        string growth = Regex.Match(chr, @"\[growtype 6\](.*?)(?=\n\[waiting motion\])", RegexOptions.Singleline).Groups[1].Value;
        string[] awakening = Regex.Matches(Section(growth, "awakening name"), "`([^`]+)`").Select(m => m.Groups[1].Value).ToArray();
        if (!awakening.SequenceEqual(new[] { "夜刀神", "夜见罗刹" }))
            throw new InvalidDataException("Ghostblade awakening identity changed");
        var skills = new Dictionary<ushort, byte>();
        AddSkills(Block(initial, "skill"));
        AddSkills(Block(growth, "skill"));
        foreach (Match m in Regex.Matches(growth, @"\[awakening skill\](.*?)\[/awakening skill\]", RegexOptions.Singleline))
            AddSkills(m.Groups[1].Value);
        var scripts = List(pvf.ReadText("n_quest/quest.lst")).ToDictionary(p => p.Key, p => pvf.ReadText("n_quest/" + p.Value));
        int[] main = scripts.Where(p => Section(p.Value, "grade").Trim() == "`[epic]`").Select(p => p.Key).Order().ToArray();
        int[] jobs = scripts.Where(p =>
        {
            string name = Section(p.Value, "name").Trim('`', ' ', '\r', '\n');
            return name == "转职 - " + names[5] || awakening.Any(a => name.StartsWith("觉醒 - " + a, StringComparison.Ordinal));
        }).Select(p => p.Key).Order().ToArray();
        var references = new HashSet<int>();
        var readMaps = new HashSet<string>();
        var missingMaps = new List<string>();
        foreach (string path in List(pvf.ReadText("town/town.lst")).Values)
        {
            string text = pvf.ReadText("town/" + path);
            Collect(text);
            foreach (Match m in Regex.Matches(text, @"\[area\]\s*\d+\s+`([^`]+)`"))
                ReadMap("map/" + m.Groups[1].Value);
            foreach (Match phase in Regex.Matches(text, @"\[phase\](.*?)\[/phase\]", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(phase.Groups[1].Value, "`([^`]+)`"))
                    ReadMap("map/" + m.Groups[1].Value);
        }

        Collect(pvf.ReadText("etc/dock/docksysteminfo.etc"));
        int[] mapQuests = references.Where(scripts.ContainsKey).Order().ToArray();
        var complete = main.Concat(mapQuests).Concat(jobs).Concat(InfernoQuestUnlock.PreparationQuestIds).ToHashSet();
        // Unlock tasks can be side/episode quests; include their actual prerequisite chain.
        // 深渊派对与时空裂缝外传不在史诗/地图闭包内，离线剑影需显式纳入后才有深渊页与裂缝气球。
        var pending = new Queue<int>(complete);
        while (pending.TryDequeue(out int id))
            foreach (int prerequisite in Integers(Section(scripts[id], "pre required quest")))
                if (prerequisite > 0 && scripts.ContainsKey(prerequisite) && complete.Add(prerequisite))
                    pending.Enqueue(prerequisite);
        // 离线角色没有达人契约；按同版资源建立双树、固定等级技能与实际点数来源。
        var learningCatalog = SkillLearningCatalog.Load(pvf, expertContractLevels: 0);
        var growthCatalog = CharacterGrowthCatalog.Load(pvf, learningCatalog);
        var initialization = SkillLearning.Initialize(0, skills.OrderBy(s => s.Key)
            .Select(s => new CharacterSkill(s.Key, s.Value)).ToArray(), learningCatalog);
        if (!initialization.Success)
            throw new InvalidDataException("实验角色技能树初始化失败: " + initialization.Failure);
        var learning = SkillPointLedger.Synchronize(0, 0x25, 95,
            initialization.State! with { Experience = growthCatalog.GetLevelStartExperience(95) }, learningCatalog);
        var result = new CharacterPreparation(
            main,
            mapQuests,
            jobs,
            complete.Order().ToArray(),
            learning.Trees[0].Skills.OrderBy(s => s.Id).Select(s => new CharacterSkill(s.Id, s.Level)).ToArray(),
            references.Where(q => !scripts.ContainsKey(q)).Order().ToArray(),
            missingMaps.ToArray()) { InitialLearning = learning };
        result.Validate();
        return result;
        void AddSkills(string text)
        {
            int[] values = Integers(text);
            if (values.Length % 2 != 0)
                throw new InvalidDataException("Invalid starter skill pairs");
            for (int i = 0; i < values.Length; i += 2)
            {
                ushort id = checked((ushort)values[i]);
                byte level = checked((byte)values[i + 1]);
                skills[id] = Math.Max(skills.GetValueOrDefault(id), level);
            }
        }

        void Collect(string text)
        {
            foreach (string pattern in new[]
            {
                @"\[need quest(?: or)?\](.*?)\[/need quest(?: or)?\]",
                @"\[quest condition\](.*?)\[/quest condition\]",
                @"\[open questindex condition\]\s*(\d+)",
                @"`\[completed quest\]`\s*(\d+)"
            })
                foreach (Match m in Regex.Matches(text, pattern, RegexOptions.Singleline))
                    foreach (int id in Integers(m.Groups[1].Value))
                        if (id > 0)
                            references.Add(id);
        }

        void ReadMap(string path)
        {
            path = PvfArchive.NormalizePath(path);
            if (!readMaps.Add(path))
                return;
            if (!pvf.Contains(path))
            {
                missingMaps.Add(path);
                return;
            }

            string text = pvf.ReadText(path);
            Collect(text);
            foreach (Match m in Regex.Matches(Section(text, "import script"), "`([^`]+)`"))
            {
                string child = PvfArchive.NormalizePath(path[..(path.LastIndexOf('/') + 1)] + m.Groups[1].Value);
                if (!pvf.Contains(child))
                    child = PvfArchive.NormalizePath("map/" + m.Groups[1].Value);
                if (!pvf.Contains(child))
                {
                    int slash = child.LastIndexOf('/');
                    child = child[..(slash + 1)] + "(r)" + child[(slash + 1)..];
                }

                ReadMap(child);
            }
        }
    }
}
