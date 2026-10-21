using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class QuestReplayHandler(QuestConnectionCatalog connections,DungeonCatalog dungeons,DungeonRoomCatalog rooms,ServerLog log,
    DungeonMonsterDropCatalog? drops = null,ItemCatalog? items = null,EpicPieceCatalog? pieces = null,
    Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? prepareDrops = null,
    CrackDimensionRewardServices? crackRewards = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.QUEST_REPLAY,PrepareAsync,nameof(QuestReplayHandler));

    async Task<CommandResult> PrepareAsync(GameConnectionContext context,Packet request)
    {
        if (!QuestReplayRequests.TryParse(request.Body,out uint questId) || context.BlocksTownActions)
            return CommandResult.Unhandled;
        var before = context.Owner.Execute(() => (context.Session.Selected,context.Session.Ready,context.Session.SelectionEpoch,context.Session.Phase));
        if (before.Selected is null || !before.Ready) return CommandResult.Unhandled;
        var receipt = context.Session.PrepareQuestReplay(questId,connections,dungeons,rooms,drops,items,pieces,prepareDrops,crackRewards);
        log.Write("quest_replay_prepare",new { Connection = context.Id,QuestId = questId,Accepted = receipt is not null,receipt?.DungeonId });
        if (receipt is not null)
            await context.Reply(CmdPacketType.QUEST_REPLAY,QuestReplayPackets.Accepted(),"quest_replay_prepared",() => context.Session.CanPublishQuestReplay(receipt));
        else
            await context.Reply(CmdPacketType.QUEST_REPLAY,QuestReplayPackets.Rejected(),"quest_replay_rejected",() => context.Owner.Execute(() =>
                context.Session.Ready && context.Session.SelectionEpoch == before.SelectionEpoch && context.Session.Phase == before.Phase &&
                ReferenceEquals(context.Session.Selected,before.Selected)));
        return CommandResult.Handled;
    }
}
