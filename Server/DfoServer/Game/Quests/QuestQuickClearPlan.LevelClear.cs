using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal sealed partial class QuestQuickClearPlan
{
    // 批量标记不复用手册的逐项接取/问答，也不执行普通奖励或职业变化。
    internal static bool MarkingGrade(StoryQuest quest,uint action) => quest.Grade=="[epic]" ||
        (action is QuestTicketDefinition.LevelAction or QuestTicketDefinition.AllAction) && AchievementTitle(quest) ||
        action==QuestTicketDefinition.AllAction && (quest.Grade is null or "" or "[normal]" or "[mission]");

    static bool TryCreateLevelClearTicket(Character expected,Guid operationId,QuestCatalog quests,QuestTicketSource ticket,
        ImmutableDictionary<int,byte> answers,out QuestQuickClearPlan? plan,out string reason,TitleCatalog? titles)
    {
        plan=null;
        try
        {
            uint action=ticket.Definition.ActionIndex;
            int limit=action switch
            {
                QuestTicketDefinition.GmAction => int.MaxValue,
                QuestTicketDefinition.AllAction => expected.Level,
                _ => ticket.Definition.CompletionLevel ?? 0
            };
            if (expected.Id==0 || expected.Level is <1 or >95 || expected.Job>=16 || operationId==Guid.Empty || !answers.IsEmpty ||
                limit<1 ||
                !ReferenceEquals(ticket.Inventory.Before,expected.Inventory) || expected.Inventory.Find(ticket.Item.Position)!=ticket.Item)
                throw new InvalidDataException("定级清理券需要原角色、真实源、合法资源等级及独立操作身份");
            var completed=QuestEligibility.Completed(expected);
            bool withTitles=action is QuestTicketDefinition.LevelAction or QuestTicketDefinition.AllAction;
            var scope=quests.All.Where(quest=>quest.MinimumLevel>=1 && quest.MinimumLevel<=limit && !completed.Contains(quest.Id) &&
                MarkingGrade(quest,action) && quest.Definition is {AccountQuest:false} &&
                !QuestAccountPolicy.SharedCompletion(quest) && QuestEligibility.CharacterRequirements(expected,quest,quests).Eligible).ToImmutableArray();
            ImmutableArray<int> general=!withTitles ? [] : titles?.Slots.Where(row=>row.Key.Category==0 && row.Value.Kind!=-1 && row.Value.QuestId>0)
                .OrderBy(row=>row.Key.Index).Select(row=>row.Value.QuestId).Distinct()
                .Where(id=>expected.TitleProgress.GetValueOrDefault(id)?.Remaining.Complete!=true).ToImmutableArray() ?? [];
            if (scope.IsEmpty && general.IsEmpty) throw new InvalidDataException("指定等级内的主线、称号及旧称号簿奖励均已完成");
            plan=new(expected,operationId,limit,scope,ImmutableDictionary<int,byte>.Empty,[],quests,ticket);
            plan.GeneralTitleQuestIds=general;
            plan.Snapshot=JsonSerializer.Serialize(new { State=JsonSerializer.Deserialize<JsonElement>(plan.Snapshot),
                Action=ticket.Definition.ActionIndex,CompletionLevel=limit,GeneralTitleQuestIds=general,
                TitleProgressBefore=expected.TitleProgress,TitleSectionsBefore=expected.TitleSections });
            reason="指定资源等级内主线、achievement/title及general称号已冻结，不提升角色等级";
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
        { reason=error.Message; return false; }
    }
}
