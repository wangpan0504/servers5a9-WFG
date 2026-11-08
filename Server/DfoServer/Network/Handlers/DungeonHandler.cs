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
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Currency;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler(ServerLog log, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
    CharacterGrowthCatalog growth, DungeonKillExperienceCatalog rewards, TownProjection town, CharacterLevelProjection levelProjection,
    DungeonMonsterDropCatalog? drops = null, ItemCatalog? items = null, EpicPieceCatalog? pieces = null,
    GoldLimitCatalog? goldLimits = null, QuestProjection? quests = null,
    Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? prepareDrops = null,
    TutorialCatalog? tutorials = null, TutorialScenes? tutorialScenes = null,
    CrackDimensionRewardServices? crackRewards = null, DungeonAltarRewardPolicy? altarRewards = null,
    DungeonDespairRewardPolicy? despairRewards = null, DfoServer.Game.Titles.TitleAchievementService? titles = null,
    TitleProjection? titleProjection = null, InventoryProjection? inventoryProjection = null,
    PartyDungeonService? partyDungeons = null, CharacterStatComputer? partyStats = null,
    DfoServer.Game.Party.PartyService? parties = null, EpicPieceShopService? epicPieceShop = null,
    TagEvildomRunProjection? tagEvildomRun = null, EvildomWarCatalog? evildomWar = null,
    DfoServer.Game.Raids.LukeRaidDungeonService? lukeRaid = null,
    DfoServer.Game.Raids.ILukeRaidLifecycleStore? lukeLifecycle = null,
    DfoServer.Game.Raids.AntonRaidDungeonService? antonRaid = null, DfoServer.Game.Raids.AntonRaidDungeonService? preyRaid = null,
    EvildomWarRewardPolicy? evildomRewards = null, IEvildomWarRewardStore? evildomRewardStore = null,
    ImperialArenaProjection? imperialArena = null,
    DfoServer.Game.Raids.FiendwarDungeonService? fiendwarRaid = null,
    DfoServer.Game.Strongway2019.Strongway2019RouteService? strongwayRoute = null,
    DfoServer.Game.Strongway2019.Strongway2019MatchingService? strongwayMatching = null,
    Strongway2019Projection? strongwayProjection = null,
    Strongway2019RewardProjection? strongwayRewards = null,
    DfoServer.Game.Raids.AntonRaidStart? raidStart = null,
    Func<Exception,bool>? altarProjectionBusy = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.RegisterPrivate(FiendwarKeyLeaseRequests.Opcode, FiendwarKeysAsync, nameof(DungeonHandler),
            sharedGate: static (context, _) => context.Session.CurrentParty?.Invoke()?.Commands);
        commands.Register(CmdPacketType.FIEND_WAR_BOSS_PROC, FiendwarBossAsync, nameof(DungeonHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.RAID_SITUATION_NOTICE, FiendwarSupportAsync, nameof(DungeonHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.SEQUENTIAL_DUNGEON_INFO, SequentialInfoAsync, nameof(DungeonHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.START_EVILDOM_WAR, StartEvildomWarAsync, nameof(DungeonHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.SELECT_DUNGEON, SelectAsync, nameof(DungeonHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.FINISH_LOADING, LoadedAsync, nameof(DungeonHandler), UserStateRequirement.DungeonContext);
        // 已记录死亡格式跨会话状态接收；实际死亡和奖励仍由当前上下文中的事实决定。
        commands.Register(CmdPacketType.DIE_MONSTER, DeathAsync, nameof(DungeonHandler));
        commands.Register(CmdPacketType.DIE_CHARACTER, CharacterDeathAsync, nameof(DungeonHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.USE_COIN, UseCoinAsync, nameof(DungeonHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.MOVE_MAP, MoveAsync, nameof(DungeonHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.OPERATE_RIDABLE_OBJECT, RidableOperationAsync, nameof(DungeonHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.GIVEUP_GAME, ReturnAsync, nameof(DungeonHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.GET_ITEM, PickupAsync, nameof(DungeonHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.DUNGEON_EVENT_STORY_PAUSE, StoryPauseAsync, nameof(DungeonHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.DIE_BLOOD_MONSTER, AltarDeathAsync, nameof(DungeonHandler));
        commands.Register(CmdPacketType.BLOOD_ROUND_UI_PREPARE_FINISH_, AltarPreparedAsync, nameof(DungeonHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.SELECT_ULTIMATE_DIFFICULTY, AltarDifficultyAsync, nameof(DungeonHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.SELECT_TOWER_OF_DAZZLEMENT_REWARD, SelectDazzlementRewardAsync, nameof(DungeonHandler), UserStateRequirement.WorldReady);
    }

    async Task<CommandResult> SelectDazzlementRewardAsync(GameConnectionContext context, Packet request)
    {
        byte[] body = request.Body;
        if (body.Length == 0 || body[0] > DazzlementTowerProgress.MaxSelectionLimit || body.Length != 1 + body[0])
        {
            await context.Reply(CmdPacketType.SELECT_TOWER_OF_DAZZLEMENT_REWARD, DazzlementTowerPackets.SelectRewardReply(19), "dazzlement_reward_select_rejected");
            return CommandResult.Handled;
        }
        byte[] slots = body[1..];
        if (slots.Distinct().Count() != slots.Length || slots.Any(slot => slot >= DazzlementTowerProgress.MaxStashCards))
        {
            await context.Reply(CmdPacketType.SELECT_TOWER_OF_DAZZLEMENT_REWARD, DazzlementTowerPackets.SelectRewardReply(19), "dazzlement_reward_select_rejected");
            return CommandResult.Handled;
        }
        await context.Reply(CmdPacketType.SELECT_TOWER_OF_DAZZLEMENT_REWARD, DazzlementTowerPackets.SelectRewardReply(1, slots), "dazzlement_reward_selected");
        await context.Notify(NotiPacketType.TOWER_OF_DAZZLEMENT_INFO, DazzlementTowerPackets.SelectedRewards(slots), "dazzlement_selected_rewards");
        log.Write("dazzlement_reward_selected", new { Connection = context.Id, Character = context.Session.Selected?.Id, Slots = slots });
        return CommandResult.Handled;
    }

    async Task<CommandResult> SequentialInfoAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.TryParseSequentialInfo(request.Body, out uint sequenceId)) return CommandResult.Unhandled;
        if (sequenceId == AntonNormalProgress.SequenceId)
        {
            var anton = context.Session.CaptureAntonNormalSequence();
            log.Write("anton_normal_sequence_query", new { Connection = context.Id, Sequence = sequenceId,
                Accepted = anton is not null, anton?.Day, anton?.Info.Stage, anton?.Info.Branch });
            if (anton is not null)
                await context.Notify(NotiPacketType.SEQUENTIAL_DUNGEON_INFO, AntonNormalSequencePackets.Information(anton.Info),
                    "anton_normal_sequence", () => context.Session.CanPublishAntonNormalSequence(anton));
            return CommandResult.Handled;
        }
        if (sequenceId == DfoServer.GameWorld.Dungeons.PreyNormalCatalog.SequenceId)
        {
            var prey = context.Session.CapturePreyNormalSequence();
            log.Write("prey_normal_sequence_query", new { Connection = context.Id, Sequence = sequenceId,
                Accepted = prey is not null, prey?.Day, prey?.Info.Stage, prey?.Info.Branch });
            if (prey is not null)
                await context.Notify(NotiPacketType.SEQUENTIAL_DUNGEON_INFO, PreyNormalSequencePackets.Information(prey.Info),
                    "prey_normal_sequence", () => context.Session.CanPublishPreyNormalSequence(prey));
            return CommandResult.Handled;
        }
        if (sequenceId != OrdinaryLukeProgress.SequenceId) return CommandResult.Unhandled;
        var receipt = context.Session.CaptureOrdinaryLukeSequence(sequenceId);
        log.Write("ordinary_luke_sequence_query", new { Connection = context.Id, Sequence = sequenceId,
            Accepted = receipt is not null, receipt?.Day, receipt?.Info.Stage, receipt?.Info.ClearBits });
        if (receipt is not null)
            await context.Notify(NotiPacketType.SEQUENTIAL_DUNGEON_INFO, OrdinaryLukeSequencePackets.Information(receipt.Info),
                "ordinary_luke_sequence", () => context.Session.CanPublishOrdinaryLukeSequence(receipt));
        return CommandResult.Handled;
    }

    async Task<CommandResult> CharacterDeathAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.TryParseCharacterDeath(request.Body, out var position)) return CommandResult.Unhandled;
        using var peers = context.Session.PartyDungeon is { } party ?
            await PartyDungeonProjection.AcquirePeersAsync(context, party.Members) : null;
        var receipt = context.Session.ReportFormalCharacterDeath();
        log.Write("formal_character_death", new { Connection = context.Id, Accepted = receipt is not null, position.X, position.Y, position.Context,
            Character = receipt?.Character.Id, receipt?.Snapshot.Identity, receipt?.AutomaticRevival });
        if (receipt is not null)
        {
            bool altar = !receipt.AutomaticRevival && receipt.Monsters.Altar is not null && altarRewards is not null;
            bool partyAlive = context.Session.PartyDungeon is { } currentParty &&
                currentParty.Members.Any(m => m.IsCurrent() && ReferenceEquals(m.Actor.Session.PartyDungeon, currentParty) &&
                    m.Actor.Session.CurrentDungeonDeathReturn() is null);
            if (context.Death is not null)
            {
                if (receipt.AutomaticRevival || altar || partyAlive) await context.Death.StopAsync();
                else
                {
                    if (receipt.Entry.IsRevisedBeast && items is not null && goldLimits is not null && pieces is not null)
                        context.Death.ConfigureRevisedBeast(() => RevisedBeastProjection.PublishAsync(context, items, goldLimits, pieces, log, rooms));
                    await context.Death.StartAsync();
                }
            }
            bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
            await context.Notify(NotiPacketType.DIE_STATE, DungeonPackets.CharacterLife(receipt.Character.Id, (byte)0),
                "formal_character_dead", Guard);
            await PartyDungeonProjection.PublishEventAsync(context, PartyDungeonNoticeKind.Life, receipt.Character.Id, 0);
            if (receipt.AutomaticRevival)
            {
                await context.Notify(NotiPacketType.DIE_STATE, DungeonPackets.CharacterLife(receipt.Character.Id, (byte)2),
                    "formal_character_blessing_revived", Guard);
                await PartyDungeonProjection.PublishEventAsync(context, PartyDungeonNoticeKind.Life, receipt.Character.Id, 2);
            }
            if (altar && context.Session.WaitDungeonAltarRevival() is { } waiting && context.Altar is not null)
                await context.Altar.WakeAfterDeathAsync(waiting, () => context.Session.BeginDungeonAltarSettlement(altarRewards!, growth));
        }
        return CommandResult.Handled;
    }

    async Task<CommandResult> UseCoinAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.TryParseUseCoin(request.Body, out ushort targetKey)) return CommandResult.Unhandled;
        using var peers = context.Session.PartyDungeon is { } party ?
            await PartyDungeonProjection.AcquirePeersAsync(context, party.Members) : null;
        var scene = context.Session.CurrentFormalDungeonScene();
        var result = context.Session.ReviveFormalCharacter(targetKey);
        log.Write("formal_character_revive", new { Connection = context.Id, Target = targetKey, Accepted = result?.Accepted == true,
            Coins = result?.Receipt?.Character.Inventory.ReviveCoins, RaidPhaseCoinsUsed = context.Session.RaidCombatView()?.LukePhaseCoinsUsed });
        if (result is null) return CommandResult.Handled;
        if (!result.Accepted)
        {
            await context.Reply(CmdPacketType.USE_COIN, [0, (byte)result.Error, (byte)(result.Error >> 8)],
                "formal_revive_rejected", () => scene is not null && context.Session.CanPublishFormalDungeon(scene));
            return CommandResult.Handled;
        }
        var receipt = result.Receipt ?? throw new InvalidOperationException("复活缺少扣币提交回执");
        if (context.Death is not null) await context.Death.StopAsync();
        bool Guard() => context.Session.CanPublishInventory(receipt);
        await context.Reply(CmdPacketType.USE_COIN, DungeonPackets.CoinUsed(targetKey), "formal_revive_ack", Guard);
        await context.Notify(NotiPacketType.DIE_STATE, DungeonPackets.CharacterLife(targetKey, true), "formal_character_revived", Guard);
        await PartyDungeonProjection.PublishEventAsync(context, PartyDungeonNoticeKind.Life, targetKey, 1);
        await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,
            InventoryPackets.Delta(receipt.Character.Inventory, InventoryListType.Main, [new(InventoryListType.Main, 1)]),
            "formal_revive_coin_balance", Guard);
        if (receipt.PreviousQuests is { } old && !old.SequenceEqual(receipt.Character.ActiveMainQuests))
            await QuestProjection.ProgressAsync(context,old,receipt.Character,Guard,"formal_revive_quest_conditions");
        if (context.Altar is not null && context.Session.ResumeDungeonAltar() is { } resumed)
            await context.Altar.WakeAsync(resumed);
        return CommandResult.Handled;
    }

    async Task<CommandResult> StoryPauseAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.TryParseStoryPause(request.Body,out var pause)) return CommandResult.Unhandled;
        using var peers = context.Session.PartyDungeon is { } party ?
            await PartyDungeonProjection.AcquirePeersAsync(context, party.Members) : null;
        var receipt = context.Session.CurrentFormalDungeonScene();
        if (receipt is null) return CommandResult.Unhandled;
        await context.Notify(NotiPacketType.DUNGEON_EVENT_STORY_PAUSE,
            DungeonPackets.StoryPause(receipt.Character.Id,pause.State,pause.Type),"formal_story_pause",
            () => context.Session.CanPublishFormalDungeon(receipt));
        await PartyDungeonProjection.PublishEventAsync(context, PartyDungeonNoticeKind.Pause, receipt.Character.Id, pause.State, pause.Type);
        // 演出恢复后重新激活已加载角色；提前恢复仍由后续加载确认发布初始状态。
        if (pause.State == 1 && receipt.Snapshot.Phase is SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement)
            await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(receipt.Character.Id),
                "formal_story_player_state", () => context.Session.CanPublishFormalDungeon(receipt));
        return CommandResult.Handled;
    }

    static bool SealDoorEnabled => Environment.GetEnvironmentVariable("DFO_SEAL_DOOR") == "1";
    // 该副本里可入密门的深渊 maze 的 [seal door appear rate] 最大值；无深渊 maze 为 0。
    int SealDoorRate(uint dungeonId)
    {
        try { return dungeons.Get(checked((int)dungeonId)).Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0 && maze.HellParty is not null)
            .Select(maze => maze.HellParty!.AppearRate).DefaultIfEmpty(0).Max(); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException or OverflowException) { return 0; }
    }

    async Task<CommandResult> SelectAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.TryParseSelection(request.Body, out var selection)) return CommandResult.Unhandled;
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.Ready,
            context.Session.SelectionEpoch, context.Session.Phase, context.Session.CurrentDungeonSelection));
        var character = before.Selected;
        if (character is not null && before.Ready && before.Phase == SessionPhase.Dungeon &&
            context.Session.ChannelEntry?.Type == 23 && antonRaid is { Enabled: true })
            return await SelectAntonAsync(context, selection);
        if (character is not null && before.Ready && !context.BlocksTownActions &&
            before.Phase is SessionPhase.Town or SessionPhase.MapSelect or SessionPhase.Loading &&
            context.Session.ChannelEntry?.Type == 45 && fiendwarRaid is not null)
            return await SelectFiendwarAsync(context, selection);
        if (character is not null && before.Ready && before.Phase == SessionPhase.Loading &&
            context.Session.ChannelEntry?.Type is 23 or 50 && (context.Session.ChannelEntry.Type == 50 ? preyRaid : antonRaid) is { Enabled: true } &&
            (context.Session.StartedRaid is not null || selection.DungeonIndex <= int.MaxValue && (context.Session.ChannelEntry.Type == 50 ? preyRaid : antonRaid)?.Contains((int)selection.DungeonIndex) == true))
            return await SelectAntonAsync(context, selection);
        if (character is null || !before.Ready || before.Phase is not (SessionPhase.Town or SessionPhase.MapSelect) ||
            context.BlocksTownActions)
            return CommandResult.Unhandled;
        if (context.Session.ChannelEntry?.Type is 23 or 50 && (context.Session.StartedRaid is not null ||
            selection.DungeonIndex <= int.MaxValue && (context.Session.ChannelEntry.Type == 50 ? preyRaid : antonRaid)?.Contains((int)selection.DungeonIndex) == true))
            return await SelectAntonAsync(context, selection);
        if (context.Session.ChannelEntry?.Type == 32)
        {
            var selectedParty = context.Session.CurrentParty?.Invoke();
            if (selectedParty is null || context.DispatchGates?.Holds(context, selectedParty.Commands) != true)
            {
                log.Write("formal_dungeon_entry", new { Connection = context.Id, Accepted = false, Party = true,
                    RaidType = 1, Dungeon = selection.DungeonIndex, RejectReason = "luke-party-dispatch-gate-mismatch" });
                await context.Reply(CmdPacketType.SELECT_DUNGEON, DungeonPackets.SelectionRejected(), "luke_entry_rejected");
                return CommandResult.Handled;
            }
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, selectedParty.Members);
            bool valid = lukeRaid is not null && partyDungeons is not null &&
                selection.Parameter == 2 && selection.Unknown5 == 0 && selection.Unknown7 == 0 && selection.Unknown8 == 0 &&
                selection.Unknown9 == ushort.MaxValue && selection.Unknown11 == 0 && selection.Unknown15 == 0 &&
                selection.Unknown16 == 0 && selection.Unknown20 == 0 && selection.Unknown21 is 0 or uint.MaxValue;
            PartyDungeonNotice[] entered = [];
            try
            {
                // 原生候战室先发15=0，目标图只在16确定；沿同一领域准入安装选择，不提前占图。
                if (valid && !context.Session.HasLukeRaidSelection)
                {
                    var selected = selection.DungeonIndex <= int.MaxValue
                        ? lukeRaid!.Select(context.Session, (int)selection.DungeonIndex, selectedParty) : [];
                    valid = selected.Length > 0;
                    // 选择回执仅在MapSelect有效，必须在安装Loading之前发布，不能丢弃场景与队员通知。
                    if (valid) await PartyDungeonProjection.PublishAsync(selected);
                }
                if (valid) entered = partyDungeons!.Enter(context.Session, selection.DungeonIndex, selection.Parameter,
                    dungeons, rooms, null, null, null, null);
            }
            finally
            {
                // SQL失败也仅撤销本次选择；已安装Run的选择清理是空操作，不能释放成功的占用。
                if (entered.Length == 0) lukeRaid?.CancelSelection(context.Session, selectedParty, preserveScene: true);
            }
            log.Write("formal_dungeon_entry", new { Connection = context.Id, Accepted = entered.Length > 0, Party = true,
                RaidType = 1, Dungeon = selection.DungeonIndex, Members = entered.Count(notice => notice.Kind == PartyDungeonNoticeKind.Entry),
                RaidInstance = context.Session.StartedRaid?.InstanceId,
                Runs = entered.Select(notice => notice.Scene?.Snapshot.Identity.InstanceId).Distinct().ToArray(),
                RejectReason = entered.Length == 0 ? context.Session.LastFormalDungeonRejection ?? "luke-entry-unavailable" : null });
            if (entered.Length == 0)
            {
                var rejected = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
                await context.Reply(CmdPacketType.SELECT_DUNGEON, DungeonPackets.SelectionRejected(), "luke_entry_rejected", () =>
                    context.Owner.Execute(() => context.Session.Ready && ReferenceEquals(context.Session.Selected, rejected.Selected) &&
                        context.Session.SelectionEpoch == rejected.SelectionEpoch && context.Session.Phase == rejected.Phase));
            }
            else await PartyDungeonProjection.PublishAsync(entered);
            return CommandResult.Handled;
        }
        bool training = selection.DungeonIndex == 5000 && selection.Unknown8 == 1 && selection.Unknown16 == 0;
        bool experience = selection.DungeonIndex == 7142 && selection.Unknown8 == 1 && selection.Unknown16 == 0;
        bool direct = training || experience;
        bool replay = selection.Unknown8 == 1 && !direct && selection.Unknown16 != 0;
        bool practice = selection.Unknown8 == 1 && !direct && !replay;
        bool continuation = context.Session.HasDungeonContinuationRequest();
        if (selection.DungeonIndex is 11006 or 11007 && (altarRewards is null || context.Altar is null))
            return CommandResult.Unhandled;
        // 实验性（未在95客户端实证；依据参考项目 dungeon_hell_party_entry.go）：op16 请求体 [7]=1（HellPartyRequestFlag）且 [8]=0 表示"挑战深渊派对"。
        // 实机样本：94D6591104000001 00FFFF 0000000000 E2170000 00 00000000（副本号 291100308=哈林 [dungeon] 列表里的黑暗都市）。
        // [16:20]（任务号 6114）深渊入场时不参与路线，按 questId=0 的普通入场处理。
        bool hell = selection.Unknown7 == 1 && selection.Unknown8 == 0 && !direct && !continuation;
        // 03BBA474..03BBA48C把目标匹配结果写offset20；offset7仍是普通挑战深渊模式1。
        bool legend = hell && selection.Unknown20 == 1 &&
            before.CurrentDungeonSelection?.HellLegendDungeonId == selection.DungeonIndex;
        bool beastChannel = BeastChannel.IsEntry(context.Session.ChannelEntry);
        bool beastId = selection.DungeonIndex is >= 5335 and <= 5339;
        var revisedAdmission = beastId ? RevisedBeastAdmission.Check(context.Session.ChannelEntry, character,
            checked((int)selection.DungeonIndex), selection.Parameter, context.Session.DungeonBusinessNow) : default;
        bool revisedBeast = beastId && revisedAdmission.Accepted && !direct && !replay && !practice && !continuation &&
            !hell && selection.Unknown7 == 0 && selection.Unknown8 == 0 && RevisedBeastSelection.IsQuestContext(selection.Unknown16);
        if (beastChannel && !revisedBeast || beastId && !revisedBeast) return await RejectAsync("revised-beast-admission");
        // 首版仍保持退役；改版仅走专属频道及当日资格。
        if (selection.DungeonIndex is >= 310 and <= 314 && selection.Parameter == 4 && selection.Unknown7 == 2)
            return await RejectAsync();
        if (hell && before.CurrentDungeonSelection?.HellLegendDungeonId == selection.DungeonIndex && !legend)
            return await RejectAsync();
        // 实验性/默认关闭：密门。90 参考只解析 [seal door appear rate]，没有运行时；这里的最小方案 = 普通入场（questId=0）时按 rate% 概率把该房间换成深渊图并走深渊入场（不扣票、不查深渊任务）。
        // 仅当 DFO_SEAL_DOOR=1 时启用；rate 按百分数解读（推断）。随机失败/入场被拒时回退为普通入场。
        bool sealDoor = false;
        if (practice && selection.Unknown7 != 0) return await RejectAsync();
        if (SealDoorEnabled && !revisedBeast && !practice && !hell && !legend && !direct && !continuation && selection.Unknown7 == 0 && selection.Unknown8 == 0 && selection.Unknown16 == 0 &&
            selection.DungeonIndex != 5000 && selection.DungeonIndex != 7142 && SealDoorRate(selection.DungeonIndex) is > 0 and var sealRate &&
            System.Security.Cryptography.RandomNumberGenerator.GetInt32(100) < sealRate)
            sealDoor = true;
        if (continuation && (direct || replay || practice)) return await RejectAsync();
        // 练习与两个直入共享offset8=1；非零任务号仍须经过1459的剧情回放授权。
        if (selection.DungeonIndex == 5000 && (!training || selection.Parameter != 0)) return await RejectAsync();
        if (selection.DungeonIndex == 7142 && (!experience || selection.Parameter != 0)) return await RejectAsync();
        // 回放由1459的完成事实/显式路线授权；offset21是活动关联值，仅保留审计，不作为普通入场资格。
        // 末位活动关联值也可能出现在普通及祭坛请求；特殊模式仍须通过各自的专用入场许可。
        byte expectedFlag = (byte)(hell ? 1 : 0);
        if (selection.Unknown5 != 0 || selection.Unknown11 != 0 || !replay &&
            (selection.Unknown7 != expectedFlag || selection.Unknown8 != (direct || practice ? 1 : 0) || selection.Unknown9 != ushort.MaxValue ||
            selection.Unknown15 != 0 || selection.Unknown20 != (legend ? 1 : 0)))
            return await RejectAsync();
        bool multiplePartyMembers = context.Session.HasMultiplePartyMembers?.Invoke() == true;
        bool mockBattle = context.MonsterMockBattle?.ContainsDungeon(selection.DungeonIndex) == true;
        if (mockBattle && (multiplePartyMembers || direct || replay || practice || continuation || hell || legend || sealDoor || selection.Unknown16 != 0))
            return await RejectAsync();
        // 一人真实Party也必须走共享PartyDungeonService；否则会落入单人Formal入口，英雄准入的partyAdmission被丢失。
        bool soloPartyEntry = context.Session.CurrentParty?.Invoke() is { Members.Length: 1 } soloParty &&
            selection.DungeonIndex is 5363 or 400000105 && soloParty.Members.Any(member =>
                member.Slot == soloParty.Leader && ReferenceEquals(member.Actor.Session, context.Session));
        if (multiplePartyMembers || soloPartyEntry || context.Session.ReadEvildomWar() is not null)
        {
            if (partyDungeons is null || revisedBeast || direct || replay || practice || continuation || sealDoor)
                return await RejectAsync();
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, context.Session.CurrentParty?.Invoke()?.Members ?? []);
            (string Reason, byte MemberSlot)? partyRejection = null;
            var entered = partyDungeons.Enter(context.Session, selection.DungeonIndex, selection.Parameter,
                dungeons, rooms, drops, items, pieces, prepareDrops,
                questId: hell ? 0u : selection.Unknown16, hellParty: hell, hellLegend: legend,
                crackRewards: crackRewards,
                onRejected: (reason, slot) => partyRejection = (reason, slot));
            log.Write("formal_dungeon_entry", new { Connection = context.Id, Accepted = entered.Length > 0, Party = true,
                Members = entered.Count(notice => notice.Kind == PartyDungeonNoticeKind.Entry), Dungeon = selection.DungeonIndex, Quest = selection.Unknown16, HellParty = hell, HellLegend = legend,
                Request = selection,
                RejectReason = entered.Length == 0 ? partyRejection?.Reason ?? "party-entry-unavailable" : null,
                RejectMemberSlot = partyRejection?.MemberSlot });
            if (entered.Length == 0) return await RejectAsync(partyRejection?.Reason, partyRejection?.MemberSlot, party: multiplePartyMembers);
            await PartyDungeonProjection.PublishAsync(entered);
            return CommandResult.Handled;
        }
        FormalDungeonReceipt? receipt;
        try
        {
            receipt = mockBattle ? context.MonsterMockBattle!.Enter(selection, dungeons, rooms) :
                continuation ? context.Session.EnterFormalDungeonContinuation(selection.DungeonIndex,selection.Unknown16,selection.Parameter) :
                replay ? context.Session.EnterQuestReplay(selection.DungeonIndex,selection.Unknown16,selection.Parameter) :
                (sealDoor ? context.Session.EnterFormalDungeon(selection.DungeonIndex, 0, selection.Parameter, dungeons, rooms,
                    drops, items, pieces: pieces, prepareDrops: prepareDrops,tutorials: tutorials,tutorialScenes: tutorialScenes,
                    hellParty: true, sealDoor: true, crackRewards: crackRewards) : null) ??
                context.Session.EnterFormalDungeon(selection.DungeonIndex, hell || legend ? 0u : selection.Unknown16, selection.Parameter, dungeons, rooms,
                    drops, items, pieces: pieces, prepareDrops: prepareDrops,tutorials: tutorials,tutorialScenes: tutorialScenes,
                    classExperience: experience, hellParty: hell || legend, hellLegend: legend, practice: practice, crackRewards: crackRewards, revisedBeast: revisedBeast);
        }
        catch (Exception error) when (selection.DungeonIndex == 530)
        {
            log.Write("formal_dungeon_entry_exception", new { Connection = context.Id, Dungeon = selection.DungeonIndex,
                Request = selection, Error = error.GetType().Name, Message = error.Message, Stack = error.StackTrace });
            return await RejectAsync("formal-entry-exception");
        }
        log.Write("formal_dungeon_entry", new { Connection = context.Id, Accepted = receipt is not null, Replay = replay, Continuation = continuation,
            RevisedBeast = revisedBeast, Training = training, ClassExperience = experience, Practice = practice, HellParty = hell, HellLegend = legend,
            SealDoor = sealDoor && receipt?.Entry.HellParty is not null, Request = selection, receipt?.Snapshot.Identity,
            RequestedDifficulty = selection.Parameter, ActualDifficulty = receipt?.Entry.Difficulty,
            BypassedQuestTerrain = receipt?.Entry.BypassedQuestTerrain,
            RejectReason = receipt is null && !continuation && !replay ? context.Session.LastFormalDungeonRejection : null,
            HellTicket = receipt?.HellTicket, EntryItem = receipt?.Entry.EntryItem, StartFatigue = receipt?.Entry.StartFatigue,
            EntryFatigueCharged = receipt?.EntryFatigueCharged, EntryCostNote = receipt?.Entry.EntryCostNote,
            GriefLayer = receipt?.Entry.GriefLayer, Grief = receipt?.GriefAdmission?.Decision,
            Dazzlement = receipt?.Dazzlement,
            CharacterId = character.Id, CharacterName = character.Name });
        if (receipt is null) return await RejectAsync(!continuation && !replay ? context.Session.LastFormalDungeonRejection : null);
        await town.DetachAsync(context);
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        if (receipt.Entry.IsRevisedBeast) await RevisedBeastCountProjection.RefreshAsync(context, rooms, Guard);
        if (receipt.Dazzlement is not null)
            await DazzlementTowerProjection.BindingsAsync(context, Guard);
        if (receipt.Entry.Hope is { } hope)
            await context.Reply(CmdPacketType.ACCEPT_QUEST, QuestPackets.Accepted(hope.Acceptance.QuestId,1,[]),
                "operation_hope_quest_assigned",Guard);
        // 指定直入的15没有打开选图场景；先建立27上下文，28/29进入主游戏后由客户端安装临时体验技能。
        if (direct)
            await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionNotice(0, despairLayer: context.Session.DespairSelectionLayer()),
                experience ? "formal_class_experience_scene_transition" : "formal_training_scene_transition", Guard);
        if (receipt.SpecialEntry is { Kind: DungeonSpecialEntryKind.Despair, Layer: >= 10 and <= 90 } despair && despair.Layer % 10 == 0)
            await context.Notify(NotiPacketType.USER_APC_INFO_TOD, DungeonDespairPackets.LocalPlayerOpponent(despair.Layer),
                "formal_despair_local_opponent", Guard);
        if (receipt.Monsters.Altar is not null)
            await context.Notify(NotiPacketType.BLOOD_INFO, DungeonAltarPackets.Information(receipt.Entry), "formal_altar_information", Guard);
        else
            await context.Notify(NotiPacketType.DUNGEON_INFO, DungeonPackets.Information(receipt.Entry, receipt.Snapshot.BossPosition), "formal_dungeon_information", Guard);
        // 28建立活动实例后先安装卡组；1767启用原生谋略战加载过渡，不能等37加载完成才发送。
        if (context.MonsterMockBattle is { } mockBattleProjection) await mockBattleProjection.BattleInfoAsync(receipt, spawn: true);
        if (OrdinaryLukeProgress.Applies(receipt.Entry)) await OrdinaryLukeSequenceProjection.RefreshAsync(context, Guard);
        if (PreyNormalProgress.Applies(receipt.Entry)) await PreyNormalSequenceProjection.RefreshAsync(context, Guard);
        if (AntonNormalProgress.Applies(receipt.Entry)) await AntonNormalSequenceProjection.RefreshAsync(context, Guard);
        // 实验性（#259）：比赛副本先发 NOTI386 对阵/对手，再以 NOTI387 替代 NOTI29（依据来自 Go 项目反汇编，未经 V31 客户端验证）。
        if (receipt.Monsters.Altar is not null)
        {
            await MonsterAiProjection.SendAsync(context, receipt, "formal_altar_monster_ai", Guard);
            await context.Notify(NotiPacketType.START_BLOOD_MAP, DungeonAltarPackets.StartMap(receipt.Entry), "formal_altar_map", Guard);
        }
        else if (receipt.Entry.IsTournament && receipt.Monsters.Tournament is { } tournament)
        {
            await context.Notify(NotiPacketType.TOURNAMENT_INFO, DungeonPackets.TournamentInfo(receipt.Entry, tournament),
                "formal_tournament_info", Guard);
            await MonsterAiProjection.SendAsync(context, receipt, "formal_tournament_monster_ai", Guard);
            await context.Notify(NotiPacketType.TOURNAMENT_MAP_INFO, DungeonPackets.TournamentMapInfo(receipt.Entry, receipt.MapSeed),
                "formal_tournament_map_info", Guard);
        }
        else
        {
            await MonsterAiProjection.SendAsync(context, receipt, "formal_monster_ai", Guard);
            await context.Notify(NotiPacketType.START_MAP, DungeonPackets.StartMap(receipt.Entry, receipt.Monsters, receipt.MapSeed,
                passiveItems: receipt.PassiveItems), "formal_start_map", Guard);
            if (context.Session.FormalBossRushView() is { } rush)
            {
                await context.Notify(NotiPacketType.BOSS_RUSH_DUNGEON_INFO, BossRushPackets.DungeonInfo(rush.Rooms),
                    "formal_boss_rush_info", Guard);
                await context.Notify(NotiPacketType.BOSS_RUSH_DATA, BossRushPackets.Data(rush.Hud.Rank, rush.Hud.Score, rush.Hud.RemainMilliseconds),
                    "formal_boss_rush_data", Guard);
                await context.Notify(NotiPacketType.DUNGEON_TIMEOUT_TIME,
                    BossRushPackets.Timeout(Math.Max(1, (rush.Hud.RemainMilliseconds + 999) / 1000)),
                    "formal_boss_rush_timeout", Guard);
            }
        }
        if (DungeonPackets.HellPartyMonsterInfo(receipt.Monsters) is { } hellInfo)
            await context.Notify(NotiPacketType.HELL_PARTY_MONSTER_INFO, hellInfo, "formal_hell_party_monster_info", Guard);
        await QuestProjection.DungeonTerrainBypassAsync(context, receipt.Entry, Guard);
        await PublishEntryAssetsAsync(context, receipt, Guard);
        if (receipt.Entry.DungeonId == 530)
            await RiftDailyProjection.RefreshAsync(context, Guard, force: true);
        if (receipt.SpecialEntry?.Kind is DungeonSpecialEntryKind.Blood or DungeonSpecialEntryKind.Ultimate)
            await DungeonEnterCountProjection.RefreshAsync(context, dungeons, Guard);
        else if (receipt.GriefAdmission is not null)
            await DungeonEnterCountProjection.RefreshGriefAsync(context, dungeons, Guard);
        else if (receipt.SpecialEntry is { Kind: DungeonSpecialEntryKind.Despair })
            await DungeonEnterCountProjection.RefreshDespairAsync(context, dungeons, Guard);
        await BossSelectionBuffProjection.PostAsync(context, canSend: Guard);
        await HellLegendBuffProjection.PostAsync(context, canSend: Guard);
        if (receipt.Entry.Dimension is not null) await DimensionEntranceProjection.RefreshAsync(context, Guard, entranceOnly: false);
        if(receipt.BattlePassMissionClock is {} entryClock) await BattlePassMissionProjection.RefreshAsync(context,entryClock.GetUtcNow());
        return CommandResult.Handled;

        async Task<CommandResult> RejectAsync(string? entryReason = null, byte? memberSlot = null, bool party = false)
        {
            // 每一条被拒的选图都留原始请求体，实机排错不用再靠猜标志位。
            log.Write("formal_dungeon_entry", new { Connection = context.Id, Accepted = false, Replay = replay, Continuation = continuation,
                Training = training, ClassExperience = experience, Practice = practice, Request = selection,
                RejectReason = entryReason ?? context.Session.LastFormalDungeonRejection ?? "selection-rejected", MemberSlot = memberSlot, CharacterId = character.Id });
            bool CanSend() => context.Session.CanPublishFormalDungeonRejection(character, before.SelectionEpoch,
                before.Phase, before.CurrentDungeonSelection);
            // 先结束本次入场等待；普通选图保留原锚点，首期直入失败由下方恢复准备区。
            // 区域任务提示由NOTI27的未完成队员名单驱动；强制入场只恢复等待，不误用次数上限0xAD。
            string? hellReason = hell || legend ? entryReason : null;
            DungeonSelectionError? altarError = selection.DungeonIndex is 11006 or 11007 ? entryReason switch
            {
                "altar-entry-daily-limit" => DungeonSelectionError.DungeonLimitOrStorageReward,
                "altar-entry-insufficient-gold" => DungeonSelectionError.InsufficientGold,
                _ => null
            } : null;
            byte[] rejection = altarError is { } code ? DungeonPackets.SelectionRejected(code) :
                hellReason is { } why && why.StartsWith("hell-party-", StringComparison.Ordinal)
                ? DungeonPackets.HellPartyRejected(why.StartsWith("hell-party-quest-required:", StringComparison.Ordinal) ? DungeonSelectionError.Silent :
                    why == "hell-party-item-required" ? DungeonSelectionError.RequiredMaterials : DungeonSelectionError.NoAvailableDifficulty)
                : entryReason == "fatigue-exhausted" || entryReason?.StartsWith("start-fatigue-insufficient:", StringComparison.Ordinal) == true
                ? DungeonPackets.SelectionRejected(DungeonSelectionError.MemberFatigueOrClearCondition, memberSlot ?? 0)
                : entryReason?.StartsWith("entry-item-required:", StringComparison.Ordinal) == true ||
                  entryReason?.StartsWith("dungeon-required-item:", StringComparison.Ordinal) == true ||
                  entryReason?.StartsWith("channel-town-required-item:", StringComparison.Ordinal) == true ||
                  entryReason == "ancient-required-items-insufficient"
                ? DungeonPackets.SelectionRejected(party ? DungeonSelectionError.GroupMaterials : DungeonSelectionError.RequiredMaterials, memberSlot ?? 0)
                : DungeonPackets.SelectionRejected();
            await context.Reply(CmdPacketType.SELECT_DUNGEON, rejection, hell ? "formal_hell_party_rejected" : "formal_dungeon_rejected", CanSend);
            if (!party && selection.DungeonIndex is 5359 or 5363 &&
                entryReason?.StartsWith("strongway2019-", StringComparison.Ordinal) == true)
            {
                // ACK写出期间可能发生换角或接管，必须在同一owner内复核旧选择再消费返回锚点。
                var returned = context.Owner.Execute(() => context.Owner.IsCurrent && CanSend()
                    ? context.Session.ReturnFromDungeonSelection() : null);
                if (returned is not null)
                {
                    await town.ReturnFromDungeonSelectionAsync(context, returned);
                    await context.Notify(NotiPacketType.AAS_TIPS, ServerNoticePackets.Build("强者之路",
                        Strongway2019EntryRejectionText(entryReason)), "strongway2019_entry_rejected_notice",
                        () => context.Session.CanPublishDungeonSelection(returned, returning: true));
                }
            }
            return CommandResult.Handled;
        }
    }

    // 沿用统一入场已投影槽集合；只补剩余货币，按原序去重，不遗漏其它入场货币。
    internal static InventorySlot[] EntryCurrencySlots(IEnumerable<CurrencyKind> currencies, IReadOnlySet<short> projectedSlots) =>
        currencies.Select(ItemSlotBoundService.CurrencySlot).OfType<short>()
            .Where(slot => !projectedSlots.Contains(slot)).Distinct()
            .Select(slot => new InventorySlot(InventoryListType.Main, slot)).ToArray();

    async Task<CommandResult> LoadedAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 8 || request.Body.Any(value => value != 0)) return CommandResult.Unhandled;
        if (context.Session.PartyDungeon is { } party)
        {
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, party.Members);
            var completed = party.CompleteLoading(context.Session);
            foreach (var notice in completed) notice.Recipient.Actor.Session.ObserveStrongway2019RouteLoaded();
            log.Write("formal_dungeon_move", new { Connection = context.Id, Party = party.Id, LoadingReady = true, Released = completed.Length });
            await PartyDungeonProjection.PublishAsync(completed);
            if (strongwayProjection is not null) await strongwayProjection.LoadedAsync(completed);
            return CommandResult.Handled;
        }
        var receipt = context.Session.CompleteFormalDungeonLoading();
        if (receipt is null) return CommandResult.Unhandled;
        if (receipt.NavigationReplay) return CommandResult.Handled;
        bool published = false;
        bool Guard() => published = context.Session.CanPublishFormalDungeon(receipt);
        await context.Reply(CmdPacketType.FINISH_LOADING, [1], "formal_loading_ack", Guard);
        await context.Notify(NotiPacketType.FINISH_LOADING, DungeonPackets.LoadingNotice(), "formal_loading_notice", Guard);
        await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(receipt.Character.Id), "formal_player_state", Guard);
        RaidCombatProjection.PostPreyDungeonTime(context, receipt);
        // 进入副本重建Actor后补发网吧特权buff，否则buff栏在副本内是空的。
        await PcRoomBuffProjection.PostAsync(context, Guard);
        town.GuildEconomy?.Post(context, force: true);
        await GuildTrainingProjection.PostAsync(context);
        await BossSelectionBuffProjection.PostAsync(context, force: true, canSend: Guard);
        await HellLegendBuffProjection.PostAsync(context, force: true, canSend: Guard);
        if (receipt.ScriptedReturnId != Guid.Empty)
        {
            if (published) context.Session.ConfirmScriptedReturnLoading(receipt);
            return CommandResult.Handled;
        }
        if (context.MonsterMockBattle is { } mockBattle) await mockBattle.BattleInfoAsync(receipt, spawn: false);
        if (receipt.Entry.UsesFatigue)
            await context.Notify(NotiPacketType.FATIGUE, FatiguePackets.Current(context.Session.FatigueForClient(receipt.Character)), "formal_room_fatigue", Guard);
        if (!receipt.FatigueEnergySlots.IsEmpty)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, [.. receipt.FatigueEnergySlots]), "room_quick_slot_energy", Guard);
        if (context.Elevator is not null) await context.Elevator.StartAsync(receipt);
        if (context.LostLand is not null) await context.LostLand.StartAsync(receipt);
        if (context.Altar is not null && context.Session.StartDungeonAltar(receipt) is { } altar)
            await context.Altar.WakeAsync(altar);
        if (receipt.HellPartyRoomCleared)
            await context.Notify(NotiPacketType.HELL_PARTY_CLEAR, DungeonPackets.HellPartyCleared(), "formal_hell_party_clear", Guard);
        if (receipt.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement)
            await EnableSettlementAsync(context);
        await PublishEpicPieceShopAsync(context, prepare: false);
        if(receipt.BattlePassMissionClock is {} loadingClock) await BattlePassMissionProjection.RefreshAsync(context,loadingClock.GetUtcNow());
        return CommandResult.Handled;
    }

    async Task<CommandResult> DeathAsync(GameConnectionContext context, Packet request)
    {
        if (context.Session.PartyDungeon is not null) return await PartyDeathAsync(context, request);
        if (DungeonRequests.TryParsePassiveDeath(request.Body, out var passive))
        {
            var progress = context.Session.ObserveFormalPassiveDeath(passive!.ObjectIdentifier,
                passive.ChecksumMatches,Convert.ToHexString(SHA256.HashData(request.Body)),rooms);
            log.Write("formal_passive_death", new { Connection = context.Id, Accepted = progress?.Commit is { ProjectionCurrent: true },
                progress?.Phase, progress?.DungeonIdentity, Request = passive,
                Reason = progress is null ? context.Session.DiagnosePassiveEvent(passive.ObjectIdentifier,rooms) : null });
            if (progress?.Commit is { ProjectionCurrent: false }) return CommandResult.CloseConnection;
            if (progress?.Commit is not null)
            {
                if (progress.Commit.Plan.Inventory.ChangedSlots.Length > 0)
                    await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(progress.Character.Inventory,
                        InventoryListType.Main,[.. progress.Commit.Plan.Inventory.ChangedSlots]),"formal_passive_quest_items",
                        () => context.Session.CanPublishQuestLifecycle(progress));
                await QuestProjection.ProgressAsync(context,progress.Commit.Plan.Expected.ActiveMainQuests,progress.Character,
                    () => context.Session.CanPublishQuestLifecycle(progress),"formal_passive_quest_conditions");
                await EnableSettlementAsync(context);
            }
            return CommandResult.Handled;
        }
        var previousCharacter = context.Session.Selected;
        FormalDungeonDeathResult result;
        uint objectKey;
        object audit;
        if (DungeonRequests.TryParseScriptedDeath(request.Body,out var scripted))
        {
            objectKey = scripted!.ObjectKey;
            audit = scripted;
            var observation = new NativeDungeonScriptedDeathObservation(objectKey,scripted.RewriteFlag,
                scripted.ChecksumMatches,Convert.ToHexString(SHA256.HashData(request.Body)));
            if (context.Session.HasDungeonAltar)
                return await PublishAltarMonsterDeathAsync(context,objectKey,context.Session.ObserveDungeonAltarScriptedDeath(observation, growth));
            result = context.Session.ObserveFormalDungeonScriptedDeath(observation,rewards,growth);
        }
        else
        {
            if (!DungeonRequests.TryParseDeath(request.Body,out var parsed))
                return CommandResult.Unhandled;
            if (parsed!.SinglePlayerStatistics is not { } statistics)
                return await PublishDeathAsync(context,new(DungeonRunOutcome.Rejected),previousCharacter,parsed.ObjectKey,parsed);
            objectKey = parsed.ObjectKey;
            var part = statistics.Participant;
            var observation = new NativeDungeonDeathObservation(objectKey,parsed.Reporter,part.Kind,part.ObjectKey,
                part.CumulativeHpDecrease,part.PositiveHpDecreaseCount,statistics.RewriteFlag,statistics.SpawnLevel,
                statistics.SpawnHpSnapshot,statistics.Unknown18,statistics.Unknown26,statistics.ChecksumMatches,
                Convert.ToHexString(SHA256.HashData(request.Body)))
            { Participants = statistics.Participants.Select(value => new NativeDungeonDeathParticipant(value.Kind,value.ObjectKey,
                value.CumulativeHpDecrease,value.PositiveHpDecreaseCount)).ToImmutableArray() };
            if (context.Session.HasDungeonAltar)
                return await PublishAltarMonsterDeathAsync(context,objectKey,context.Session.ObserveDungeonAltarDeath(observation, growth));
            audit = observation;
            result = context.Session.ObserveFormalDungeonDeath(observation,rewards,growth);
        }
        if (result.PendingImperialArenaDeath is { } pendingImperial)
        {
            if (imperialArena is null) throw new InvalidOperationException("帝国竞技场缺少奖励提交服务");
            result = imperialArena.Commit(context, pendingImperial) ?? throw new InvalidOperationException("帝国竞技场奖励规划期间会话已失效");
        }
        if (result.PendingTagEvildomClear is { } pendingTag)
        {
            if (tagEvildomRun is null) throw new InvalidOperationException("正式TAG终点缺少奖励提交服务");
            result = tagEvildomRun.Commit(context, pendingTag) ?? throw new InvalidOperationException("TAG终点规划期间原会话已失效");
        }
        return await PublishDeathAsync(context, result, previousCharacter, objectKey, audit);
    }

    async Task<CommandResult> PublishDeathAsync(GameConnectionContext context, FormalDungeonDeathResult result,
        Character? previousCharacter, uint objectKey, object audit)
    {
        var target = result.ReportedTarget;
        log.Write("formal_dungeon_death", new { Connection = context.Id, Recognized = true, context.Session.Phase,
            result.Outcome, ObjectKey = objectKey, Report = audit,
            result.EquipmentRarityBonus.RarePercent, result.EquipmentRarityBonus.UniquePercent,
            result.EpicPieceBonus,
            Target = target is null ? null : new { target.MonsterId, target.SpawnType, target.Team, target.IsFriendly, target.IsBlocking, target.CanReportDeath },
            result.Receipt?.Snapshot.Identity, result.Reward, result.GainedExperience, result.ProficiencyGained, result.ClearFact, result.RoomClearFact,
            DirectItems = result.DirectPickup.GroundKeys.Length });
        if (result.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate))
            return CommandResult.Handled;
        if (result.ClearFact is { } strongwayClear && context.Session.CurrentStrongway2019Match is { } match &&
            context.Session.PartyDungeon is { } strongwayParty &&
            context.Session.CaptureStrongway2019Run(strongwayParty) is { } strongwayRun)
        {
            match.Competition.ObserveClear(strongwayRun, strongwayRun.Scene.Identity, strongwayClear);
            if (strongwayProjection is not null) await strongwayProjection.TerminalAsync(match);
        }
        else if (strongwayRewards?.Commit(context) is { } strongwayReward)
            await strongwayRewards.CommittedAsync(context, strongwayReward);
        if (result.Receipt is not { } receipt)
        {
            if (result.Outcome == DungeonRunOutcome.Duplicate && context.Session.CurrentTagEvildomClear() is { } clearedTag)
            {
                if (tagEvildomRun is not null) await tagEvildomRun.ClearAsync(context, clearedTag);
            }
            else if (result.Outcome == DungeonRunOutcome.Duplicate && context.Session.CurrentFormalDungeonScene()?.Entry.IsRevisedBeast == true)
            {
                if (items is not null && goldLimits is not null && pieces is not null)
                {
                    if (context.Session.Phase == SessionPhase.Dungeon)
                        await RevisedBeastProjection.PublishAnneAsync(context, items, goldLimits, pieces, log, rooms);
                    else await RevisedBeastProjection.PublishAsync(context, items, goldLimits, pieces, log, rooms);
                }
            }
            else if (result.Outcome == DungeonRunOutcome.Duplicate) await EnableSettlementAsync(context, result.MonsterKey);
            return CommandResult.Handled;
        }
        if (result.SpecialChildRetired)
        {
            if (result.Elevator is { } elevator)
                await context.Notify(NotiPacketType.ELEVATOR_CLEAR_TIME_CHECK, DungeonPackets.Elevator(elevator.Notice!),
                    "formal_elevator_completed", () => context.Session.CanPublishDungeonElevator(elevator));
            await context.Notify(NotiPacketType.DIE_MONSTER, ExclusiveDropPackets.MonsterDeath(result.MonsterKey, [], checked((int)receipt.Entry.DungeonId)),
                "formal_special_child_retired", () => context.Session.CanPublishFormalDungeon(receipt));
            return CommandResult.Handled;
        }
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        if (context.Session.RecordLukeNamedDeath(result) is { } namedChange) context.Session.PublishRaidCombat(namedChange);
        // 02B0FA96读取第二个u16作为可见性过滤；普通掉落显式为0，位置由客户端死亡对象散落链确定。
        if (receipt.Character.Marriage is { State: 4 } marriage &&
            marriage.Members.TryGetValue(receipt.Character.Id, out var member) &&
            previousCharacter?.Marriage?.Members.GetValueOrDefault(receipt.Character.Id)?.CapsuleGauge != member.CapsuleGauge)
            await context.Notify((NotiPacketType)1019, MarriageCouplePackets.CapsuleGauge(member.CapsuleGauge), "marriage_capsule_clear_gauge", Guard);
        var grounds = result.Drops;
        if (grounds.IsEmpty && result.Drop is { } single)
            grounds = [single];
        DungeonDropPacketItem[] dropRows = grounds.Select(drop => new DungeonDropPacketItem(drop.Reservation.GroundKey,
            drop.Reservation.RecordSlot, drop.Content.Item, 0, 0) { GoldAmount = drop.GoldAmount }).ToArray();
        int enteredDungeon = checked((int)receipt.Entry.DungeonId);
        if (receipt.Entry.IsRevisedBeast && receipt.Entry.RevisedBeastEncounter is { } encounter &&
            target is not null && !encounter.UsesOrdinaryBossDeath(target.MonsterId))
            await context.Reply(CmdPacketType.DIE_MONSTER, RevisedBeastPackets.RetireAnne(result.MonsterKey), "revised_beast_anne_retired", Guard);
        else
            await context.Notify(NotiPacketType.DIE_MONSTER,
                ExclusiveDropPackets.MonsterDeath(result.MonsterKey, dropRows, enteredDungeon, result.BossPositionUpdate), "formal_monster_death", Guard);
        if (receipt.Entry.IsRevisedBeast)
        {
            if (items is not null && goldLimits is not null && pieces is not null)
            {
                if (receipt.Snapshot.Phase == SinglePlayerDungeonPhase.Dungeon)
                    await RevisedBeastProjection.PublishAnneAsync(context, items, goldLimits, pieces, log, rooms);
                else if (receipt.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement)
                    await RevisedBeastProjection.PublishAsync(context, items, goldLimits, pieces, log, rooms);
            }
            return CommandResult.Handled;
        }
        if (result.BossRushHud is { } hud)
            await context.Notify(NotiPacketType.BOSS_RUSH_DATA, BossRushPackets.Data(hud.Rank, hud.Score, hud.RemainMilliseconds),
                "formal_boss_rush_data", Guard);
        if (receipt.HellPartyRoomCleared)
            await context.Notify(NotiPacketType.HELL_PARTY_CLEAR, DungeonPackets.HellPartyCleared(), "formal_hell_party_clear", Guard);
        if (context.Session.PendingDungeonBossEntrance() is { } entrance)
        {
            bool sent = false;
            await context.Notify(NotiPacketType.COMPLETE_CONDITION_PASS_GATE,DungeonPackets.BossEntranceOpened(),"formal_boss_entrance_opened",
                () => sent = context.Session.CanPublishFormalDungeon(entrance));
            if (sent) context.Session.ConfirmDungeonBossEntrance(entrance);
        }
        // 友方、非阻挡对象只应答退场，不发布经验、任务或结算；未知和已结算敌人仅审计。
        if (result.Reward is not { } reward)
        {
            foreach (ushort key in result.CulledMonsterKeys)
                await context.Notify(NotiPacketType.DIE_MONSTER, ExclusiveDropPackets.MonsterDeath(key, [], enteredDungeon), "formal_clear_room_cull", Guard);
            if (result.ImperialArenaDeath is { } imperialDeath)
            {
                if (imperialArena is null) throw new InvalidOperationException("帝国竞技场已提交奖励缺少投影服务");
                await imperialArena.DeathAsync(context, imperialDeath, Guard);
                if(result.BattlePassMissionClock is {} imperialClock && Guard())
                    await BattlePassMissionProjection.RefreshAsync(context,imperialClock.GetUtcNow());
            }
            else if (result.TagEvildomClear is { } clearedTag)
            {
                if (tagEvildomRun is null) throw new InvalidOperationException("TAG已提交结果缺少投影服务");
                await tagEvildomRun.ClearAsync(context, clearedTag);
            }
            else if (result.ClearFact is not null)
                await EnableSettlementAsync(context, result.MonsterKey);
            await PublishEpicPieceShopAsync(context, prepare: true);
            return CommandResult.Handled;
        }
        var state = receipt.Character.DungeonState ?? throw new InvalidOperationException("已提交角色缺成长附属owner");
        var previousLevel = result.PreviousLevel ?? throw new InvalidOperationException("已提交死亡缺少原实际等级");
        var auxiliary = new CharacterGrowthAuxiliaryState(checked((uint)receipt.Character.Inventory.WinPoints), state.GrowthCapsuleExperience,
            state.ProficiencyLevel, state.ProficiencyExperience);
        uint plainKill = reward.Amount - reward.GrowthContractExperience - reward.PotionExperience;
        // 满级后角色经验为 0，击杀经验全额记入熟练度；显示与秘药判定都用两者之和的有效经验。
        uint effective = checked(result.GainedExperience + result.ProficiencyGained);
        var display = reward.Bonuses?.Display(effective) ?? CharacterExperienceDisplay.Ordinary with
        { GrowthContractExperience = GrowthContractPolicy.AppliedBonus(plainKill, reward.GrowthContractExperience, effective),
            PotionExperience = ExpBonusPolicy.Shown(plainKill, reward.GrowthContractExperience, reward.PotionExperience, effective) };
        foreach (ushort key in result.CulledMonsterKeys)
            await context.Notify(NotiPacketType.DIE_MONSTER, ExclusiveDropPackets.MonsterDeath(key, [], enteredDungeon), "formal_clear_room_cull", Guard);
        // 秘药分量非 0 时，客户端会先拼出经验明细。31 若排在这条 NOTI37 之后，02D23170 看到明细窗口仍在就跳过结算锁存。
        // 普通击杀仍保持 37 然后 115/31。只有带秘药的通关击杀把许可提前到经验通知之前。
        // 绝望348会提交资产/称号并替换角色快照，必须在本次击杀成长投影之后；普通31许可不替换快照。
        bool potionClear = !receipt.Entry.IsDespair && result.ClearFact is not null && display.PotionExperience > 0;
        if (potionClear)
            await EnableSettlementAsync(context, result.MonsterKey);
        if (CharacterExperienceNotice.ShouldPublish(previousLevel))
            await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(receipt.Character.Level, receipt.Character.Learning, auxiliary, display), "formal_kill_experience", Guard);
        // 直入包只发布已提交的真实实例差量；不发送依赖既有地面对象的NOTI39或伪造拾取ACK。
        var directCurrencySlots = result.DirectPickup.ChangedCurrencies.Select(ItemSlotBoundService.CurrencySlot)
            .Where(slot => slot.HasValue).Select(slot => new InventorySlot(InventoryListType.Main, slot!.Value));
        foreach (var changed in result.QuestDropSlots.Concat(result.DirectPickup.ChangedSlots).Concat(directCurrencySlots)
            .Distinct().GroupBy(slot => slot.List))
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                changed.Key, [.. changed]), "formal_death_items", Guard);
        if (result.DirectPickup.ChangedCurrencies.Any(kind => kind is >= CurrencyKind.Cera and <= CurrencyKind.HappyTokenCera))
            await context.Notify(NotiPacketType.CERA, CurrencyPackets.Cera(receipt.Character.Inventory.AccountWallet),
                "formal_direct_currency", Guard);
        if (!result.DirectPickup.EpicPieceIndexes.IsEmpty && pieces is not null)
            await context.Notify(NotiPacketType.EPIC_BOOK_INFO,
                EpicBookPackets.Info(receipt.Character.Inventory.EpicPieces, pieces, result.DirectPickup.EpicPieceIndexes),
                "formal_direct_epic_pieces", Guard);
        if (result.DirectPickup.AccountMaterialsChanged)
            await town.AccountMaterialsAsync(context, receipt.Character, canSend: Guard);

        foreach (var change in result.EquipmentChanges.Where(change => change.Before.Core.ItemId == change.After.Core.ItemId))
            await context.Notify(NotiPacketType.GROWTH_ITEM_RENEW_DATA, EquipmentGrowthPackets.Experience(change.After, change.Gain),
                "equipment_growth_experience", Guard);
        var evolved = result.EquipmentChanges.Where(change => change.Before.Core.ItemId != change.After.Core.ItemId).Select(change => change.After).ToArray();
        if (evolved.Length > 0)
        {
            await context.Notify(NotiPacketType.GROWTH_ITEM_CHANGE_DATA, EquipmentGrowthPackets.State(evolved, true),
                "equipment_growth_evolved", Guard);
            if (inventoryProjection is not null)
                await inventoryProjection.RefreshCharacterAsync(context, receipt.Character, "equipment_growth", Guard);
        }
        if (previousCharacter is not null && previousCharacter.Level == receipt.Character.Level &&
            SkillPackets.BindingsChanged(previousCharacter.Learning, receipt.Character.Learning))
            await SkillProjection.PublishAsync(context, receipt.Character, "formal_growth_skills", "formal_growth_combo", Guard);
        if (previousCharacter is not null)
            await SkillSwitchInventoryProjection.RefreshStateAsync(
                context,
                previousCharacter.SkillSwitchInventory,
                previousCharacter.Inventory,
                receipt.Character,
                Guard,
                "formal_growth_skill_switch_inventory");
        await levelProjection.RefreshAsync(context, receipt.Character,
            previousLevel, "formal_level_committed", Guard,
            previousLearning: previousCharacter?.Learning, adventureProgress: result.Adventure);
        bool closed = quests is not null && await quests.CloseReadyChildrenAsync(context);
        if (!closed && result.PreviousQuests is { } previous)
        {
            var changed = receipt.Character.ActiveMainQuests.Where(row => previous.Any(old =>
                old.AcceptanceId == row.AcceptanceId && old.ConditionState != row.ConditionState)).ToArray();
            var hope = changed.SingleOrDefault(row => context.Session.IsOperationHopeQuest(row.QuestId));
            var ordinary = changed.Where(row => row != hope).ToArray();
            await QuestProjection.PublishConditionsAsync(context,ordinary,Guard,"formal_hunt_conditions");
            // 574先清空再重新接取，不能替代33的原位进度/完成回调；队伍成员也复用本死亡投影。
            if (hope is not null)
                await context.Reply(CmdPacketType.SET_QUEST_TRIGGER, QuestPackets.ClearTrigger(hope.QuestId, hope.ConditionState),
                    "operation_hope_hunt_condition", Guard);
        }
        if (result.ClearFact is not null && !potionClear)
            await EnableSettlementAsync(context, result.MonsterKey);
        await PublishEpicPieceShopAsync(context, prepare: true);
        if(result.BattlePassMissionClock is {} deathClock) await BattlePassMissionProjection.RefreshAsync(context,deathClock.GetUtcNow());
        return CommandResult.Handled;
    }

    async Task<CommandResult> PickupAsync(GameConnectionContext context, Packet request)
    {
        if (items is null || pieces is null || !DungeonPickupRequests.TryParseOrdinary(request.Body, out var parsed))
            return CommandResult.Unhandled;
        if (context.Session.PartyDungeon is not null) return await PartyPickupAsync(context, parsed.GroundObjectKey);
        var result = context.Session.PickFormalDungeonItem(parsed.GroundObjectKey, items, pieces, goldLimits);
        if (result.Outcome != DungeonRunOutcome.Accepted || result.Receipt is not { } receipt || result.Commit is not { } commit)
        {
            // 22显示空间/携带上限不足；无效key、重复或错误阶段沿19静默清除等待。
            await context.Reply(CmdPacketType.GET_ITEM, DungeonPickupPackets.Rejected(result.CapacityRejected ? (ushort)22 : (ushort)19),
                "formal_pickup_rejected");
            return CommandResult.Handled;
        }
        if (!commit.ProjectionCurrent) return CommandResult.CloseConnection;
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        var destination = commit.Currency is { } kind
            ? new InventorySlot(InventoryListType.Main, CurrencyPackets.PickupSlot(kind)) : commit.Destination;
        // 总额来自已提交回执，附加额仅拆分展示，不再放大地面金币或余额。
        var notice = commit.Currency == CurrencyKind.Gold
            ? DungeonPickupPackets.SinglePlayerGoldNotification(parsed.GroundObjectKey, receipt.Character.Id, checked((uint)commit.Amount),
                checked((uint)(commit.Amount - result.Ground!.GoldAmount)))
            : DungeonPickupPackets.OrdinaryNotification(parsed.GroundObjectKey,
                receipt.Character.Id, new byte[8], receipt.Character.Id,
                commit.EpicPiece is not null || commit.AccountMaterial is not null
                    ? DungeonPickupPackets.NoInventorySlot : checked((ushort)destination.Index), 0);
        await context.Notify(NotiPacketType.GET_ITEM, notice, "formal_pickup_notice", Guard);
        if (commit.EpicPiece is { } fragment)
        {
            await context.Notify(NotiPacketType.EPIC_BOOK_INFO,
                EpicBookPackets.Info(receipt.Character.Inventory.EpicPieces, pieces, [fragment.Index]), "formal_pickup_epic_piece", Guard);
            log.Write("epic_piece_pickup", new { Connection = context.Id, Character = receipt.Character.Id,
                Ground = parsed.GroundObjectKey, fragment.PieceId, fragment.Amount, fragment.Balance, fragment.BookRevision, commit.Duplicate });
        }
        else if (commit.AccountMaterial is not null)
            await town.AccountMaterialsAsync(context, receipt.Character, canSend: Guard);
        else
        {
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                destination.List, [destination]), "formal_pickup_inventory", Guard);
            if (destination.List == InventoryListType.AccountCommon &&
                receipt.Character.Inventory.Find(destination) is { } created && created.Core.Count == commit.Amount)
                await town.SyncCommonCalendarSlotsAsync(context,
                    receipt.Character.Inventory with { Items = receipt.Character.Inventory.Items.Remove(created) },
                    receipt.Character, Guard);
        }
        await context.Reply(CmdPacketType.GET_ITEM, DungeonPickupPackets.Accepted(), "formal_pickup_ack", Guard);
        if (result.PreviousQuests is { } previousQuests)
            await QuestProjection.ProgressAsync(context,previousQuests,receipt.Character,Guard,"formal_pickup_quest_conditions");
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
        if (context.Session.PartyDungeon is { } party)
        {
            bool strongway = context.Session.CurrentStrongway2019Match is not null && strongwayRoute is not null;
            // 换图服务自行取得原四个连接门；先占peer门会使其零等待复验必然失败。
            using var peers = strongway ? null : await PartyDungeonProjection.AcquirePeersAsync(context, party.Members);
            var moved = strongway
                ? strongwayRoute!.Move(context.Session, move.X, move.Y, move.Unknown10,
                    gate => context.DispatchGates?.Holds(context, gate) == true)
                : party.Move(context.Session, move.X, move.Y, move.Unknown10);
            log.Write("formal_dungeon_move", new { Connection = context.Id, Party = party.Id, Accepted = moved.Length > 0, move.X, move.Y });
            if (moved.Length == 0)
            {
                await context.Reply(CmdPacketType.MOVE_MAP, DungeonPackets.MoveRejected(), "party_move_rejected");
                return CommandResult.Handled;
            }
            await PartyDungeonProjection.PublishAsync(moved);
            return CommandResult.Handled;
        }
        var rotation = context.Session.RotateHelperHell(move.X, move.Y, move.Unknown10, dungeons, rooms, drops, items,
            pieces, prepareDrops);
        if (rotation.Attempted)
        {
            await PublishHelperHellRotationAsync(context, rotation.Receipt);
            return CommandResult.Handled;
        }
        var receipt = context.Session.MoveFormalDungeon(move.X, move.Y, move.Unknown10);
        log.Write("formal_dungeon_move",new { Connection = context.Id, Accepted = receipt is not null,
            move.X, move.Y, Kind = move.Unknown10, receipt?.Snapshot.CurrentMap, receipt?.Snapshot.MapOperation, receipt?.Snapshot.MapPayload });
        if (receipt is null)
        {
            // 深渊波次尚存时客户端可能已开门；静默拒绝会让它永久等待本次过图。
            await context.Reply(CmdPacketType.MOVE_MAP, DungeonPackets.MoveRejected(), "formal_move_rejected");
            return CommandResult.Handled;
        }
        if (receipt.NavigationReplay) return CommandResult.Handled;
        bool published = false;
        bool Guard() => published = context.Session.CanPublishFormalDungeon(receipt);
        await context.Reply(CmdPacketType.MOVE_MAP, DungeonPackets.MoveAccepted(), "formal_move_ack", Guard);
        await MonsterAiProjection.SendAsync(context, receipt, "formal_monster_ai", Guard);
        if (receipt.Monsters.Altar is not null)
        {
            if (context.Altar is not null) await context.Altar.StopAsync();
            await context.Notify(NotiPacketType.START_BLOOD_MAP,
                DungeonAltarPackets.StartMap(receipt.Entry, receipt.Snapshot.MapPayload), "formal_altar_next_map", Guard);
            return CommandResult.Handled;
        }
        await context.Notify(NotiPacketType.START_MAP, DungeonPackets.StartMap(receipt.Entry, receipt.Monsters, receipt.MapSeed,
            receipt.Snapshot.MapOperation,receipt.Snapshot.MapPayload, receipt.PassiveItems), "formal_next_map", Guard);
        if (DungeonPackets.HellPartyMonsterInfo(receipt.Monsters) is { } hellInfo && receipt.Snapshot.MapPayload != DungeonMapPayload.Cached)
            await context.Notify(NotiPacketType.HELL_PARTY_MONSTER_INFO, hellInfo, "formal_hell_party_monster_info", Guard);
        if (published && receipt.ScriptedReturnId != Guid.Empty) context.Session.ConfirmScriptedReturnPresentation(receipt);
        return CommandResult.Handled;
    }

    async Task<CommandResult> RidableOperationAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.TryParseRidableOperation(request.Body,out var operation))
            return CommandResult.Unhandled;
        if (context.Session.PartyDungeon is { } party)
        {
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context,party.Members);
            return party.OperateRidable(context.Session,operation.Operation,operation.ResourceId,operation.MapX,operation.MapY)
                ? CommandResult.Handled : CommandResult.Unhandled;
        }
        var result = context.Session.OperateFormalRidable(operation.Operation,operation.ResourceId,operation.MapX,operation.MapY);
        bool accepted = result?.Outcome is DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate;
        log.Write("formal_ridable_operation",new { Connection = context.Id, Accepted = accepted, operation.Operation,
            operation.ResourceId, operation.MapX, operation.MapY, result?.Outcome, result?.Snapshot.Identity });
        // 原生本地状态机已执行操作；不伪造ACK或重复创建MAP已有的骑乘演员。
        return accepted ? CommandResult.Handled : CommandResult.Unhandled;
    }

    async Task<CommandResult> ReturnAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0) return CommandResult.Unhandled;
        await EndStrongway2019Async(context);
        var party = context.Session.PartyDungeon;
        using var peers = party is null ? null : await PartyDungeonProjection.AcquirePeersAsync(context, party.Members);
        if (party is not null)
        {
            return await PartyDungeonProjection.DepartAsync(context, partyDungeons, parties,
                () => context.Reply(CmdPacketType.GIVEUP_GAME, DungeonPackets.GiveUpAccepted(), "formal_giveup_ack"));
        }
        bool tag = context.Session.HasFormalTagEvildom;
        if (context.Session.CurrentFormalDungeonScene()?.Entry.IsRevisedBeast == true && items is not null && goldLimits is not null && pieces is not null)
            await RevisedBeastProjection.PublishAsync(context, items, goldLimits, pieces, log, rooms);
        var receipt = context.Session.ReturnFromFormalDungeon();
        if (receipt is null) return CommandResult.Unhandled;
        if (context.LostLand is not null) await context.LostLand.StopAsync();
        if (context.Altar is not null) await context.Altar.StopAsync();
        if (context.Death is not null) await context.Death.StopAsync();
        bool Guard() => context.Session.CanPublishTravel(receipt);
        await context.Reply(CmdPacketType.GIVEUP_GAME, DungeonPackets.GiveUpAccepted(), "formal_giveup_ack", Guard);
        await town.ReturnFromFormalDungeonAsync(context, receipt, "formal_return_town");
        if (tag && context.Session.ChannelEntry?.Type == 39 && imperialArena is not null) await imperialArena.ReturnAsync(context, receipt);
        else if (tag && tagEvildomRun is not null) await tagEvildomRun.ReturnAsync(context, receipt);
        log.Write("formal_dungeon_return", new { Connection = context.Id, Reason = "giveup", receipt.Character.Id, receipt.Character.Position });
        return CommandResult.Handled;
    }

    async Task EndStrongway2019Async(GameConnectionContext context)
    {
        if (strongwayProjection is not null)
        {
            await strongwayProjection.BeforeDepartureAsync(context);
            return;
        }
        if (context.Session.CurrentStrongway2019Match is not { } match) return;
        if (context.Session.PartyDungeon is { } party && context.Session.CaptureStrongway2019Run(party) is { } run)
            match.Competition.Exit(run, run.Scene.Identity);
        strongwayMatching?.Detach(context.Session);
    }

    internal async Task DepartStrongway2019Async(GameConnectionContext context, Func<SemaphoreSlim, bool>? holdsCommandGate = null)
    {
        await EndStrongway2019Async(context);
        var party = context.Session.PartyDungeon;
        using var peers = party is null ? null : await PartyDungeonProjection.AcquirePeersAsync(context, party.Members,
            holdsCommandGate: holdsCommandGate);
        if (party is not null) await PartyDungeonProjection.DepartAsync(context, partyDungeons, parties, () => Task.CompletedTask);
    }
}
