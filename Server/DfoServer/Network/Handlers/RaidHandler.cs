using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

// 攻坚队创建/退出（单人队长队；入队、编队、开打之后的包序未恢复）。
// C2S664 CREATE_RAID：请求类型必须等于 01DC69B0(当前频道类型) 且 raid.lst 有该类型脚本，否则回 ACK664 失败 0x13（dstr 32060）。
//   成功顺序：NOTI 0x24E 记录 → NOTI 0x24F op3（u8 M=1 + 队长成员块，开窗）→ ACK664 成功 u32 key（01DDC920 对已设的同一 key 无副作用）。
//   魔界大战频道 0x36 → 类型 6：raid.lst 无 6，01DDADC0 对 >5 返回空，窗口刷新 021752C5 直接放弃，保持失败。
// C2S665 LEAVE_RAID：u16 角色号（01DC6D60 写 0643A52C）。本角色是队长时删队并回 ACK665 成功 [1, 0]。
// C2S658 RAID_ENTRY_COST_INFO：u8 标志（01DC65B0，客户端置 [mgr+0x1C4]=1 防重发）；ACK658 成功不读字段，回 [1]。入场材料数据包未恢复。
// C2S666 START_RAID：空包体（01DC7D52）。没有队伍回 0x13；单人队低于脚本 [RAID START MINIMUM MEMBER] 回 0x16；
//   人数足够也回 0x13（开战后入场包序未恢复，不回成功）。
// C2S667 SET_RAID_WAITING：u8 0 + u8 团本类型（02172BBF）。已是队长回 0x13，已登记回 0x12，否则登记并回成功 [1]（待机列表下发未恢复）。
// C2S670 MODIFY_RAID_INFO：u8 类型 + 队名 + u8。本角色是该类型队长时改标题，先发 NOTI 0x24F op2 整条记录再回 [1]；否则回 0x13。
// 踢人：LEAVE_RAID 第二发送点 0398B9DC 写 [obj+0x2C] 的 u16，单人队没有可踢成员，非本人仍不回包。加入/C2S669 RAID_MANAGER_WORK 未恢复。
// C2S1361 REQUEST_RAID_INFO 客户端没有 ACK 处理器（0x551 只注册了 NOTI），不在这里回包。
internal sealed class RaidHandler(ServerLog log, RaidCatalog catalog, RaidRegistry registry) : IGameCommandModule
{
    public RaidHandler(ServerLog log) : this(log, RaidCatalog.Empty, new RaidRegistry()) { }

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.CREATE_RAID, CreateAsync, nameof(RaidHandler));
        commands.Register(CmdPacketType.LEAVE_RAID, LeaveAsync, nameof(RaidHandler));
        commands.Register(CmdPacketType.RAID_ENTRY_COST_INFO, EntryCostAsync, nameof(RaidHandler));
        commands.Register(CmdPacketType.START_RAID, StartAsync, nameof(RaidHandler));
        commands.Register(CmdPacketType.SET_RAID_WAITING, WaitingAsync, nameof(RaidHandler));
        commands.Register(CmdPacketType.MODIFY_RAID_INFO, ModifyAsync, nameof(RaidHandler));
    }

    async Task<CommandResult> StartAsync(GameConnectionContext context, Packet request)
    {
        var character = context.Session.Selected;
        if (request.Body.Length != 0 || character is null) return CommandResult.Unhandled;
        var party = registry.Find(character.Id);
        var definition = party is null ? null : catalog.Find(party.Type);
        const int members = 1;
        ushort error = party is null || definition is null ? RaidPackets.StartUnavailable :
            members < definition.StartMinimum ? RaidPackets.StartTooFewMembers : RaidPackets.StartUnavailable;
        log.Write("raid_start_request", new { Connection = context.Id, Self = character.Id, Key = party?.Key.ToString("X8"), Members = members,
            definition?.StartMinimum, Error = error, Reason = party is null ? "no-raid" : error == RaidPackets.StartTooFewMembers ? "too-few-members" : "start-flow-unrecovered" });
        await context.Reply(CmdPacketType.START_RAID, RaidPackets.StartFailure(error), "raid_start_rejected");
        return CommandResult.Handled;
    }

    async Task<CommandResult> WaitingAsync(GameConnectionContext context, Packet request)
    {
        var character = context.Session.Selected;
        if (request.Body.Length != 2 || character is null) return CommandResult.Unhandled;
        byte raidType = request.Body[1];
        ushort? error = registry.Find(character.Id) is not null ? RaidPackets.WaitingInRaid :
            !registry.AddWaiting(character.Id) ? RaidPackets.WaitingAlready : null;
        log.Write("raid_waiting_request", new { Connection = context.Id, Self = character.Id, Head = request.Body[0], RaidType = raidType,
            Accepted = error is null, Error = error });
        await context.Reply(CmdPacketType.SET_RAID_WAITING, error is { } code ? RaidPackets.CreateFailure(code) : RaidPackets.WaitingSuccess,
            error is null ? "raid_waiting" : "raid_waiting_rejected");
        return CommandResult.Handled;
    }

    async Task<CommandResult> ModifyAsync(GameConnectionContext context, Packet request)
    {
        ModifyRaidRequest parsed;
        try { parsed = RaidRequests.ModifyRaid(request.Body); }
        catch (InvalidDataException error)
        {
            log.Write("raid_modify_request", new { Connection = context.Id, Body = Convert.ToHexString(request.Body), Accepted = false,
                Reason = "body-layout-mismatch", Detail = error.Message });
            return CommandResult.Unhandled;
        }
        var character = context.Session.Selected;
        if (character is null) return CommandResult.Unhandled;
        var current = registry.Find(character.Id);
        if (current is null || current.Type != parsed.RaidType)
        {
            log.Write("raid_modify_request", new { Connection = context.Id, parsed.RaidType, parsed.Name, parsed.Field, Accepted = false,
                Reason = current is null ? "no-raid" : "raid-type-mismatch" });
            await context.Reply(CmdPacketType.MODIFY_RAID_INFO, RaidPackets.CreateFailure(RaidPackets.ModifyNoRaid), "raid_modify_rejected");
            return CommandResult.Handled;
        }
        byte[] title = CharacterName.Encoding.GetBytes(parsed.Name);
        var party = registry.Rename(character.Id, parsed.Name, title)!;
        log.Write("raid_modify_request", new { Connection = context.Id, parsed.RaidType, parsed.Name, parsed.Field, Accepted = true, Key = party.Key.ToString("X8") });
        await context.Notify(NotiPacketType.RAID_MODIFY, RaidPackets.UpdateRecord(party.Key, title, party.Type, character.Id,
            CharacterName.Encoding.GetBytes(character.Name)), "raid_modified_record");
        await context.Reply(CmdPacketType.MODIFY_RAID_INFO, RaidPackets.ModifySuccess, "raid_modified");
        return CommandResult.Handled;
    }

    async Task<CommandResult> CreateAsync(GameConnectionContext context, Packet request)
    {
        CreateRaidRequest parsed;
        try { parsed = RaidRequests.CreateRaid(request.Body); }
        catch (InvalidDataException error)
        {
            log.Write("raid_create_request", new { Connection = context.Id, Length = request.Body.Length,
                Body = Convert.ToHexString(request.Body), Accepted = false, Reason = "body-layout-mismatch", Detail = error.Message });
            return CommandResult.Unhandled;
        }
        var character = context.Session.Selected;
        var entry = context.Session.ChannelEntry;
        byte expected = entry is null ? RaidTypes.Default : RaidTypes.FromChannelType(entry.Type);
        var definition = catalog.Find(parsed.RaidType);
        string? reason = character is null ? "no-character" :
            context.Session.Phase != SessionPhase.Town || context.BlocksTownActions ? "not-in-town" :
            parsed.RaidType != expected ? "raid-type-channel-mismatch" :
            definition is null ? "raid-type-without-script" : null;
        if (reason is not null)
        {
            log.Write("raid_create_request", new { Connection = context.Id, parsed.RaidType, Expected = expected, ChannelType = entry?.Type,
                parsed.Name, parsed.Field210, parsed.Field211, parsed.Field214, parsed.Flags, Accepted = false, Reason = reason,
                Error = RaidPackets.CreateUnavailable });
            await context.Reply(CmdPacketType.CREATE_RAID, RaidPackets.CreateFailure(), "raid_create_rejected");
            return CommandResult.Handled;
        }
        byte[] title = CharacterName.Encoding.GetBytes(parsed.Name);
        byte[] leaderName = CharacterName.Encoding.GetBytes(character!.Name);
        var party = registry.Create(context.Channel, parsed.RaidType, parsed.Name, title, character.Id, character.Name);
        registry.RemoveWaiting(character.Id);
        log.Write("raid_create_request", new { Connection = context.Id, parsed.RaidType, parsed.Name, parsed.Field210, parsed.Field211,
            parsed.Field214, parsed.Flags, Accepted = true, Key = party.Key.ToString("X8"), Leader = character.Id,
            definition!.Path, definition.MemberMax, definition.StartMinimum });
        await context.Notify(NotiPacketType.RAID_LIST, RaidPackets.RaidList(party.Key, title, party.Type, character.Id, leaderName), "raid_created_record");
        await context.Notify(NotiPacketType.RAID_MODIFY, RaidPackets.OpenWithMembers(party.Key, [(character.Id, leaderName)]), "raid_created_open");
        await context.Reply(CmdPacketType.CREATE_RAID, RaidPackets.CreateSuccess(party.Key), "raid_created");
        return CommandResult.Handled;
    }

    async Task<CommandResult> LeaveAsync(GameConnectionContext context, Packet request)
    {
        var character = context.Session.Selected;
        if (request.Body.Length != 2 || character is null)
        {
            log.Write("raid_leave_request", new { Connection = context.Id, Body = Convert.ToHexString(request.Body), Accepted = false, Reason = "body-or-character" });
            return CommandResult.Unhandled;
        }
        ushort target = (ushort)(request.Body[0] | request.Body[1] << 8);
        var party = target == character.Id ? registry.Leave(target) : null;
        log.Write("raid_leave_request", new { Connection = context.Id, Target = target, Self = character.Id, Accepted = party is not null,
            Key = party?.Key.ToString("X8"), Reason = party is null ? (target == character.Id ? "no-raid" : "kick-unsupported") : null });
        if (party is null) return CommandResult.Unhandled;
        await context.Reply(CmdPacketType.LEAVE_RAID, RaidPackets.LeaveSuccess, "raid_left");
        return CommandResult.Handled;
    }

    async Task<CommandResult> EntryCostAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 1) return CommandResult.Unhandled;
        log.Write("raid_entry_cost_request", new { Connection = context.Id, Flag = request.Body[0] });
        await context.Reply(CmdPacketType.RAID_ENTRY_COST_INFO, RaidPackets.EntryCostSuccess, "raid_entry_cost");
        return CommandResult.Handled;
    }
}
