using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Pvp;
using DfoServer.GameWorld.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Projection;

namespace DfoServer.Network.Handlers;

// 普通练习与任务连招、街机各有独立实例，不把普通练习或裸33进度当任务训练胜利。
internal sealed class PvpTrainingHandler(PvpArcadeCatalog arcade,PvpPracticeComboCatalog combos,QuestCatalog quests,ServerLog log,
    ItemCatalog items,GoldLimitCatalog goldLimits,EpicPieceCatalog pieces,DfoServer.GameWorld.Characters.SkillLearningCatalog skills,
    PvpMissionBook? missionBook = null, PvpMissionProjection? missions = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.ARCADEMODE_PVP_ROUND_INFO,RoundAsync,nameof(PvpTrainingHandler));
        commands.Register(CmdPacketType.PONGUNTOOKUK_REWARD,ManualAsync,nameof(PvpTrainingHandler));
        commands.Register(CmdPacketType.ARCADE_PVP_DATA_COPY,CopyAsync,nameof(PvpTrainingHandler));
        commands.Register(CmdPacketType.PVP_MISSION_COMBO, ComboAsync, nameof(PvpTrainingHandler));
        commands.Register(CmdPacketType.PVP_MISSION_ARCADE_MODE, ArcadeAsync, nameof(PvpTrainingHandler));
    }

    Task<CommandResult> ComboAsync(GameConnectionContext context, Packet packet)
    {
        int index = PvpTrainingRequests.Combo(packet.Body);
        var reward = missionBook is null ? null : context.Session.CompletePvpMissionCombo(index, missionBook);
        bool queued = missions?.QueueRewards(context, reward) != false;
        if (queued && reward is not null) missions?.Queue(context, update: true);
        log.Write("pvp_mission_combo", new { Connection = context.Id, Index = index,
            Committed = reward is not null, Queued = queued });
        return Task.FromResult(CommandResult.Handled);
    }

    Task<CommandResult> ArcadeAsync(GameConnectionContext context, Packet packet)
    {
        byte mode = PvpTrainingRequests.ArcadeMode(packet.Body);
        // 015D7EE3在末轮获胜时发送881，随后才发1287；此信号本身不能证明通关。
        log.Write("pvp_mission_arcade_signal", new { Connection = context.Id, Mode = mode });
        return Task.FromResult(CommandResult.Handled);
    }

    async Task<CommandResult> CopyAsync(GameConnectionContext context,Packet packet)
    {
        byte choice=PvpTrainingRequests.CopyChoice(packet.Body);
        var receipt=context.Session.ChooseTrainingSkills(choice,skills);
        log.Write("pvp_training_copy",new {Connection=context.Id,choice,Committed=receipt is not null});
        if(receipt is not null && context.Session.UsesArenaSkills)
            await SkillProjection.PublishAsync(context,receipt.Character,"pvp_training_copy_skills","pvp_training_copy_combos",
                ()=>context.Session.CanPublishLearning(receipt));
        // 本版未证实1268独立ACK；仅在事务后回放实际正式决斗技能，不造成功正文。
        return CommandResult.Handled;
    }

    async Task<CommandResult> ManualAsync(GameConnectionContext context,Packet packet)
    {
        if(packet.Body.Length!=0) return CommandResult.Unhandled;
        var receipt=context.Session.ObservePvpTrainingTrigger(2674,new(0x40,0,0),quests,true,items,goldLimits,pieces);
        log.Write("pvp_training_manual",new {Connection=context.Id,Accepted=receipt?.Commit is not null});
        if(receipt?.Commit is {ProjectionCurrent:false}) return CommandResult.CloseConnection;
        if(receipt?.Commit is { } commit)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,
                InventoryPackets.Delta(commit.Character.Inventory,InventoryListType.Main,[..commit.Plan.Inventory.ChangedSlots]),
                "pvp_training_manual",()=>context.Session.CanPublishQuestLifecycle(receipt));
        // 015C1DFC只发空1269；物品变动后由0202C140调用015C23A0发33，不虚构独立成功ACK。
        return CommandResult.Handled;
    }

    internal async Task<CommandResult> CreateAsync(GameConnectionContext context,Packet packet)
    {
        var request=PvpTrainingRequests.Create(packet.Body);
        // 普通大厅练习使用同版kind1/mode0，任务道场仍须匹配原接取及训练条件。
        if (request is { Kind: 1, MapIndex: 0, Setting: 0, Mode: 0 } &&
            context.Session.Selected is { Position.Town: 10, Position.Area: <= 7 and not 4 })
        {
            var character = context.Session.Selected;
            var practice = context.Session.BeginPvpPractice(context.BlocksTownActions, combos.Resolve(character.Job, character.GrowType), expected: character);
            log.Write("pvp_practice", new { Connection = context.Id, Accepted = practice is not null, Instance = practice?.Instance,
                character.Position.Town, character.Position.Area });
            if (practice is null)
                await context.Reply(CmdPacketType.MAKE_PVP_ROOM, PvpTrainingPackets.Rejected(), "pvp_practice_rejected");
            else
            {
                bool Current() => context.Session.CanPublishPvpPractice(practice);
                using var publication = context.Sender.QueuePublication(Current);
                await context.Reply(CmdPacketType.MAKE_PVP_ROOM, PvpTrainingPackets.Created(), "pvp_practice_created", Current);
                await context.Notify(NotiPacketType.PVP_ROOM_INFO, PracticeRoom(practice.Actor.Character), "pvp_practice_room", Current);
            }
            return CommandResult.Handled;
        }
        var receipt=request is {Kind:3,MapIndex:0,Setting:0,Mode:>0} or {Kind:1,MapIndex:0,Setting:0,Mode:0}
            ? context.Session.BeginPvpTraining(Prepare,context.BlocksTownActions) : null;
        var standalone = context.Session.Selected;
        var arcadePlan = request.Kind == 3 && standalone is not null ? arcade.Resolve(standalone.Job, standalone.GrowType, request.Mode - 1) : null;
        if (receipt is null && request is { Kind: 3, MapIndex: 0, Setting: 0, Mode: > 0 } && arcadePlan is not null &&
            standalone is { Position.Town: 10, Position.Area: <= 7 })
        {
            var practice = context.Session.BeginPvpPractice(context.BlocksTownActions, arcade: arcadePlan, expected: standalone);
            if (practice is not null)
            {
                bool Current() => context.Session.CanPublishPvpPractice(practice);
                using var publication = context.Sender.QueuePublication(Current);
                await context.Reply(CmdPacketType.MAKE_PVP_ROOM, PvpTrainingPackets.Created(), "pvp_arcade_created", Current);
                await context.Notify(NotiPacketType.PVP_ROOM_INFO, Room(practice.Actor.Character.Id, false, 10), "pvp_arcade_room", Current);
                log.Write("pvp_arcade", new { Connection = context.Id, Instance = practice.Instance, Mode = arcadePlan.Mode.Index });
                return CommandResult.Handled;
            }
        }
        log.Write("pvp_training",new {Connection=context.Id,packet.Opcode,Accepted=receipt is not null,
            request.Kind,Mode=request.Mode,Instance=receipt?.Run.InstanceId,Town=standalone?.Position.Town,Area=standalone?.Position.Area});
        if(receipt is null) await context.Reply(CmdPacketType.MAKE_PVP_ROOM,PvpTrainingPackets.Rejected(),"pvp_training_rejected");
        else
        {
            bool Current()=>context.Session.CanPublishPvpTraining(receipt);
            await context.Reply(CmdPacketType.MAKE_PVP_ROOM,PvpTrainingPackets.Created(),"pvp_training_created",Current);
            await context.Notify(NotiPacketType.PVP_ROOM_INFO,Room(receipt.Run,false),"pvp_training_room",Current);
        }
        return CommandResult.Handled;

        PvpTrainingEntry? Prepare(Character character)
        {
            var candidates=character.ActiveMainQuests.Where(row=>quests.TryGet(row.QuestId,out var q) &&
                PvpTrainingProgress.Valid(q,row.ConditionState) && row.ConditionState!=0).ToArray();
            if(candidates is not [var active] || !quests.TryGet(active.QuestId,out var quest)) return null;
            if(request.Kind==1) return PvpTrainingEntry.PrepareCombo(character,quest,combos.Resolve(character.Job,character.GrowType));
            var plan=arcade.Resolve(character.Job,character.GrowType,request.Mode-1);
            if(plan is null) return null;
            // 已解析模式池同时用于正式回合校验；不借其它角色或职业提供候选。
            var source=arcade.Opponents.GetValueOrDefault((character.Job,character.GrowType,plan.Mode.Index));
            source ??=new(System.Collections.Frozen.FrozenDictionary<int,ImmutableArray<int>>.Empty,plan.Mode.RandomOpponents);
            return PvpTrainingEntry.Prepare(character,quest,plan.Mode,source) is { } entry ? entry with {Rounds=plan.Allowed,RandomRounds=plan.RandomRounds} : null;
        }
    }

    internal async Task<CommandResult> SeatAsync(GameConnectionContext context,Packet packet)
    {
        var request=PvpTrainingRequests.Seat(packet.Body);
        if (context.Session.ReadPvpPractice() is { } practice)
        {
            var left = request is { Seat: 0, State: 254 } ? context.Session.EndPvpPractice(practice) : null;
            if (left is not null)
            {
                await context.Notify(NotiPacketType.PVP_SEAT_STATE, PvpTrainingPackets.LeaveSeat(left.Actor.Character.Id, left.Mode is null ? (byte)6 : (byte)10),
                    "pvp_practice_exit", () => context.Session.CanPublishPvpPractice(left, removed: true));
                TownProjection.QueuePvpReturn(context, left.Actor.Character,
                    () => context.Session.CanPublishPvpPractice(left, removed: true));
            }
            return CommandResult.Handled;
        }
        var run=context.Session.ReadPvpTraining();
        // 原生公共退出发seat/FE；只结束本人私有训练，不泛化普通换座、观战或其它玩家房间。
        var receipt=request is {Seat:0,State:254} && run is not null
            ? context.Session.EndPvpTraining(run.InstanceId) : null;
        log.Write("pvp_training",new {Connection=context.Id,packet.Opcode,Accepted=receipt is not null,request.Seat,request.State});
        if(receipt is not null)
        {
            await context.Notify(NotiPacketType.PVP_SEAT_STATE,PvpTrainingPackets.LeaveSeat(receipt.Run.Entry.Expected.Id,receipt.Run.Entry.IsCombo ? (byte)6 : (byte)10),
                "pvp_training_exit",()=>context.Session.CanPublishPvpTraining(receipt));
            TownProjection.QueuePvpReturn(context, receipt.Run.Entry.Expected,
                () => context.Session.CanPublishPvpTraining(receipt));
        }
        // CMD52没有独立成功ACK合同；清席后显式完成城镇场景投影。
        return CommandResult.Handled;
    }

    Task<CommandResult> RoundAsync(GameConnectionContext context,Packet packet)
    {
        var report=PvpTrainingRequests.Round(packet.Body);
        var current=context.Session.ReadPvpTraining();
        var receipt=current is null ? null : context.Session.ReportPvpTraining(current,report);
        bool accepted = receipt is not null || context.Session.ReportPvpPractice(report);
        if (accepted && missionBook is not null)
        {
            var reward = context.Session.CompletePvpMissionArcade(missionBook);
            if (reward is not null && missions?.QueueRewards(context, reward) != false) missions?.Queue(context, update: true);
        }
        log.Write("pvp_training",new {Connection=context.Id,packet.Opcode,Accepted=accepted,
            report.Event,report.Round,report.Opponent,WonAllRounds=receipt?.WonAllRounds==true});
        // 1287没有已证实的ACK消费者；不伪造成功包。任务事务另行消费正式事实。
        return Task.FromResult(CommandResult.Handled);
    }

    internal static byte[] PracticeRoom(Character character) => Room(character.Id, false, 6);

    internal static byte[] Room(PvpTrainingRun run,bool removed) => Room(run.Entry.Expected.Id, removed, run.Entry.IsCombo ? (byte)6 : (byte)10);

    static byte[] Room(ushort id, bool removed, byte mode)
    {
        var seats=Enumerable.Range(0,8).Select(index=>new PvpRoomSeat(0,!removed && index==0 ? id : ushort.MaxValue)).ToImmutableArray();
        // MapIndex为客户端地图选择序号而非50000等MAP ID，保留原请求0；原连招room mode=6，街机为10。
        return PvpTrainingPackets.Room(new(id,0,0,removed ? (sbyte)-1 : (sbyte)0,0,mode,seats,0,0));
    }
}
