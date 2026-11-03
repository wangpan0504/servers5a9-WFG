using DfoServer.GameWorld.Quests;
using PvfLib;
using DfoServer.Game.Quests;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing;

namespace DfoServer;

internal static class QuestCatalogTests
{
    public static void RunCore()
    {
        RunClosingTags();
        int checks = TestCheck.Count;
        string[] bookTags = ["[story book unit]", "[sequence]", "[/sequence]", "[/story book unit]"];
        PvfToken[] bookTokens = [new(3,0),new(3,1),new(0,3109),new(0,3108),new(3,2),new(3,3)];
        var bookIds = QuestCatalog.ReadStoryBookQuestIds(new(bookTokens, id => bookTags[id]));
        TestCheck.That(bookIds.SequenceEqual([3108,3109]), "故事簿章节来自资源sequence，不按play only或文件名猜测");
        TestCheck.Reject<InvalidDataException>(() => QuestCatalog.ReadStoryBookQuestIds(new(
            [new(3,0),new(3,1),new(0,30000),new(3,2),new(3,3)], id => bookTags[id])), "故事簿拒绝越界章节");
        var paths = new Dictionary<int,string> { [100] = "valid.qst", [101] = "missing.qst", [102] = "invalid.qst", [103] = "obsolete.qst" };
        var catalog = QuestCatalog.LoadDefinitions(paths, (id,path) => id switch
        {
            100 => QuestEligibilityTests.Quest(id) with { Path = path },
            101 => throw new FileNotFoundException(path), 102 => throw new InvalidDataException(path),
            _ => throw new NotSupportedException(path)
        });
        var character = new Character(1,0,"Quest",1,new Location(38,2,450,240),null);
        TestCheck.That(catalog.All.Count == 4 && QuestEligibility.Visible(character,catalog).SequenceEqual([100]) &&
            catalog.All.Where(quest => quest.Id != 100).All(quest => quest.Definition is { Diagnostics.Length: 1 } &&
                QuestEligibility.InspectResource(quest).Entry == QuestResourceEntry.Invalid),
            "旧脚本缺失或定义损坏只保留不可用原因，不阻断有效任务，不修补后取得资格");
        TestCheck.Reject<InvalidDataException>(() => QuestCatalog.LoadDefinitions(new Dictionary<int,string> { [3145] = "used.qst" },
            (_,_) => throw new InvalidDataException("used resource")), "已使用的新手资源损坏不能用空定义继续运行");
        var alternative = catalog.Get(100) with { Definition = catalog.Get(100).Definition! with
            { PrerequisiteGroups = [[100],[99999]], Collisions = [99998], HideOnCompletedQuests = [99997] } };
        TestCheck.That(QuestCoverage.HasRequiredReferences(alternative,catalog) &&
            !QuestCoverage.HasRequiredReferences(alternative with { Definition = alternative.Definition! with { PrerequisiteGroups = [[99999]] } },catalog) &&
            !QuestCoverage.HasRequiredReferences(alternative with { Definition = alternative.Definition! with { RequiredAnswers = [new(99999,0)] } },catalog),
            "废弃的OR分支和冲突引用不阻断有效资源；必需前置及回答引用缺失仍明确限制");
        TestCheck.Reject<IOException>(() => QuestCatalog.LoadDefinitions(paths, (_,_) => throw new IOException("storage failure")),
            "存储故障不误当失效旧任务吞掉");
        Console.WriteLine($"PASS {TestCheck.Count - checks} 失效旧任务资源隔离与已用资源严格校验断言");
    }

