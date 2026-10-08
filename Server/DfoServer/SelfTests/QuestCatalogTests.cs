using DfoServer.GameWorld.Quests;
using PvfLib;

namespace DfoServer;

internal static class QuestCatalogTests
{
    public static void Run(PvfArchive pvf)
    {
        int checks = 0;
        void Check(bool ok, string name) { checks++; if (!ok) throw new Exception("FAIL: " + name); }
        var catalog = QuestCatalog.Load(pvf);
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
        bool rejected = false;
        try { catalog.Get(3150); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "quest after 3149 is outside this catalog");
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
