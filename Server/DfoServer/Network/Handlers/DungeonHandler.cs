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
    DfoServer.Game.Raids.AntonRaidDungeonService? antonRaid = null,
    EvildomWarRewardPolicy? evildomRewards = null, IEvildomWarRewardStore? evildomRewardStore = null,
    ImperialArenaProjection? imperialArena = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
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
            if (context.Death is not null)
            {
                if (receipt.AutomaticRevival || altar || context.Session.PartyDungeon is not null) await context.Death.StopAsync();
                else await context.Death.StartAsync();
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
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(receipt.Character.ActiveMainQuests),
                "formal_revive_quest_conditions", Guard);
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
        if (character is not null && before.Ready && before.Phase == SessionPhase.Loading &&
            context.Session.ChannelEntry?.Type == 23 && antonRaid is { Enabled: true } &&
            (context.Session.StartedRaid is not null || selection.DungeonIndex <= int.MaxValue && antonRaid.Contains((int)selection.DungeonIndex)))
            return await SelectAntonAsync(context, selection);
        if (character is null || !before.Ready || before.Phase is not (SessionPhase.Town or SessionPhase.MapSelect) ||
            context.BlocksTownActions)
            return CommandResult.Unhandled;
        if (context.Session.ChannelEntry?.Type == 23 && (context.Session.StartedRaid is not null ||
            selection.DungeonIndex <= int.MaxValue && antonRaid?.Contains((int)selection.DungeonIndex) == true))
            return await SelectAntonAsync(context, selection);
        if (context.Session.ChannelEntry?.Type == 32 && context.Session.StartedRaid is not null)
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
            bool valid = lukeRaid is not null && partyDungeons is not null && context.Session.HasLukeRaidSelection &&
                selection.Parameter == 2 && selection.Unknown5 == 0 && selection.Unknown7 == 0 && selection.Unknown8 == 0 &&
                selection.Unknown9 == ushort.MaxValue && selection.Unknown11 == 0 && selection.Unknown15 == 0 &&
                selection.Unknown16 == 0 && selection.Unknown20 == 0 && selection.Unknown21 == 0;
            PartyDungeonNotice[] entered = [];
            try
            {
                if (valid) entered = partyDungeons!.Enter(context.Session, selection.DungeonIndex, selection.Parameter,
                    dungeons, rooms, null, null, null, null);
            }
            finally
            {
                // SQL失败也仅撤销本次选择；已安装Run的选择清理是空操作，不能释放成功的占用。
                if (entered.Length == 0) lukeRaid?.CancelSelection(context.Session, selectedParty);
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
        // 魔兽第六难度 = 难度4 + MAZE2（用户 2026-10-28 口径：MAZE 0/1/2 = 普通/深渊/魔兽拓扑）。
        // 实机 2026-10-25T14:49:06 的副本310/难度4/[7]=0 是普通噩梦按钮；MAZE1 留给下面的深渊分支。
        bool mazeBeast = BeastDungeonRotation.MatchesEntry(checked((int)selection.DungeonIndex), selection.Parameter, selection.Unknown7) &&
            selection.Unknown8 == 0 && !direct && !continuation;
        // 实机样本 2026-10-28T10:54:58（魔兽城镇51/2点气球）：
        // 5336 难度2 [7]=0 [8]=0 [9]=FFFF [16]=10000。难度由客户端选图面板随确认包直接下发，
        // [16] 是角色当时进行中的魔兽材料任务10000；两者都不是入场条件，此前按「普通难度0、无任务」写的判定把每一次点击都拒了。
        bool gatewayBeast = BeastDungeonRotation.IsGateway(selection.DungeonIndex) && selection.Unknown7 == 0 &&
            selection.Unknown8 == 0 && !direct && !continuation &&
            context.Session.ChannelEntry?.Type == ChannelAreaLanding.BeastType;
        bool beastMode = mazeBeast || gatewayBeast;
        bool hell = selection.Unknown7 == 1 && selection.Unknown8 == 0 && !direct && !continuation && !beastMode;
        // 03BBA474..03BBA48C把目标匹配结果写offset20；offset7仍是普通挑战深渊模式1。
        bool legend = hell && selection.Unknown20 == 1 &&
            before.CurrentDungeonSelection?.HellLegendDungeonId == selection.DungeonIndex;
        if (hell && before.CurrentDungeonSelection?.HellLegendDungeonId == selection.DungeonIndex && !legend)
            return await RejectAsync();
        // 实验性/默认关闭：密门。90 参考只解析 [seal door appear rate]，没有运行时；这里的最小方案 = 普通入场（questId=0）时按 rate% 概率把该房间换成深渊图并走深渊入场（不扣票、不查深渊任务）。
        // 仅当 DFO_SEAL_DOOR=1 时启用；rate 按百分数解读（推断）。随机失败/入场被拒时回退为普通入场。
        bool sealDoor = false;
        if (practice && selection.Unknown7 != 0) return await RejectAsync();
        if (SealDoorEnabled && !practice && !hell && !legend && !direct && !continuation && selection.Unknown7 == 0 && selection.Unknown8 == 0 && selection.Unknown16 == 0 &&
            selection.DungeonIndex != 5000 && selection.DungeonIndex != 7142 && SealDoorRate(selection.DungeonIndex) is > 0 and var sealRate &&
            System.Security.Cryptography.RandomNumberGenerator.GetInt32(100) < sealRate)
            sealDoor = true;
        if (continuation && (direct || replay || practice)) return await RejectAsync();
        // 练习与两个直入共享offset8=1；非零任务号仍须经过1459的剧情回放授权。
        if (selection.DungeonIndex == 5000 && (!training || selection.Parameter != 0)) return await RejectAsync();
        if (selection.DungeonIndex == 7142 && (!experience || selection.Parameter != 0)) return await RejectAsync();
        // 回放由1459的完成事实/显式路线授权；offset21是活动关联值，仅保留审计，不作为普通入场资格。
        // 魔兽入场由 [7]=MAZE2 表示，深渊由 [7]=1 表示，普通噩梦仍是 [7]=0；三者不能互相吞并。
        // 末位活动关联值也可能出现在普通及祭坛请求；特殊模式仍须通过各自的专用入场许可。
        byte expectedFlag = mazeBeast ? selection.Unknown7 : (byte)(hell ? 1 : 0);
        if (selection.Unknown5 != 0 || selection.Unknown11 != 0 || !replay &&
            (selection.Unknown7 != expectedFlag || selection.Unknown8 != (direct || practice ? 1 : 0) || selection.Unknown9 != ushort.MaxValue ||
            selection.Unknown15 != 0 || selection.Unknown20 != (legend ? 1 : 0)))
            return await RejectAsync();
        bool multiplePartyMembers = context.Session.HasMultiplePartyMembers?.Invoke() == true;
        if (multiplePartyMembers || context.Session.ReadEvildomWar() is not null)
        {
            if (partyDungeons is null || direct || replay || practice || continuation || sealDoor)
                return await RejectAsync();
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, context.Session.CurrentParty?.Invoke()?.Members ?? []);
            (string Reason, byte MemberSlot)? partyRejection = null;
            var entered = partyDungeons.Enter(context.Session, selection.DungeonIndex, selection.Parameter,
                dungeons, rooms, drops, items, pieces, prepareDrops,
                questId: hell || beastMode ? 0u : selection.Unknown16, hellParty: hell, hellLegend: legend,
                crackRewards: crackRewards, beastMode: beastMode,
                onRejected: (reason, slot) => partyRejection = (reason, slot));
            log.Write("formal_dungeon_entry", new { Connection = context.Id, Accepted = entered.Length > 0, Party = true,
                Members = entered.Count(notice => notice.Kind == PartyDungeonNoticeKind.Entry), Dungeon = selection.DungeonIndex, Quest = selection.Unknown16, HellParty = hell, HellLegend = legend,
                Beast = beastMode, Request = selection,
                RejectReason = entered.Length == 0 ? partyRejection?.Reason ?? "party-entry-unavailable" : null,
                RejectMemberSlot = partyRejection?.MemberSlot });
            if (entered.Length == 0) return await RejectAsync(partyRejection?.Reason, partyRejection?.MemberSlot, party: multiplePartyMembers);
            await PartyDungeonProjection.PublishAsync(entered);
            return CommandResult.Handled;
        }
        var receipt = continuation ? context.Session.EnterFormalDungeonContinuation(selection.DungeonIndex,selection.Unknown16,selection.Parameter) :
            replay ? context.Session.EnterQuestReplay(selection.DungeonIndex,selection.Unknown16,selection.Parameter) :
            (sealDoor ? context.Session.EnterFormalDungeon(selection.DungeonIndex, 0, selection.Parameter, dungeons, rooms,
                drops, items, pieces: pieces, prepareDrops: prepareDrops,tutorials: tutorials,tutorialScenes: tutorialScenes,
                hellParty: true, sealDoor: true, crackRewards: crackRewards) : null) ??
            context.Session.EnterFormalDungeon(selection.DungeonIndex, hell || legend || beastMode ? 0u : selection.Unknown16, selection.Parameter, dungeons, rooms,
                drops, items, pieces: pieces, prepareDrops: prepareDrops,tutorials: tutorials,tutorialScenes: tutorialScenes,
                classExperience: experience, hellParty: hell || legend, hellLegend: legend, practice: practice, crackRewards: crackRewards,
                beastMode: beastMode);
        log.Write("formal_dungeon_entry", new { Connection = context.Id, Accepted = receipt is not null, Replay = replay, Continuation = continuation,
            Training = training, ClassExperience = experience, Practice = practice, HellParty = hell, HellLegend = legend, Beast = beastMode,
            // 魔兽入场记下魔兽区块声明的过场图，实机日志能直接看出走的是魔兽布局而不是普通噩梦。
            Cutscene = receipt?.Entry.Maze.BeastLayout?.CutsceneImage,
            SealDoor = sealDoor && receipt?.Entry.HellParty is not null, Request = selection, receipt?.Snapshot.Identity,
            RejectReason = receipt is null && !continuation && !replay ? context.Session.LastFormalDungeonRejection : null,
            HellTicket = receipt?.HellTicket, EntryItem = receipt?.Entry.EntryItem, StartFatigue = receipt?.Entry.StartFatigue,
            EntryFatigueCharged = receipt?.EntryFatigueCharged, EntryCostNote = receipt?.Entry.EntryCostNote,
            GriefLayer = receipt?.Entry.GriefLayer, Grief = receipt?.GriefAdmission?.Decision,
            Dazzlement = receipt?.Dazzlement,
            CharacterId = character.Id, CharacterName = character.Name });
        if (receipt is null) return await RejectAsync(!continuation && !replay ? context.Session.LastFormalDungeonRejection : null);
        await town.DetachAsync(context);
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
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
        // 实验性（#259）：比赛副本先发 NOTI386 对阵/对手，再以 NOTI387 替代 NOTI29（依据来自 Go 项目反汇编，未经 V31 客户端验证）。
        if (receipt.Monsters.Altar is not null)
            await context.Notify(NotiPacketType.START_BLOOD_MAP, DungeonAltarPackets.StartMap(receipt.Entry), "formal_altar_map", Guard);
        else if (receipt.Entry.IsTournament && receipt.Monsters.Tournament is { } tournament)
        {
            await context.Notify(NotiPacketType.TOURNAMENT_INFO, DungeonPackets.TournamentInfo(receipt.Entry, tournament),
                "formal_tournament_info", Guard);
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
                await context.Notify(NotiPacketType.BOSS_RUSH_DUNGEON_INFO, BossRushPackets.DungeonInfo(rush.Bosses),
                    "formal_boss_rush_info", Guard);
                await context.Notify(NotiPacketType.BOSS_RUSH_DATA, BossRushPackets.Data(rush.Hud.Rank, rush.Hud.Score, rush.Hud.RemainMilliseconds),
                    "formal_boss_rush_data", Guard);
            }
        }
        if (DungeonPackets.HellPartyMonsterInfo(receipt.Monsters) is { } hellInfo)
            await context.Notify(NotiPacketType.HELL_PARTY_MONSTER_INFO, hellInfo, "formal_hell_party_monster_info", Guard);
        if (!receipt.HellConsumedSlots.IsDefaultOrEmpty && receipt.HellTicket is { } hellTicket)
        {
            // 实验性：深渊派对入场票已在入场提交时扣除；把背包变化投给客户端并留日志。
            log.Write("hell_party_entry_ticket_consumed", new { Connection = context.Id, receipt.Entry.DungeonId, hellTicket.ItemId, hellTicket.Count, receipt.LegendTicket,
                Slots = receipt.HellConsumedSlots.Select(slot => slot.Index).ToArray(), CharacterId = character.Id, CharacterName = character.Name });
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, [.. receipt.HellConsumedSlots]), "hell_party_entry_items", Guard);
        }
        if (receipt.SpecialEntry is { Gold: > 0 } special)
        {
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, [new(InventoryListType.Main, 0)]), "formal_altar_entry_gold", Guard);
            log.Write("formal_altar_entry_committed", new { Connection = context.Id, receipt.Entry.DungeonId,
                special.Day, special.Kind, special.Gold, EntryNumber = special.Expected.CharacterEntries + 1 });
        }
        if (!receipt.EntryItemConsumedSlots.IsDefaultOrEmpty)
        {
            // #510/#511 普通入场票已在入场提交时与疲劳、尝试号同事务扣除；把背包变化投给客户端并留日志。
            log.Write("dungeon_entry_item_consumed", new { Connection = context.Id, receipt.Entry.DungeonId,
                ItemId = receipt.Entry.EntryItem?.ItemId ?? GriefTowerEntryDecision.InvitationItem,
                Count = receipt.Entry.EntryItem?.Count ?? 1,
                Slots = receipt.EntryItemConsumedSlots.Select(slot => slot.Index).ToArray(), receipt.EntryFatigueCharged,
                CharacterId = character.Id, CharacterName = character.Name });
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, [.. receipt.EntryItemConsumedSlots]), "dungeon_entry_items", Guard);
        }
        if (!receipt.ChannelConsumedSlots.IsDefaultOrEmpty)
        {
            log.Write("channel_town_entry_items_consumed", new { Connection = context.Id, receipt.Entry.DungeonId,
                Slots = receipt.ChannelConsumedSlots.Select(slot => slot.Index).ToArray(), CharacterId = character.Id });
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, [.. receipt.ChannelConsumedSlots]), "channel_town_entry_items", Guard);
        }
        // 祭坛金币、入场票、深渊票、强袭与频道代价已各自投影过同槽位；入场货币只补未投影的槽位，同一格不重复下发。
        var projectedSlots = new HashSet<short>();
        if (receipt.SpecialEntry is { Gold: > 0 }) projectedSlots.Add(0);
        foreach (var slot in receipt.EntryItemConsumedSlots) projectedSlots.Add(slot.Index);
        foreach (var slot in receipt.ChannelConsumedSlots) projectedSlots.Add(slot.Index);
        foreach (var slot in receipt.HellConsumedSlots) projectedSlots.Add(slot.Index);
        foreach (var slot in receipt.AssaultConsumedSlots) projectedSlots.Add(slot.Index);
        var currencySlots = EntryCurrencySlots(receipt.EntryChangedCurrencies, projectedSlots);
        if (currencySlots.Length != 0)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, currencySlots), "dungeon_entry_currencies", Guard);
        if (receipt.EntryChangedCurrencies.Any(kind => kind is >= CurrencyKind.Cera and <= CurrencyKind.HappyTokenCera))
            await context.Notify(NotiPacketType.CERA, CurrencyPackets.Cera(receipt.Character.Inventory.AccountWallet), "dungeon_entry_cera", Guard);
        foreach (var enterCount in receipt.EnterCounts.IsDefault ? [] : receipt.EnterCounts)
            await context.Notify(NotiPacketType.DUNGEON_ENTER_COUNT_INFO, DungeonEnterCountPackets.Info(enterCount.Dungeon, enterCount.Daily, enterCount.Weekly),
                "dungeon_enter_count_after_entry", Guard);
        if (!receipt.AssaultConsumedSlots.IsDefaultOrEmpty)
        {
            log.Write("assault_mode_entry_committed", new { Connection = context.Id, receipt.Entry.DungeonId,
                Slots = receipt.AssaultConsumedSlots.Select(slot => slot.Index).ToArray() });
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, [.. receipt.AssaultConsumedSlots]), "assault_mode_entry_items", Guard);
        }
        if (receipt.SpecialEntry?.Kind is DungeonSpecialEntryKind.Blood or DungeonSpecialEntryKind.Ultimate)
            await DungeonEnterCountProjection.RefreshAsync(context, dungeons, Guard);
        else if (receipt.GriefAdmission is not null)
            await DungeonEnterCountProjection.RefreshGriefAsync(context, dungeons, Guard);
        else if (receipt.SpecialEntry is { Kind: DungeonSpecialEntryKind.Despair })
            await DungeonEnterCountProjection.RefreshDespairAsync(context, dungeons, Guard);
        if (receipt.Entry.Dimension is not null) await DimensionEntranceProjection.RefreshAsync(context, Guard, entranceOnly: false);
        return CommandResult.Handled;

        async Task<CommandResult> RejectAsync(string? entryReason = null, byte? memberSlot = null, bool party = false)
        {
            // 每一条被拒的选图都留原始请求体，实机排错不用再靠猜标志位。
            log.Write("formal_dungeon_entry", new { Connection = context.Id, Accepted = false, Replay = replay, Continuation = continuation,
                Training = training, ClassExperience = experience, Practice = practice, Beast = beastMode, Request = selection,
                RejectReason = entryReason ?? "selection-rejected", MemberSlot = memberSlot, CharacterId = character.Id });
            bool CanSend() => context.Session.CanPublishFormalDungeonRejection(character, before.SelectionEpoch,
                before.Phase, before.CurrentDungeonSelection);
            // 结束本次入场等待，保留原选图和回城锚点；不发送成功或自动回城通知。
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
            log.Write("formal_dungeon_move", new { Connection = context.Id, Party = party.Id, LoadingReady = true, Released = completed.Length });
            await PartyDungeonProjection.PublishAsync(completed);
            return CommandResult.Handled;
        }
        var receipt = context.Session.CompleteFormalDungeonLoading();
        if (receipt is null) return CommandResult.Unhandled;
        if (receipt.NavigationReplay) return CommandResult.Handled;
        bool published = false;
        bool Guard() => published = context.Session.CanPublishFormalDungeon(receipt);
        if (!receipt.EntryItemConsumedSlots.IsDefaultOrEmpty)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, [.. receipt.EntryItemConsumedSlots]), "beast_loading_ticket", Guard);
        await context.Reply(CmdPacketType.FINISH_LOADING, [1], "formal_loading_ack", Guard);
        await context.Notify(NotiPacketType.FINISH_LOADING, DungeonPackets.LoadingNotice(), "formal_loading_notice", Guard);
        await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(receipt.Character.Id), "formal_player_state", Guard);
        // 进入副本重建Actor后补发网吧特权buff，否则buff栏在副本内是空的。
        await PcRoomBuffProjection.PostAsync(context, Guard);
        town.GuildEconomy?.Post(context, force: true);
        await GuildTrainingProjection.PostAsync(context);
        if (receipt.ScriptedReturnId != Guid.Empty)
        {
            if (published) context.Session.ConfirmScriptedReturnLoading(receipt);
            return CommandResult.Handled;
        }
        if (receipt.Entry.UsesFatigue)
            await context.Notify(NotiPacketType.FATIGUE, FatiguePackets.Current(context.Session.FatigueForClient(receipt.Character)), "formal_room_fatigue", Guard);
        if (context.Elevator is not null) await context.Elevator.StartAsync(receipt);
        if (context.LostLand is not null) await context.LostLand.StartAsync(receipt);
        if (context.Beast is not null) await context.Beast.RefreshAsync();
        if (context.Altar is not null && context.Session.StartDungeonAltar(receipt) is { } altar)
            await context.Altar.WakeAsync(altar);
        if (receipt.HellPartyRoomCleared)
            await context.Notify(NotiPacketType.HELL_PARTY_CLEAR, DungeonPackets.HellPartyCleared(), "formal_hell_party_clear", Guard);
        if (receipt.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement)
            await EnableSettlementAsync(context);
        await PublishEpicPieceShopAsync(context, prepare: false);
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
                await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(progress.Character.ActiveMainQuests),
                    "formal_passive_quest_conditions",() => context.Session.CanPublishQuestLifecycle(progress));
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
            Target = target is null ? null : new { target.MonsterId, target.SpawnType, target.Team, target.IsFriendly, target.IsBlocking, target.CanReportDeath },
            result.Receipt?.Snapshot.Identity, result.Reward, result.GainedExperience, result.ProficiencyGained, result.ClearFact, result.RoomClearFact });
        if (result.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate))
            return CommandResult.Handled;
        if (result.Receipt is not { } receipt)
        {
            if (result.Outcome == DungeonRunOutcome.Duplicate && context.Session.CurrentTagEvildomClear() is { } clearedTag)
            {
                if (tagEvildomRun is not null) await tagEvildomRun.ClearAsync(context, clearedTag);
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
        if (context.Beast is not null) await context.Beast.RefreshAsync();
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
        await context.Notify(NotiPacketType.DIE_MONSTER,
            ExclusiveDropPackets.MonsterDeath(result.MonsterKey, dropRows, enteredDungeon, result.BossPositionUpdate), "formal_monster_death", Guard);
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
            if (result.BeastRewardSlots.Length > 0)
                await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(receipt.Character.Inventory,
                    InventoryListType.Main,[.. result.BeastRewardSlots]),"formal_beast_stage_reward",Guard);
            foreach (ushort key in result.CulledMonsterKeys)
                await context.Notify(NotiPacketType.DIE_MONSTER, ExclusiveDropPackets.MonsterDeath(key, [], enteredDungeon), "formal_clear_room_cull", Guard);
            if (result.ImperialArenaDeath is { } imperialDeath)
            {
                if (imperialArena is null) throw new InvalidOperationException("帝国竞技场已提交奖励缺少投影服务");
                await imperialArena.DeathAsync(context, imperialDeath, Guard);
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
        if (result.QuestDropSlots.Count > 0)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main,[.. result.QuestDropSlots]),"formal_quest_drop_items",Guard);
        if (result.BeastRewardSlots.Length > 0)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main,[.. result.BeastRewardSlots]),"formal_beast_stage_reward",Guard);
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
            previousLearning: previousCharacter?.Learning);
        bool closed = quests is not null && await quests.CloseReadyChildrenAsync(context);
        if (!closed && result.PreviousQuests is { } previous && receipt.Character.ActiveMainQuests.Any(row => previous.Any(old => old.AcceptanceId == row.AcceptanceId && old.ConditionState != row.ConditionState)))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(receipt.Character.ActiveMainQuests), "formal_hunt_conditions", Guard);
        if (result.ClearFact is not null && !potionClear)
            await EnableSettlementAsync(context, result.MonsterKey);
        await PublishEpicPieceShopAsync(context, prepare: true);
        return CommandResult.Handled;
    }

    async Task<CommandResult> PickupAsync(GameConnectionContext context, Packet request)
    {
        if (items is null || pieces is null || !DungeonPickupRequests.TryParseOrdinary(request.Body, out var parsed))
            return CommandResult.Unhandled;
        if (context.Session.PartyDungeon is not null) return await PartyPickupAsync(context, parsed.GroundObjectKey);
        var previousQuests = context.Owner.Execute(() => context.Session.Selected?.ActiveMainQuests);
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
        // 普通consumer不使用这九个局部值。本服无扩展贡献，明确投影零，不作Builder默认。
        var notice = commit.Currency == CurrencyKind.Gold
            ? DungeonPickupPackets.SinglePlayerGoldNotification(parsed.GroundObjectKey, receipt.Character.Id, checked((uint)commit.Amount))
            : DungeonPickupPackets.OrdinaryNotification(parsed.GroundObjectKey,
                receipt.Character.Id, new byte[8], receipt.Character.Id,
                commit.EpicPiece is not null ? DungeonPickupPackets.NoInventorySlot : checked((ushort)destination.Index), 0);
        await context.Notify(NotiPacketType.GET_ITEM, notice, "formal_pickup_notice", Guard);
        if (commit.EpicPiece is { } fragment)
        {
            await context.Notify(NotiPacketType.EPIC_BOOK_INFO,
                EpicBookPackets.Info(receipt.Character.Inventory.EpicPieces, pieces, [fragment.Index]), "formal_pickup_epic_piece", Guard);
            log.Write("epic_piece_pickup", new { Connection = context.Id, Character = receipt.Character.Id,
                Ground = parsed.GroundObjectKey, fragment.PieceId, fragment.Amount, fragment.Balance, fragment.BookRevision, commit.Duplicate });
        }
        else
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                destination.List, [destination]), "formal_pickup_inventory", Guard);
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
        if (context.Session.PartyDungeon is { } party)
        {
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, party.Members);
            var moved = party.Move(context.Session, move.X, move.Y, move.Unknown10);
            log.Write("formal_dungeon_move", new { Connection = context.Id, Party = party.Id, Accepted = moved.Length > 0, move.X, move.Y });
            await PartyDungeonProjection.PublishAsync(moved);
            return CommandResult.Handled;
        }
        var receipt = context.Session.MoveFormalDungeon(move.X, move.Y, move.Unknown10);
        log.Write("formal_dungeon_move",new { Connection = context.Id, Accepted = receipt is not null,
            move.X, move.Y, Kind = move.Unknown10, receipt?.Snapshot.CurrentMap, receipt?.Snapshot.MapOperation, receipt?.Snapshot.MapPayload });
        if (receipt is null) return CommandResult.Unhandled;
        if (receipt.NavigationReplay) return CommandResult.Handled;
        bool published = false;
        bool Guard() => published = context.Session.CanPublishFormalDungeon(receipt);
        await context.Reply(CmdPacketType.MOVE_MAP, DungeonPackets.MoveAccepted(), "formal_move_ack", Guard);
        if (receipt.Monsters.Altar is not null)
        {
            if (context.Altar is not null) await context.Altar.StopAsync();
            await context.Notify(NotiPacketType.START_BLOOD_MAP,
                DungeonAltarPackets.StartMap(receipt.Entry, receipt.Snapshot.MapPayload), "formal_altar_next_map", Guard);
            return CommandResult.Handled;
        }
        await MonsterAiProjection.SendAsync(context, receipt, "formal_monster_ai", Guard);
        await context.Notify(NotiPacketType.START_MAP, DungeonPackets.StartMap(receipt.Entry, receipt.Monsters, receipt.MapSeed,
            receipt.Snapshot.MapOperation,receipt.Snapshot.MapPayload, receipt.PassiveItems), "formal_next_map", Guard);
        if (DungeonPackets.HellPartyMonsterInfo(receipt.Monsters) is { } hellInfo && receipt.Snapshot.MapPayload != DungeonMapPayload.Cached)
            await context.Notify(NotiPacketType.HELL_PARTY_MONSTER_INFO, hellInfo, "formal_hell_party_monster_info", Guard);
        if (published && receipt.ScriptedReturnId != Guid.Empty) context.Session.ConfirmScriptedReturnPresentation(receipt);
        if (context.Beast is not null) await context.Beast.RefreshAsync();
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
        var party = context.Session.PartyDungeon;
        using var peers = party is null ? null : await PartyDungeonProjection.AcquirePeersAsync(context, party.Members);
        if (party is not null)
        {
            return await PartyDungeonProjection.DepartAsync(context, partyDungeons, parties,
                () => context.Reply(CmdPacketType.GIVEUP_GAME, DungeonPackets.GiveUpAccepted(), "formal_giveup_ack"));
        }
        bool tag = context.Session.HasFormalTagEvildom;
        var receipt = context.Session.ReturnFromFormalDungeon();
        if (receipt is null) return CommandResult.Unhandled;
        if (context.LostLand is not null) await context.LostLand.StopAsync();
        if (context.Beast is not null) await context.Beast.RefreshAsync();
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
}
