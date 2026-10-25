using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestHiddenNpcDialogueTests
{
    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int before = Count;
        var quests = SelfTestCatalogs.Quests(pvf); var quest = quests.Get(2699);
        var skills = SelfTestCatalogs.SkillLearning(pvf); var growth = SelfTestCatalogs.Growth(pvf,skills);
        var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
        That(QuestHiddenEntry.NpcDialogueShape(quest) && quest.Definition!.Values("npc dialog")[0].Value == 480 &&
            quest.IntData.SequenceEqual([58]) && QuestHiddenEntry.Direct(quest,quests),"2699保留真实NPC480入口与NPC58会面目标");
        VerifyClearedDialogue(towns,characters,items,quests,skills,growth,parameters);
        var done = towns.AccessQuestIds.Concat(quest.Prerequisites).Where(id => id != quest.Id).Distinct().ToArray();
        Location At(byte town,byte area)
        {
            var rectangle = towns.Areas[(town,area)].Map(done.ToHashSet()).Bounds
                .First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            return new(town,area,checked((ushort)(rectangle.X+rectangle.Width/2)),checked((ushort)(rectangle.Y+rectangle.Height/2)));
        }
        var palace = At(6,2); var workshop = At(6,0);
        using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,level:95,completed:done,position:palace);
        var original = f.Read();
        That(QuestEligibility.Candidates(original,quests).Contains(2699) &&
            !QuestEligibility.Visible(original,quests).Contains(2699) && !QuestHiddenEntry.CanOpen(original,quest,quests),
            "对话隐藏任务进入21候选而非普通列表，不能走即时开启");
        That(!QuestHiddenEntry.CanOpenNpcDialogue(original with { Level=89 },quest,quests,towns,480) &&
            !QuestHiddenEntry.CanOpenNpcDialogue(original with { CompletedQuests=[] },quest,quests,towns,480) &&
            !QuestHiddenEntry.CanOpenNpcDialogue(original with { Position=workshop },quest,quests,towns,480),
            "原等级、前置与实际NPC场景不可由客户端字段替代");
        var malformed = quest with { Definition=quest.Definition! with { Fields=quest.Definition.Fields.Add(new("hidden set",[],[])) } };
        That(!QuestHiddenEntry.NpcDialogueShape(malformed),"额外隐藏触发条件不能被NPC对话入口忽略");
        DfoServer.Game.Session.QuestLifecycleReceipt? Change(QuestLifecycleAction action) =>
            f.Session.ChangeQuest(2699,action,quests,items.Items,items.GoldLimits,items.EpicPieces,
                action == QuestLifecycleAction.Condition ? new(null,0,0) : null);
        That(Change(QuestLifecycleAction.Accept)?.Commit is null,"未收到合法884许可，直接31不能绕过NPC入口");
        That(f.Session.ReadHiddenQuest(2699,quests,58) is { Open:false } && Change(QuestLifecycleAction.Accept)?.Commit is null,
            "伪造会面目标58不能替代开启NPC480");
        var opening = f.Session.ReadHiddenQuest(2699,quests,480);
        That(opening is { Open:true } && f.Read().Revision == original.Revision,"合法884只授权开启，不提前接取或修改角色");
        That(f.Session.Move(new(checked((ushort)(palace.X+1)),palace.Y,0,0,0,0)) &&
            !f.Session.CanPublishQuestHidden(opening!) && Change(QuestLifecycleAction.Accept)?.Commit is null,
            "位置事务更新快照后不能复用旧NPC开启许可");
        opening = f.Session.ReadHiddenQuest(2699,quests,480);
        That(opening is { Open:true },"仍在真实NPC场景可重新请求本次位置的开启许可");
        f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady();
        That(!f.Session.CanPublishQuestHidden(opening!) && Change(QuestLifecycleAction.Accept)?.Commit is null,
            "重选同角色也不能复用旧884许可");
        That(f.Session.ReadHiddenQuest(2699,quests,480) is { Open:true } && Change(QuestLifecycleAction.Accept)?.Commit is not null,
            "重新核验当前皇宫NPC后，真实2699经公共事务接取");
        That(Change(QuestLifecycleAction.Condition)?.Commit is null,"开启NPC480不能冒充交付目标58");
        f.TravelTo(towns,workshop);
        That(Change(QuestLifecycleAction.Condition)?.Commit is not null,"沿真实城镇连接抵达NPC58后推进原会面目标");
        var finished = f.Session.FinishQuest(2699,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
        That(finished.Outcome == QuestFinishOutcome.Committed && f.Read().CompletedQuests.Contains(2699) &&
            f.Read().ActiveMainQuests.All(row => row.QuestId != 2699),"真实2699按原奖励正常交付并关闭活动身份");
        var revision = f.Read().Revision;
        f.Session.FinishQuest(2699,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
        That(f.Read().Revision == revision && f.Session.ReadHiddenQuest(2699,quests,480) is { Open:false },
            "完成后重复交付、重复884均不重复开放或领奖");
        Console.WriteLine($"PASS {Count-before} 真实2699隐藏NPC入口、许可、选角及会面交付断言");
    }
    static void VerifyClearedDialogue(TownCatalog towns,CharacterCatalog characters,ItemResources items,
        DfoServer.GameWorld.Quests.QuestCatalog quests,SkillLearningCatalog skills,CharacterGrowthCatalog growth,
        QuestRewardParameterCatalog parameters)
    {
        var quest = quests.Get(12071);
        That(QuestHiddenEntry.ClearedNpcDialogueShape(quest) && QuestHiddenEntry.Direct(quest,quests) &&
            quest.Definition!.Values("npc dialog")[0].Value == 76 && quest.IntData.SequenceEqual([76]) &&
            quest.Prerequisites.Contains(2006),"12071严格匹配原隐藏对话76、会面76和前置2006");
        var fields = quest.Definition!.Fields;
        var cleared = fields.Single(field => field.Name == "cleared quest");
        var npc = fields.Single(field => field.Name == "npc dialog");
        foreach (var invalid in new[]
        {
            quest with { Definition = quest.Definition with { Fields = fields.Add(cleared) } },
            quest with { Definition = quest.Definition with { Fields = fields.Replace(cleared,cleared with { Values = npc.Values }) } },
            quest with { Definition = quest.Definition with { Fields = fields.Replace(cleared,cleared with { Children = [npc] }) } },
            quest with { IntData = [58] },
            quest with { Definition = quest.Definition with { Fields = fields.Remove(npc) } },
            quest with { Condition = "[seeking]",IntData = [76,1] }
        })
            That(!QuestHiddenEntry.ClearedNpcDialogueShape(invalid) && !QuestHiddenEntry.Direct(invalid,quests),
                "cleared标志重复、有值、子节点、异目标或非对话形状均不得开放");
        var done = towns.AccessQuestIds.Concat(quest.Prerequisites).Where(id => id != quest.Id).Distinct().ToArray();
        var area = towns.Areas[((byte)14,(byte)1)];
        var bounds = area.Map(done.ToHashSet()).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var location = new Location(14,1,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,level:95,completed:done,position:location);
        var original = f.Read();
        That(!QuestHiddenEntry.CanOpen(original,quest,quests) &&
            QuestHiddenEntry.CanOpenNpcDialogue(original,quest,quests,towns,76) &&
            !QuestHiddenEntry.CanOpenNpcDialogue(original with { Level=89 },quest,quests,towns,76) &&
            !QuestHiddenEntry.CanOpenNpcDialogue(original with { CompletedQuests=done.Where(id => id!=2006).ToArray() },quest,quests,towns,76) &&
            !QuestHiddenEntry.CanOpenNpcDialogue(original with { Position=towns.Room },quest,quests,towns,76),
            "12071不走即时隐藏入口，保持等级、前置与真实NPC76场景");
        DfoServer.Game.Session.QuestLifecycleReceipt? Change(QuestLifecycleAction action) =>
            f.Session.ChangeQuest(12071,action,quests,items.Items,items.GoldLimits,items.EpicPieces,
                action == QuestLifecycleAction.Condition ? new(null,0,0) : null);
        That(Change(QuestLifecycleAction.Accept)?.Commit is null &&
            f.Session.ReadHiddenQuest(12071,quests,58) is { Open:false },"12071没有许可或错误NPC不能接取");
        var opening = f.Session.ReadHiddenQuest(12071,quests,76);
        That(opening is { Open:true } && f.Read().Revision==original.Revision,"884只授予许可，不自动接取或归零");
        f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady();
        That(!f.Session.CanPublishQuestHidden(opening!) && Change(QuestLifecycleAction.Accept)?.Commit is null,
            "12071重登后旧开启许可失效");
        That(f.Session.ReadHiddenQuest(12071,quests,76) is { Open:true } && Change(QuestLifecycleAction.Accept)?.Commit is not null &&
            f.Read().ActiveMainQuests.Single(row => row.QuestId==12071).ConditionState==1,
            "真实NPC重新授权后31仍按原会面初值1接取，不凭cleared标志直接完成");
        var accepted = f.Read();
        That(Change(QuestLifecycleAction.Accept)?.Commit is null &&
            f.Session.FinishQuest(12071,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome != QuestFinishOutcome.Committed,
            "重复31及尚未会面直接34均拒绝");
        var remoteBounds = towns.Areas[((byte)14,(byte)0)].Map(done.ToHashSet()).Bounds
            .First(row => row.Conditions.Length==0 && row.Quests.All(done.Contains)).Bounds;
        var remote = new Location(14,0,checked((ushort)(remoteBounds.X+remoteBounds.Width/2)),checked((ushort)(remoteBounds.Y+remoteBounds.Height/2)));
        f.TravelTo(towns,remote);
        var away = f.Read();
        That(Change(QuestLifecycleAction.Condition)?.Commit is null && f.Read().Revision==away.Revision &&
            f.Read().ActiveMainQuests.Single(row => row.QuestId==12071).ConditionState==1,
            "12071接取后离开NPC76，客户端自动33不能凭cleared标志越场景完成");
        f.TravelTo(towns,location);
        That(Change(QuestLifecycleAction.Condition)?.Commit is not null &&
            f.Read().ActiveMainQuests.Single(row => row.QuestId==12071).ConditionState==0,
            "原客户端自动33必须经当前NPC76事实推进0");
        var revision = f.Read().Revision;
        Change(QuestLifecycleAction.Condition);
        That(f.Read().Revision==revision,"重复自动33不再写库或改变接取身份");
        f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady();
        That(f.Read().ActiveMainQuests.Single(row => row.QuestId==12071) is { ConditionState:0 } active &&
            active.AcceptanceId==accepted.ActiveMainQuests.Single(row => row.QuestId==12071).AcceptanceId,
            "重登保留真实完成进度与同一次接取身份");
        var beforeReward = f.Read();
        That(quest.MinimumLevel==90 && beforeReward.Level==95 && quest.Grade=="[hidden]" &&
            quest.DeclaredIgnoreLevelForExperience is null or 0 && quest.DeclaredGoldMultiple is null,
            "12071原等级90、角色95、非史诗惩罚及缺省金币倍率资源合同");
        var green=parameters.Penalty(false,false); var grey=parameters.Penalty(false,true);
        int penalty=5>=grey.MinimumDifference ? grey.Percent :
            5>=green.MinimumDifference && 5<=green.MaximumDifference ? green.Percent : parameters.BasicPenalty;
        uint expectedGold=checked((uint)((ulong)parameters.Gold[90]*100/100*(ulong)penalty/100));
        That(quest.Rewards is [{ Value:0 },{ Value:0 }] &&
            f.Session.FinishQuest(12071,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome == QuestFinishOutcome.Committed &&
            f.Read().CompletedQuests.Contains(12071) && f.Read().ActiveMainQuests.All(row => row.QuestId!=12071) &&
            f.Read().Inventory.Items.SequenceEqual(beforeReward.Inventory.Items) &&
            f.Read().Inventory.Gold==checked(beforeReward.Inventory.Gold+expectedGold),
            "12071沿原difficulty G及0/0动态金币marker交付，不擅改金币或增物品");
        revision=f.Read().Revision;
        f.Session.FinishQuest(12071,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
        That(f.Read().Revision==revision && f.Session.ReadHiddenQuest(12071,quests,76) is { Open:false },
            "12071完成后不可重复开奖或领取");
    }

}
