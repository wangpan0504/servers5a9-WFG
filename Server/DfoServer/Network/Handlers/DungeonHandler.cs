using System.Security.Cryptography;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Currency;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Handlers;

internal sealed class DungeonHandler(ServerLog log, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
    CharacterGrowthCatalog growth, DungeonKillExperienceCatalog rewards, TownProjection town, CharacterLevelProjection levelProjection,
    DungeonMonsterDropCatalog? drops = null, ItemCatalog? items = null, EpicPieceCatalog? pieces = null,
    GoldLimitCatalog? goldLimits = null, QuestProjection? quests = null,
    Func<DungeonEntryPlan, DungeonPlannedMonster, DungeonDropGenerationPlan>? prepareDrops = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SELECT_DUNGEON, SelectAsync, nameof(DungeonHandler));
        commands.Register(CmdPacketType.FINISH_LOADING, LoadedAsync, nameof(DungeonHandler));
        commands.Register(CmdPacketType.DIE_MONSTER, DeathAsync, nameof(DungeonHandler));
        commands.Register(CmdPacketType.MOVE_MAP, MoveAsync, nameof(DungeonHandler));
        commands.Register(CmdPacketType.GIVEUP_GAME, ReturnAsync, nameof(DungeonHandler));
        commands.Register(CmdPacketType.GET_ITEM, PickupAsync, nameof(DungeonHandler));
        commands.Register(CmdPacketType.DUNGEON_EVENT_STORY_PAUSE, StoryPauseAsync, nameof(DungeonHandler));
    }

    async Task<CommandResult> StoryPauseAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.TryParseStoryPause(request.Body,out var pause)) return CommandResult.Unhandled;
        var receipt = context.Session.CurrentFormalDungeonScene();
        if (receipt is null) return CommandResult.Unhandled;
        await context.Notify(NotiPacketType.DUNGEON_EVENT_STORY_PAUSE,
            DungeonPackets.StoryPause(receipt.Character.Id,pause.State,pause.Type),"formal_story_pause",
            () => context.Session.CanPublishFormalDungeon(receipt));
        // 演出恢复后重新激活已加载角色；提前恢复仍由后续加载确认发布初始状态。
        if (pause.State == 1 && receipt.Snapshot.Phase is SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement)
            await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(receipt.Character.Id),
                "formal_story_player_state", () => context.Session.CanPublishFormalDungeon(receipt));
        return CommandResult.Handled;
    }

    async Task<CommandResult> SelectAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.TryParseSelection(request.Body, out var selection)) return CommandResult.Unhandled;
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.Ready,
            context.Session.SelectionEpoch, context.Session.Phase, context.Session.CurrentDungeonSelection));
        var character = before.Selected;
        if (character is null || !before.Ready || before.Phase is not (SessionPhase.Town or SessionPhase.MapSelect) ||
            context.BlocksTownActions)
            return CommandResult.Unhandled;
        bool replay = selection.Unknown8 == 1;
        // 回放由1459的完成事实/显式路线授权；附加Scene flags只保留审计，不套普通选图常量。
        if (selection.Unknown5 != 0 || selection.Unknown11 != 0 || !replay &&
            (selection.Unknown7 != 0 || selection.Unknown8 != 0 || selection.Unknown9 != ushort.MaxValue ||
             selection.Unknown15 != 0 || selection.Unknown20 != 0 || selection.Unknown21 != 0))
            return await RejectAsync();
        var receipt = replay ? context.Session.EnterQuestReplay(selection.DungeonIndex,selection.Unknown16,selection.Parameter) :
            context.Session.EnterFormalDungeon(selection.DungeonIndex, selection.Unknown16, selection.Parameter, dungeons, rooms,
                drops, items, pieces: pieces, prepareDrops: prepareDrops);
        log.Write("formal_dungeon_entry", new { Connection = context.Id, Accepted = receipt is not null, Replay = replay, Request = selection, receipt?.Snapshot.Identity });
        if (receipt is null) return await RejectAsync();
        await town.DetachAsync(context);
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        await context.Notify(NotiPacketType.DUNGEON_INFO, DungeonPackets.Information(receipt.Entry), "formal_dungeon_information", Guard);
        await context.Notify(NotiPacketType.START_MAP, DungeonPackets.StartMap(receipt.Entry, receipt.Monsters, receipt.MapSeed), "formal_start_map", Guard);
        return CommandResult.Handled;

        async Task<CommandResult> RejectAsync()
        {
            bool CanSend() => context.Session.CanPublishFormalDungeonRejection(character, before.SelectionEpoch,
                before.Phase, before.CurrentDungeonSelection);
            // 只结束本次入场等待，保留原选图和回城锚点；不发送成功、弹窗或自动回城通知。
            await context.Reply(CmdPacketType.SELECT_DUNGEON, DungeonPackets.SelectionRejected(), "formal_dungeon_rejected", CanSend);
            return CommandResult.Handled;
        }
    }

    async Task<CommandResult> LoadedAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 8 || request.Body.Any(value => value != 0)) return CommandResult.Unhandled;
        var receipt = context.Session.CompleteFormalDungeonLoading();
        if (receipt is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        await context.Reply(CmdPacketType.FINISH_LOADING, [1], "formal_loading_ack", Guard);
        await context.Notify(NotiPacketType.FINISH_LOADING, DungeonPackets.LoadingNotice(), "formal_loading_notice", Guard);
        await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(receipt.Character.Id), "formal_player_state", Guard);
        if (receipt.Entry.IsOrdinary)
            await context.Notify(NotiPacketType.FATIGUE, FatiguePackets.Current(context.Session.FatigueForClient(receipt.Character)), "formal_room_fatigue", Guard);
        if (context.Elevator is not null) await context.Elevator.StartAsync(receipt);
        return CommandResult.Handled;
    }

    async Task<CommandResult> DeathAsync(GameConnectionContext context, Packet request)
    {
        if (DungeonRequests.TryParsePassiveDeath(request.Body, out var passive))
        {
            if (!context.Session.Ready || context.Session.DungeonSnapshot is not { Phase: SinglePlayerDungeonPhase.Dungeon })
                return CommandResult.Unhandled;
            var progress = context.Session.ObserveFormalPassiveDeath(passive!.ObjectIdentifier,passive.ActorFlags,
                passive.ChecksumMatches,Convert.ToHexString(SHA256.HashData(request.Body)),rooms);
            log.Write("formal_passive_death", new { Connection = context.Id, Accepted = progress?.Commit is not null, Request = passive });
            if (progress?.Commit is not null)
            {
                if (progress.Commit.Plan.Inventory.ChangedSlots.Length > 0)
                    await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(progress.Character.Inventory,
                        InventoryListType.Main,[.. progress.Commit.Plan.Inventory.ChangedSlots]),"formal_passive_quest_items",
                        () => context.Session.CanPublishQuestLifecycle(progress));
                await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(progress.Character.ActiveMainQuests),
                    "formal_passive_quest_conditions",() => context.Session.CanPublishQuestLifecycle(progress));
            }
            return CommandResult.Handled;
        }
        var previousLearning = context.Session.Selected?.Learning;
        FormalDungeonDeathResult result;
        uint objectKey;
        object audit;
        if (DungeonRequests.TryParseScriptedDeath(request.Body,out var scripted))
        {
            objectKey = scripted!.ObjectKey;
            audit = scripted;
            result = context.Session.ObserveFormalDungeonScriptedDeath(new NativeDungeonScriptedDeathObservation(objectKey,scripted.RewriteFlag,
                scripted.ChecksumMatches,Convert.ToHexString(SHA256.HashData(request.Body))),rewards,growth);
        }
        else
        {
            if (!DungeonRequests.TryParseDeath(request.Body,out var parsed) || parsed!.SinglePlayerStatistics is not { } statistics)
                return CommandResult.Unhandled;
            objectKey = parsed.ObjectKey;
            var part = statistics.Participant;
            var observation = new NativeDungeonDeathObservation(objectKey,parsed.Reporter,part.Kind,part.ObjectKey,
                part.CumulativeHpDecrease,part.PositiveHpDecreaseCount,statistics.RewriteFlag,statistics.SpawnLevel,
                statistics.SpawnHpSnapshot,statistics.Unknown18,statistics.Unknown26,statistics.ChecksumMatches,
                Convert.ToHexString(SHA256.HashData(request.Body)))
            { Participants = statistics.Participants.Select(value => new NativeDungeonDeathParticipant(value.Kind,value.ObjectKey,
                value.CumulativeHpDecrease,value.PositiveHpDecreaseCount)).ToImmutableArray() };
            audit = observation;
            result = context.Session.ObserveFormalDungeonDeath(observation,rewards,growth);
        }
        var target = result.ReportedTarget;
        log.Write("formal_dungeon_death", new { Connection = context.Id, result.Outcome, ObjectKey = objectKey, Report = audit,
            Target = target is null ? null : new { target.MonsterId, target.SpawnType, target.IsBlocking, target.CanReportDeath },
            result.Receipt?.Snapshot.Identity, result.Reward, result.GainedExperience, result.ClearFact });
        if (result.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate))
            return CommandResult.Unhandled;
        if (result.Receipt is not { } receipt) return CommandResult.Handled;
        if (result.SpecialChildRetired)
        {
            if (result.Elevator is { } elevator)
                await context.Notify(NotiPacketType.ELEVATOR_CLEAR_TIME_CHECK, DungeonPackets.Elevator(elevator.Notice!),
                    "formal_elevator_completed", () => context.Session.CanPublishDungeonElevator(elevator));
            await context.Notify(NotiPacketType.DIE_MONSTER, DungeonPackets.MonsterDeath(result.MonsterKey, []),
                "formal_special_child_retired", () => context.Session.CanPublishFormalDungeon(receipt));
            return CommandResult.Handled;
        }
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        // 02B0FA96读取第二个u16作为可见性过滤；普通掉落显式为0，位置由客户端死亡对象散落链确定。
        var grounds = result.Drops;
        if (grounds.IsEmpty && result.Drop is { } single)
            grounds = [single];
        DungeonDropPacketItem[] dropRows = grounds.Select(drop => new DungeonDropPacketItem(drop.Reservation.GroundKey,
            drop.Reservation.RecordSlot, drop.Content.Item, 0, 0) { GoldAmount = drop.GoldAmount }).ToArray();
        await context.Notify(NotiPacketType.DIE_MONSTER, DungeonPackets.MonsterDeath(result.MonsterKey, dropRows), "formal_monster_death", Guard);
        if (context.Session.PendingDungeonBossEntrance() is { } entrance)
        {
            bool sent = false;
            await context.Notify(NotiPacketType.COMPLETE_CONDITION_PASS_GATE,DungeonPackets.BossEntranceOpened(),"formal_boss_entrance_opened",
                () => sent = context.Session.CanPublishFormalDungeon(entrance));
            if (sent) context.Session.ConfirmDungeonBossEntrance(entrance);
        }
        // 友方、非阻挡对象只应答退场，不发布经验、任务或结算；未知和已结算敌人仅审计。
        if (result.Reward is not { } reward) return CommandResult.Handled;
        var state = receipt.Character.DungeonState ?? throw new InvalidOperationException("已提交角色缺成长附属owner");
        var auxiliary = new CharacterGrowthAuxiliaryState(checked((uint)receipt.Character.Inventory.WinPoints), state.GrowthCapsuleExperience,
            state.ProficiencyLevel, state.ProficiencyExperience);
        var display = CharacterExperienceDisplay.Ordinary;
        foreach (ushort key in result.CulledMonsterKeys)
            await context.Notify(NotiPacketType.DIE_MONSTER, DungeonPackets.MonsterDeath(key, []), "formal_clear_room_cull", Guard);
        await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(receipt.Character.Level, receipt.Character.Learning, auxiliary, display), "formal_kill_experience", Guard);
        if (result.QuestDropSlots.Count > 0)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main,[.. result.QuestDropSlots]),"formal_quest_drop_items",Guard);
        if (previousLearning is not null && SkillPackets.BindingsChanged(previousLearning, receipt.Character.Learning))
            await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(receipt.Character.Learning), "formal_growth_skills", Guard);
        await levelProjection.RefreshAsync(context, receipt.Character,
            result.PreviousLevel ?? throw new InvalidOperationException("已提交死亡缺少原实际等级"), "formal_level_committed", Guard);
        bool closed = quests is not null && await quests.CloseReadyChildrenAsync(context);
        if (!closed && result.PreviousQuests is { } previous && receipt.Character.ActiveMainQuests.Any(row => previous.Any(old => old.AcceptanceId == row.AcceptanceId && old.ConditionState != row.ConditionState)))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(receipt.Character.ActiveMainQuests), "formal_hunt_conditions", Guard);
        if (result.ClearFact is not null && context.Session.EnableFormalDungeonSettlement(result.MonsterKey) is { } completion)
        {
            bool CompletionGuard() => context.Session.CanPublishDungeonSettlement(completion.Receipt);
            await context.Notify(NotiPacketType.BOSS_DIE_CHECK, DungeonSettlementPackets.BossAccepted(completion.BossKey), "formal_boss_confirmed", CompletionGuard);
            bool enabled = false;
            await context.Notify(NotiPacketType.ENABLE_CLEAR_DUNGEON, [], "formal_clear_enabled", () => enabled = CompletionGuard());
            if (enabled) context.Session.ConfirmFormalDungeonSettlementEnabled(completion.Receipt);
        }
        return CommandResult.Handled;
    }

    async Task<CommandResult> PickupAsync(GameConnectionContext context, Packet request)
    {
        if (items is null || pieces is null || !DungeonPickupRequests.TryParseOrdinary(request.Body, out var parsed))
            return CommandResult.Unhandled;
        var previousQuests = context.Owner.Execute(() => context.Session.Selected?.ActiveMainQuests);
        var result = context.Session.PickFormalDungeonItem(parsed.GroundObjectKey, items, pieces, goldLimits);
        if (result.Outcome != DungeonRunOutcome.Accepted || result.Receipt is not { } receipt || result.Commit is not { } commit)
        {
            // 本服普通拒绝用已证可解除等待且保留原地面核心的22，不命名为原版满包错误。
            await context.Reply(CmdPacketType.GET_ITEM, DungeonPickupPackets.Rejected(22), "formal_pickup_rejected");
            return CommandResult.Handled;
        }
        if (!commit.ProjectionCurrent) return CommandResult.CloseConnection;
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        var destination = commit.Currency is { } kind
            ? new InventorySlot(InventoryListType.Main, CurrencyPackets.PickupSlot(kind)) : commit.Destination;
        // 普通consumer不使用这九个局部值。本服无扩展贡献，明确投影零，不作Builder默认。
        var notice = commit.Currency == CurrencyKind.Gold
            ? DungeonPickupPackets.SinglePlayerGoldNotification(parsed.GroundObjectKey, receipt.Character.Id, checked((uint)commit.Amount))
            : DungeonPickupPackets.OrdinaryNotification(parsed.GroundObjectKey,
                receipt.Character.Id, new byte[8], receipt.Character.Id, checked((ushort)destination.Index), 0);
        await context.Notify(NotiPacketType.GET_ITEM, notice, "formal_pickup_notice", Guard);
        await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
            InventoryListType.Main, [destination]), "formal_pickup_inventory", Guard);
        await context.Reply(CmdPacketType.GET_ITEM, DungeonPickupPackets.Accepted(), "formal_pickup_ack", Guard);
        if (previousQuests is not null && !previousQuests.SequenceEqual(receipt.Character.ActiveMainQuests))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(receipt.Character.ActiveMainQuests), "formal_pickup_quest_conditions", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> MoveAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.TryParseMove(request.Body, out var move)) return CommandResult.Unhandled;
        if (move!.Unknown10 == 1 && context.Session.ConfirmFormalDungeonLayerBase(move.X,move.Y))
        {
            log.Write("formal_layer_base_ack",new { Connection = context.Id, move.X, move.Y });
            return CommandResult.Handled;
        }
        var receipt = context.Session.MoveFormalDungeon(move.X, move.Y, move.Unknown10);
        log.Write("formal_dungeon_move",new { Connection = context.Id, Accepted = receipt is not null,
            move.X, move.Y, Kind = move.Unknown10, receipt?.Snapshot.CurrentMap, receipt?.Snapshot.MapOperation, receipt?.Snapshot.MapPayload });
        if (receipt is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        await context.Reply(CmdPacketType.MOVE_MAP, DungeonPackets.MoveAccepted(), "formal_move_ack", Guard);
        await context.Notify(NotiPacketType.START_MAP, DungeonPackets.StartMap(receipt.Entry, receipt.Monsters, receipt.MapSeed,
            receipt.Snapshot.MapOperation,receipt.Snapshot.MapPayload), "formal_next_map", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> ReturnAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0) return CommandResult.Unhandled;
        var receipt = context.Session.ReturnFromFormalDungeon();
        if (receipt is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishTravel(receipt);
        await context.Reply(CmdPacketType.GIVEUP_GAME, DungeonPackets.GiveUpAccepted(), "formal_giveup_ack", Guard);
        await town.ReturnFromFormalDungeonAsync(context, receipt, "formal_return_town");
        log.Write("formal_dungeon_return", new { Connection = context.Id, Reason = "giveup", receipt.Character.Id, receipt.Character.Position });
        return CommandResult.Handled;
    }
}