    public static void RunClosingTags()
    {
        int checks=TestCheck.Count;
        var strings=new Dictionary<int,string>{[1]="[level]",[2]="[type]",[3]="[meet npc]",
            [4]="[pre required quest]",[5]="[/pre required quest]",[6]="[int data]",[7]="[npc index]",[8]="[/level]",[9]="[pre required quest answer]"};
        var prefix=new List<PvfToken>{new(3,1),new(0,1),new(0,99),new(3,2),new(6,3),new(3,6),new(0,1),new(3,7),new(0,1)};
        var tokens=prefix.Concat(new PvfToken[]{new(3,4),new(0,100),new(3,5),new(3,5),new(3,5)}).ToArray();
        var quest=QuestDefinitionReader.Read(101,"fixture/redundant-end.qst",tokens,id=>strings[id]);
        var parent=QuestEligibilityTests.Quest(100);var catalog=new QuestCatalog(new() {[100]=parent,[101]=quest});
        var character=new Character(1,0,"Quest",1,new Location(38,2,450,240),null);
        TestCheck.That(quest.Definition is {Diagnostics.IsEmpty:true} && quest.Prerequisites.SequenceEqual([100]) &&
            quest.Definition.Fields.All(field=>field.Name!="/pre required quest"),"重复空前置闭合沿external忽略，完整前置仍保存");
        TestCheck.That(!QuestEligibility.Evaluate(character,quest,catalog).Eligible &&
            QuestEligibility.Evaluate(character with {CompletedQuests=[100]},quest,catalog).Eligible,
            "普通接取与跳过共用读取结果，不能因忽略空闭合绕过真实前置");
        TestCheck.That(new PvfTokenDocument(tokens,id=>strings[id],preserveExtraEndTags:true).Diagnostics.Length==2,
            "任务读取兼容不放宽通用PVF嵌套解析器");
        foreach(var tail in new PvfToken[][]
        {
            [new(3,5)],[new(3,4),new(6,3),new(3,5),new(3,5)],
            [new(3,4),new(0,100),new(3,5),new(3,8)],
            [new(3,4),new(0,100),new(3,5),new(3,5),new(3,9),new(0,100)]
        })
            TestCheck.That(QuestDefinitionReader.Read(101,"fixture/invalid-end.qst",prefix.Concat(tail).ToArray(),id=>strings[id])
                .Definition is {Diagnostics.Length:>0},"孤立/非整数/其他多余闭合及额外错误仍不可用");
        TestCheck.Reject<InvalidDataException>(()=>QuestDefinitionReader.Read(101,"fixture/value-end.qst",
            prefix.Concat(new PvfToken[]{new(3,4),new(0,100),new(3,5),new(3,5),new(0,1)}).ToArray(),id=>strings[id]),
            "闭合后的无标签数值不能被当作无害空闭合");
        Console.WriteLine($"PASS {TestCheck.Count-checks} external重复空闭合与真实前置读取断言");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("FAIL: " + name); }
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        var indexedQuests = PvfScript.List(pvf.ReadText("n_quest/quest.lst"));
        Check(catalog.TryGet(5927,out _) == indexedQuests.ContainsKey(5927),
            "孤立open_beast脚本不再为未登记5927注入回退任务");
        var first = catalog.Get(3145);
        Check(first.Grade == "[epic]" && first.Difficulty == "1" && first.RewardType == "[item]" &&
            first.DeclaredGoldMultiple is null && first.DeclaredIgnoreLevelForExperience is null && first.Attributes.Count == 0,
            "3145完整奖励元数据保留声明与缺项，不能把缺项默默写成XP/Gold零值");
        Check(first.Condition == "[clear map]" && first.SubType == -1 && first.IntData.SequenceEqual([76126])
            && first.Npc == 1 && first.CompleteNpc == 1247 && first.MinimumLevel == 1 && first.MaximumLevel == 99
            && first.Prerequisites.Count == 0 && first.MainQuest is null && first.DungeonIndex == 3 && first.DungeonParameter == -1,
            "3145 remains a single clear-map boolean for 76126");
        var meet = catalog.Get(4873);
        Check(meet.Condition == "[meet npc]" && meet.IntData.SequenceEqual([1]) && meet.Prerequisites.SequenceEqual([3145])
            && meet.Npc == 1247 && meet.CompleteNpc == 1 && meet.DungeonIndex is null, "4873 meets Linus after 3145");
        var goblin = catalog.Get(3146);
        Check(goblin.Condition == "[quest clear]" && goblin.SubType == 2 && goblin.IntData.SequenceEqual([3157, 3054])
            && goblin.Prerequisites.SequenceEqual([4873]), "3146 aggregates 3157 and 3054");
        Check(QuestRelationProgress.Initial(goblin,catalog,[]) == 2 &&
            QuestRelationProgress.Initial(goblin,catalog,[3157]) == 1 && QuestRelationProgress.Initial(goblin,catalog,[3157,3054]) == 0,
            "真实3146按完整未完成子列表初始化；编码合同不证明剧情事件或原服初值公式");
        Check(catalog.Get(3157).Condition == "[hunt enemy]" && catalog.Get(3157).MainQuest == 3146
            && catalog.Get(3157).IntData.SequenceEqual([3, -1, 13099, 3, 1]), "3157 keeps the raw hunt tuple");
        Check(catalog.Get(3054).Condition == "[clear map]" && catalog.Get(3054).IntData.SequenceEqual([76136])
            && catalog.Get(3054).MainQuest == 3146, "3054 clears 76136 under 3146");
        Check(catalog.Get(3147).IntData.SequenceEqual([76146]) && catalog.Get(3147).CompleteNpc == -1
            && catalog.Get(3147).MinimumLevel == 5 && catalog.Get(3147).Prerequisites.SequenceEqual([3146]), "3147 has no town complete npc");
        Check(catalog.Get(3148).IntData.SequenceEqual([76156]) && catalog.Get(3148).CompleteNpc == -1
            && catalog.Get(3148).Prerequisites.SequenceEqual([3147]) && catalog.Get(3148).MinimumLevel == 8, "3148 follows 3147 at level 8");
        Check(catalog.Get(4875).Condition == "[meet npc]" && catalog.Get(4875).Prerequisites.SequenceEqual([3148])
            && catalog.Get(4875).CompleteNpc == 1, "4875 returns to Linus");
        var seria = catalog.Get(3149);
        Check(seria.IntData.SequenceEqual([76166]) && seria.CompleteNpc == 2 && seria.MinimumLevel == 11
            && seria.Prerequisites.SequenceEqual([4875]) && seria.DungeonIndex == 7, "3149 rescues Seria in dungeon 7");
        Check(catalog.All.Count > 2500 && catalog.TryGet(3150, out _), "完整目录保留3149之后的剧情，不再按九项白名单过滤");
        Check(first.Rewards.Count > 2 && first.Rewards[0] == new QuestRewardToken(0, 10165055, null) && first.Rewards[1] == new QuestRewardToken(0, 1, null),
            "3145 public reward is one bracelet");
        Check(first.Rewards.Contains(new QuestRewardToken(6, first.Rewards.First(t => t.Text == "[job]").Value, "[job]"))
            && first.Rewards.Any(t => t.Type == 0 && t.Value == 101010438), "3145 keeps the job-branched weapon token");
        Check(catalog.Get(3146).Rewards.Where(t => t.Type == 0).Select(t => t.Value).SequenceEqual([10164759, 1, 20002, 1, 24002, 1, 22002, 1]),
            "3146 reward pairs stay in source order");
        Check(catalog.Get(3148).Rewards.SequenceEqual([new QuestRewardToken(0, 0, null), new QuestRewardToken(0, 0, null)])
            && catalog.Get(4873).Rewards.SequenceEqual(catalog.Get(3148).Rewards)
            && catalog.Get(4875).Rewards.SequenceEqual(catalog.Get(3148).Rewards), "zero reward pairs do not become item 0");
        Check(catalog.Get(3157).Rewards.Count == 0 && catalog.Get(3054).Rewards.Count == 0, "hunt and linked clear map have no item reward");
        Check(catalog.Get(3157).Attributes.Contains("not give exp quest") && catalog.Get(3054).Attributes.Contains("not give exp quest"),
            "子任务禁EXP属性保留原字符串，不能推广到3145");
        Check(first.BossTemplate == 107000903 && catalog.Get(3054).BossTemplate == 107000908
            && catalog.Get(3147).BossTemplate == 107000905 && catalog.Get(3148).BossTemplate == 107000930
            && seria.BossTemplate == 107000910 && meet.BossTemplate is null,
            "clear-map bosses stay on their quest maps");
        Check(seria.ShowNpcOnClear == 2 && first.ShowNpcOnClear is null && meet.ShowNpcOnClear is null,
            "only 3149 shows NPC 2 after its own clear");
        Check(first.MapNpcIds.SequenceEqual([1000]) && catalog.Get(3054).MapNpcIds.SequenceEqual([1000])
            && catalog.Get(3147).MapNpcIds.SequenceEqual([1000]) && catalog.Get(3148).MapNpcIds.SequenceEqual([1000])
            && seria.MapNpcIds.Count == 0 && meet.MapNpcIds.Count == 0
            && first.MapNpcIds.All(id => id != 1247),
            "clear maps place Delilah or nobody, not quest NPC 1247");
        Check(NpcFlag(pvf, 1000, "[visible on dungeon clear]") == 1 && NpcName(pvf, 1247) == "1247_Q_tobi.npc",
            "Delilah appears after the dungeon clear and 1247 stays a portrait script");
        var stackable = Ids(pvf, "stackable/stackable.lst");
        var equipment = Ids(pvf, "equipment/equipment.lst");
        Check(stackable.Contains(10165055) && stackable.Contains(10164759)
            && !stackable.Contains(101010438)
            && new[] { 101010438, 10403, 12403, 14403, 16403, 18403, 20002, 24002, 22002 }.All(equipment.Contains),
            "story rewards stay in stackable or equipment lists");
        var terra = catalog.Get(3956);
        Check(terra.Condition == "[seeking]" && terra.DungeonIndex == 310 &&
            terra.IntData.SequenceEqual([10164805, 20, 10164804, 10]) &&
            terra.Definition!.Drops.Any(row => row is { TemplateId: 85176, EnemyType: 3, ItemId: 10164805, Count: 2, Rate: 100, MaximumHeld: 20 }) &&
            terra.Definition.Drops.Any(row => row is { TemplateId: 85176, EnemyType: 3, ItemId: 10164804, Count: 1, Rate: 100, MaximumHeld: 10 }),
            "避开帝国的耳目从时间广场泰拉石掉两种材料");
        var awakening = catalog.Get(5154);
        Check(awakening.RewardType is null && awakening.DeclaredGoldMultiple == 200 && awakening.Condition == "[hunt monster]" &&
            awakening.Rewards.SequenceEqual([new QuestRewardToken(0, 0, null), new QuestRewardToken(0, 0, null)]) &&
            QuestCoverage.GoldOnlyReward(awakening) && QuestCoverage.Unconfirmed(awakening).Length == 0,
            "夜刀神觉醒省略reward type，金币倍率200是奖励");
        Console.WriteLine($"PASS {checks} story quest resource assertions");
    }

    static int NpcFlag(PvfArchive pvf, int npcId, string tag)
    {
        var tokens = pvf.ReadTokens("npc/" + NpcName(pvf, npcId));
        for (int i = 0; i + 1 < tokens.Length; i++)
            if (tokens[i].Type == 3 && pvf.ResolveString(tokens[i].Value) == tag && tokens[i + 1].Type == 0)
                return tokens[i + 1].Value;
        throw new InvalidDataException("NPC 没有标签 " + tag);
    }

    static HashSet<int> Ids(PvfArchive pvf, string listPath)
    {
        var tokens = pvf.ReadTokens(listPath);
        var ids = new HashSet<int>();
        for (int i = 0; i + 1 < tokens.Length; i += 2)
            if (tokens[i].Type == 0) ids.Add(tokens[i].Value);
        return ids;
    }

    static string NpcName(PvfArchive pvf, int npcId)
    {
        var tokens = pvf.ReadTokens("npc/npc.lst");
        for (int i = 0; i + 1 < tokens.Length; i += 2)
            if (tokens[i].Type == 0 && tokens[i].Value == npcId && tokens[i + 1].Type == 6)
                return pvf.ResolveString(tokens[i + 1].Value);
        throw new InvalidDataException("npc.lst 没有 " + npcId);
    }
}
