using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

internal readonly record struct ClientFailurePacket(CmdPacketType Opcode, byte[] Body);

// 只构造已核对的原生业务失败；不读取 dstr，也不代替业务事务或自动附加 NOTI502。
internal static class ClientFailurePackets
{
    public static ClientFailurePacket Create(ClientUnboundDstr prompt) =>
        throw new NotSupportedException(ClientPromptDiagnostics.Explain(prompt));

    public static ClientFailurePacket Create(ClientPartyHostError error) => Plain(CmdPacketType.CHANGE_HOST, error);
    public static ClientFailurePacket Create(ClientGuildCreateRightError error) => Plain(CmdPacketType.CALL_GUILD_CREATE_RIGHT, error);
    public static ClientFailurePacket Create(ClientGuildLevelError error) => Plain(CmdPacketType.CALL_GUILD_LEVEL_UP, error);
    public static ClientFailurePacket Create(ClientGuildJoinError error) => Plain(CmdPacketType.REQUEST_GUILD_ENTER, error);
    public static ClientFailurePacket Create(ClientGuildIntroductionError error) => Plain(CmdPacketType.CHECK_GUILD_CREATE_PROMOTE_MSG, error);
    public static ClientFailurePacket Create(ClientGuildPromoteChangeError error) => Plain(CmdPacketType.MODIFY_GUILD_PROMOTE_MSG, error);

    public static ClientFailurePacket Create(DungeonSelectionError error, byte memberSlot = 0) =>
        new(CmdPacketType.SELECT_DUNGEON, DungeonPackets.SelectionRejected(error, memberSlot));
    public static ClientFailurePacket Create(ClientCharacterNameError error) => Plain(CmdPacketType.CHECK_DOUBLE_CHARACTER_NAME, error);
    public static ClientFailurePacket Create(ClientPeerRequestError error, byte mode) =>
        new(CmdPacketType.REQUEST_PEER, PartyPackets.Failure(Code(error), mode));
    public static ClientFailurePacket Create(ClientPeerResponseError error, byte mode) =>
        new(CmdPacketType.RESPONSE_PEER, PartyPackets.Failure(Code(error), mode));
    public static ClientFailurePacket Create(ClientPartySettingsError error) => Plain(CmdPacketType.SET_PARTY_INFO, error);
    public static ClientFailurePacket Create(ClientPartyLeaveError error) => Plain(CmdPacketType.LEAVE_PARTY, error);
    public static ClientFailurePacket Create(ClientPartyKickError error) => Plain(CmdPacketType.WALKOUT_PARTY_MEMBER, error);
    public static ClientFailurePacket Create(ClientMoveItemError error, InventoryMoveRequest request) =>
        new(CmdPacketType.MOVE_ITEMSPACE, InventoryPackets.Move(request, InventoryResult.Reject(Code(error))));
    public static ClientFailurePacket Create(ClientUseItemError error, ConsumableUseRequest request) =>
        new(CmdPacketType.USE_STACKABLE, ConsumableUsePackets.Rejected(request, error));
    public static ClientFailurePacket Create(ClientRepairError error) => Plain(CmdPacketType.REPAIR_EQUIPMENT, error);
    public static ClientFailurePacket Create(ClientShopBuyError error) => Plain(CmdPacketType.BUY_ITEM, error);
    public static ClientFailurePacket Create(ClientShopSellError error) => Plain(CmdPacketType.SELL_ITEM, error);
    public static ClientFailurePacket Create(ClientCompoundError error) => Plain(CmdPacketType.COMPOUND_ITEM, error);
    public static ClientFailurePacket Create(ClientDisjointError error) => Plain(CmdPacketType.DISJOINT_ITEM, error);
    public static ClientFailurePacket Create(ClientSkillError error) => Plain(CmdPacketType.BUY_SKILL, error);
    public static ClientFailurePacket Create(ClientSkillTreeError error) => Plain(CmdPacketType.CHANGE_ANOTHER_SKILL_TREE, error);
    public static ClientFailurePacket Create(ClientClassChangeError error) => Plain(CmdPacketType.USE_RIGHT_OF_CHANGE_GROW_TYPE, error);
    public static ClientFailurePacket Create(ClientQuestAcceptError error) => Plain(CmdPacketType.ACCEPT_QUEST, error);
    public static ClientFailurePacket Create(ClientQuestGiveUpError error) => Plain(CmdPacketType.GIVEUP_QUEST, error);
    public static ClientFailurePacket Create(ClientQuestFinishError error) => Plain(CmdPacketType.FINISH_QUEST, error);
    public static ClientFailurePacket Create(ClientMailQueryError error) => Plain(CmdPacketType.QUERY_CHARAC_INFO_MAILBOX, error);
    public static ClientFailurePacket Create(ClientMailSendError error) => Plain(CmdPacketType.MAILBOX_SEND, error);
    public static ClientFailurePacket Create(ClientMailSaveError error) => Plain(CmdPacketType.CHANGE_LETTER_STAT, error);
    public static ClientFailurePacket Create(ClientGuildNameError error) => Plain(CmdPacketType.CHECK_GUILD_NAME_DOUBLE, error);
    public static ClientFailurePacket Create(ClientGuildInviteError error) => Plain(CmdPacketType.REQUEST_GUILD_CREATE_PERMIT, error);
    public static ClientFailurePacket Create(ClientGuildCertificationError error) => Plain(CmdPacketType.REPLY_GUILD_CREATE_PERMIT, error);
    public static ClientFailurePacket Create(ClientRaidCreateError error) => Plain(CmdPacketType.CREATE_RAID, error);
    public static ClientFailurePacket Create(ClientRaidStartError error) =>
        error is ClientRaidStartError.MembersMissingMaterials or ClientRaidStartError.MembersDailyClearLimit
            ? throw new NotSupportedException("ACK666 error17/217 requires count:u32 and u32 member values. Use the payload overload; a three-byte failure would truncate the client reader.")
            : Plain(CmdPacketType.START_RAID, error);
    public static ClientFailurePacket Create(ClientRaidWaitingError error) => Plain(CmdPacketType.SET_RAID_WAITING, error);
    public static ClientFailurePacket Create(ClientRaidModifyError error) => Plain(CmdPacketType.MODIFY_RAID_INFO, error);
    public static ClientFailurePacket Create(ClientAuctionCancelError error) => Plain(CmdPacketType.AUCTION_REGIST_CANCEL, error);
    public static ClientFailurePacket Create(ClientAuctionBuyError error) => Plain(CmdPacketType.AUCTION_BIDDING, error);

