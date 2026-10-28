using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ClientPromptTests
{
    public static void Run()
    {
        int before = Count;
        var move = new InventoryMoveRequest(new(InventoryListType.Main, 13), 1000, 1,
            new(InventoryListType.PersonalCargo, 7), 0, 0, -1, 0, 0);
        var use = new ConsumableUseRequest(new(InventoryListType.Main, 23), 0x44332211, 0x08070605, 0);
        Check<ClientPartyHostError>(121, 3, error => ClientFailurePackets.Create(error));
        Check<ClientGuildCreateRightError>(68, 3, error => ClientFailurePackets.Create(error));
        Check<ClientGuildLevelError>(73, 3, error => ClientFailurePackets.Create(error));
        Check<ClientGuildJoinError>(75, 3, error => ClientFailurePackets.Create(error));
        Check<ClientGuildIntroductionError>(739, 3, error => ClientFailurePackets.Create(error));
        Check<ClientGuildPromoteChangeError>(740, 3, error => ClientFailurePackets.Create(error));
        Check<ClientCharacterNameError>(692, 3, error => ClientFailurePackets.Create(error));
        Check<ClientPeerRequestError>(10, 4, error => ClientFailurePackets.Create(error, 2));
        Check<ClientPeerResponseError>(11, 4, error => ClientFailurePackets.Create(error, 2));
        Check<ClientPartySettingsError>(12, 3, error => ClientFailurePackets.Create(error));
        Check<ClientPartyLeaveError>(13, 3, error => ClientFailurePackets.Create(error));
        Check<ClientPartyKickError>(14, 3, error => ClientFailurePackets.Create(error));
        Check<ClientMoveItemError>(19, 5, error => ClientFailurePackets.Create(error, move));
        Check<ClientUseItemError>(44, 12, error => ClientFailurePackets.Create(error, use));
        Check<ClientRepairError>(23, 3, error => ClientFailurePackets.Create(error));
        Check<ClientShopBuyError>(21, 3, error => ClientFailurePackets.Create(error));
        Check<ClientShopSellError>(22, 3, error => ClientFailurePackets.Create(error));
        Check<ClientCompoundError>(25, 3, error => ClientFailurePackets.Create(error));
        Check<ClientDisjointError>(26, 3, error => ClientFailurePackets.Create(error));
        Check<ClientSkillError>(29, 3, error => ClientFailurePackets.Create(error));
        Check<ClientSkillTreeError>(260, 3, error => ClientFailurePackets.Create(error));
        Check<ClientClassChangeError>(502, 3, error => ClientFailurePackets.Create(error));
        Check<ClientQuestAcceptError>(31, 3, error => ClientFailurePackets.Create(error));
        Check<ClientQuestGiveUpError>(32, 3, error => ClientFailurePackets.Create(error));
        Check<ClientQuestFinishError>(34, 3, error => ClientFailurePackets.Create(error));
        Check<ClientMailQueryError>(324, 3, error => ClientFailurePackets.Create(error));
        Check<ClientMailSendError>(94, 3, error => ClientFailurePackets.Create(error));
        Check<ClientMailClaimError>(95, 17, error => ClientFailurePackets.Create(error, 0x11223344, 0x55667788), claim: true);
        Check<ClientMailSaveError>(134, 3, error => ClientFailurePackets.Create(error));
        Check<ClientGuildNameError>(156, 3, error => ClientFailurePackets.Create(error));
        Check<ClientGuildInviteError>(741, 3, error => ClientFailurePackets.Create(error));
        Check<ClientGuildCertificationError>(742, 3, error => ClientFailurePackets.Create(error));
        Check<ClientRaidCreateError>(664, 3, error => ClientFailurePackets.Create(error));
        Check<ClientRaidStartError>(666, 3, error =>
            error is ClientRaidStartError.MembersMissingMaterials or ClientRaidStartError.MembersDailyClearLimit
                ? ClientFailurePackets.Create(error, ReadOnlySpan<uint>.Empty) : ClientFailurePackets.Create(error), members: true);
        Check<ClientRaidWaitingError>(667, 3, error => ClientFailurePackets.Create(error));
        Check<ClientRaidModifyError>(670, 3, error => ClientFailurePackets.Create(error));
        Check<ClientAuctionRegisterError>(183, null, error => ClientFailurePackets.Create(error, 1));
        Check<ClientAuctionCancelError>(184, 3, error => ClientFailurePackets.Create(error));
        Check<ClientAuctionBuyError>(185, 3, error => ClientFailurePackets.Create(error));

        Bytes(ClientFailurePackets.Create(ClientSkillError.SpInsufficient).Body, [0, 22, 0],
            "技能SP不足发送错误22，不发送dstr770或自由文本");
        Bytes(ClientFailurePackets.Create(ClientClassChangeError.AdvancementQuestInProgress).Body, [0, 22, 0],
            "职业互转同号错误仍绑定自己的CMD502和dstr27188");
        Bytes(ClientFailurePackets.Create(ClientMailQueryError.RecipientMissing).Body, [0, 21, 0],
            "邮件查收件人不存在使用21，不沿用寄送的3");
        Bytes(ClientFailurePackets.Create(ClientMailSendError.RecipientMissing).Body, [0, 3, 0],
            "邮件寄送与查收件人独立错误域");
        Bytes(ClientFailurePackets.Create(ClientPeerResponseError.RefusedOrOffline, 1).Body, [0, 133, 0, 1],
            "拒绝玩家交互保留mode，客户端按交易或组队上下文选词");
        Bytes(ClientFailurePackets.Create(ClientMoveItemError.CannotMove, move).Body, [0, 23, 0, 0, 2],
            "物品移动失败保留两容器，不截成三字节");
        Bytes(ClientFailurePackets.Create(ClientUseItemError.JobMismatch, use).Body,
            Convert.FromHexString("000800001122334405060708"), "使用道具失败保留容器和两个来源字段");
        Bytes(ClientFailurePackets.Create(ClientMailClaimError.InventoryFull, 0x11223344, 0x55667788).Body,
            Convert.FromHexString("0101000000443322118877665504000000"), "附件失败使用逐行u32错误，不伪造领取成功或整包失败");
        Bytes(ClientFailurePackets.Create(ClientAuctionRegisterError.TradeCountExhausted, 1).Body, [0, 114, 0, 1],
            "拍卖114使用公共u16并保留后继读取的mode");
        Bytes(ClientFailurePackets.Create(ClientAuctionRegisterError.SecurityMode, 0).Body, [0, 210, 0],
            "拍卖直接收尾分支只写完整公共错误前缀");
        Reject<ArgumentOutOfRangeException>(() => ClientFailurePackets.Create(ClientAuctionRegisterError.SecurityMode, 2),
            "未确认的面板模式不写入新原生错误入口");
        Bytes(ClientFailurePackets.Create(DungeonSelectionError.MemberFatigueOrClearCondition, 1).Body, [0, 22, 0, 1],
            "副本提示继续使用原生错误码和实际成员槽");
        Bytes(CharacterPackets.NameCheck(true), [0, 24, 0], "旧名称查重接入类型入口后字节不变");

        foreach (var error in new[] { ClientRaidStartError.MembersMissingMaterials, ClientRaidStartError.MembersDailyClearLimit })
            Reject<NotSupportedException>(() => ClientFailurePackets.Create(error), "已登记但缺少必读成员数组时报告底层缺口，不截断客户端读取");
        Bytes(ClientFailurePackets.Create(ClientRaidStartError.MembersMissingMaterials, new uint[] { 0x11223344, 0x55667788 }).Body,
            Convert.FromHexString("001100020000004433221188776655"), "未接入业务的提示也保留原生码、完整数组和调用方的u32原值");
        Reject<ArgumentException>(() => ClientFailurePackets.Create(ClientRaidStartError.LeaderRequired, new uint[] { 1 }),
            "不读取数组的分支不能误附成员载荷");
        Reject<ArgumentOutOfRangeException>(() => ClientFailurePackets.Create(ClientRaidStartError.MembersMissingMaterials, new uint[16379]),
            "成员数组只按完整帧上限约束，不进行业务人数猜测");
        foreach (var prompt in Enum.GetValues<ClientUnboundDstr>())
        {
            string problem = ClientPromptDiagnostics.Explain(prompt);
            That(problem.Contains(((uint)prompt).ToString()) && problem.Contains("consumer") && problem.Length > 100,
                "未接入或未闭合文本仍有索引、消费者及实际底层缺口，不能空放在目录");
            Reject<NotSupportedException>(() => ClientFailurePackets.Create(prompt), "未闭合映射不能变成文本通知、静默失败或伪造成功");
        }
        That(ClientPromptDiagnostics.Explain(ClientUnboundDstr.PartyMentorFriendListFull).Contains("Mentorship") &&
            ClientPromptDiagnostics.Explain(ClientUnboundDstr.RepairSucceeded).Contains("committed repair"),
            "未实现业务和必须真实成功回执分别报告，不混淆为同一种未知错误");
        Reject<ArgumentOutOfRangeException>(() => ClientPromptDiagnostics.Explain((ClientUnboundDstr)uint.MaxValue),
            "未知索引与已登记但未闭合的功能分开报告");

        Notice();
        Console.WriteLine($"PASS {Count - before} 原生业务错误包、必读尾部、NOTI502显示分支及边界断言");
    }

    static void Check<T>(int opcode, int? length, Func<T, ClientFailurePacket> build, bool claim = false, bool members = false) where T : struct, Enum
    {
        foreach (T error in Enum.GetValues<T>())
        {
            var packet = build(error);
            ushort code = Convert.ToUInt16(error);
            int expectedLength = members && code is (17 or 217) ? 7 : length ?? (code is 114 or 127 ? 4 : 3);
            uint actual = claim ? BinaryPrimitives.ReadUInt32LittleEndian(packet.Body.AsSpan(13)) :
                BinaryPrimitives.ReadUInt16LittleEndian(packet.Body.AsSpan(1));
            That((ushort)packet.Opcode == opcode && packet.Body.Length == expectedLength &&
                packet.Body[0] == (claim ? 1 : 0) && actual == code,
                $"{typeof(T).Name}.{error}绑定本业务opcode、原始错误宽度及必需尾部");
        }
        T invalid = (T)Enum.ToObject(typeof(T), ushort.MaxValue);
        Reject<ArgumentOutOfRangeException>(() => build(invalid), "未登记的业务错误不能靠强转冒充已核验分支");
    }

    static void Notice()
    {
        foreach (var display in Enum.GetValues<ServerNoticeDisplay>().Where(display => (byte)display <= 3))
        {
            byte[] expected = Convert.FromHexString("0008000000B2C4C1CFB2BBD7E3");
            expected[0] = (byte)display;
            Bytes(ServerNoticePackets.Message(display, "材料不足"), expected, "502显式选择显示分支，正文为CP936字节串而非dstr索引");
            That(ServerNoticePackets.Message(display, new string('a', 1023)).Length == 1028, "502接受客户端严格小于1024的最大正文");
            foreach (string invalid in new[] { "", new string('a', 1024), new string('中', 512) })
                Reject<ArgumentOutOfRangeException>(() => ServerNoticePackets.Message(display, invalid), "502不发送空串或越界字节段");
        }
        Bytes(ServerNoticePackets.Information("材料不足"), Convert.FromHexString("0008000000B2C4C1CFB2BBD7E3"),
            "旧type0接口字节保持不变");
        Bytes(ServerNoticePackets.Chat("材料不足"), Convert.FromHexString("0208000000B2C4C1CFB2BBD7E3"),
            "type2复用502系统聊天，不拼造普通玩家聊天层号");
        foreach (var unsupported in new[] { ServerNoticeDisplay.ItemList, ServerNoticeDisplay.ToastWithOptions })
            Reject<NotSupportedException>(() => ServerNoticePackets.Message(unsupported, "x"), "未接入的502分支也保留枚举并明确报告缺少的载荷");
        Reject<ArgumentOutOfRangeException>(() => ServerNoticePackets.Message((ServerNoticeDisplay)255, "x"), "未知502类型与已知但未支持的类型分开处理");
        Reject<ArgumentNullException>(() => ServerNoticePackets.Information(null!), "502拒绝空引用");
    }
}