    // 当前消费者读完即丢弃这些值；保留调用方给出的原始u32，不猜成角色UID、队伍槽或补造成员。
    public static ClientFailurePacket Create(ClientRaidStartError error, ReadOnlySpan<uint> clientMemberValues)
    {
        ushort code = Code(error);
        if (error is not (ClientRaidStartError.MembersMissingMaterials or ClientRaidStartError.MembersDailyClearLimit))
            throw new ArgumentException("This ACK666 branch does not consume a member array.", nameof(error));
        // GameFrame正文上限为65520；这里只限制线格式容量，不另设业务人数门槛。
        if (clientMemberValues.Length > (65520 - 7) / 4) throw new ArgumentOutOfRangeException(nameof(clientMemberValues));
        byte[] body = new byte[7 + clientMemberValues.Length * 4];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), code);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3), (uint)clientMemberValues.Length);
        for (int index = 0; index < clientMemberValues.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7 + index * 4), clientMemberValues[index]);
        return new(CmdPacketType.START_RAID, body);
    }

    // ACK95 的 success=1 表示附件结果数组有效；非零逐行错误仍是领取失败，不表示资产已提交。
    public static ClientFailurePacket Create(ClientMailClaimError error, uint mailId, uint claimId)
    {
        ushort code = Code(error);
        byte[] body = new byte[17];
        body[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(5), mailId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(9), claimId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), code);
        return new(CmdPacketType.MAILBOX_EXTRACT_ITEM, body);
    }

    // 02359FF0 的114、127显示后仍落入读取面板模式的路径，其余已列错误直接收尾。
    // 此入口按匹配客户端公共u16前缀编码；历史byte参数入口也委托到此处，参数宽度不等于线格式宽度。
    public static ClientFailurePacket Create(ClientAuctionRegisterError error, byte mode)
    {
        if (mode > 1) throw new ArgumentOutOfRangeException(nameof(mode));
        var packet = Plain(CmdPacketType.AUCTION_REGIST_ITEM, error);
        return error is ClientAuctionRegisterError.TradeCountExhausted or ClientAuctionRegisterError.AvatarPreset
            ? packet with { Body = [.. packet.Body, mode] } : packet;
    }

    static ClientFailurePacket Plain<T>(CmdPacketType opcode, T error) where T : struct, Enum
    {
        ushort code = Code(error);
        return new(opcode, [0, (byte)code, (byte)(code >> 8)]);
    }

    internal static ushort Code<T>(T error) where T : struct, Enum =>
        Enum.IsDefined(error) ? Convert.ToUInt16(error) : throw new ArgumentOutOfRangeException(nameof(error), error,
            $"No verified native reply mapping for {typeof(T).Name}; a dstr index is not a wire error code.");
}
