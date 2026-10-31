using DfoServer.Game.Marriage;

namespace DfoServer.Network.Builders;

// 每个结果只绑定本版对应回调；相同负数在不同命令中不代表同一业务原因。
internal static class MarriageFailurePackets
{
    public static int Unspecified(ushort command, byte operation = 0) => command switch
        { 1034 => -3, 1035 => operation == 7 ? -3 : -7, 1036 => -2, _ => -1 };

    public static int MissingRelation(ushort command) => command switch
    {
        1038 => -4, 1039 => -3,
        1047 or 1048 or 1050 or 1051 or 1052 or 1053 or 1054 or 1055 or 1056 => -2,
        _ => Unspecified(command)
    };

    public static int WrongPlace(ushort command) => command switch
    {
        1054 => -6, 1055 => -7, 1056 => -8, _ => Unspecified(command)
    };

    public static int Rule(ushort command, MarriageFailureReason reason, byte operation = 0) => (command, reason) switch
    {
        (1038, MarriageFailureReason.NotMarried) => -4,
        (1038, MarriageFailureReason.InvalidRingLevel) => -2,
        (1038, MarriageFailureReason.MissingItem) => -7,
        (1039, MarriageFailureReason.NotMarried) => -3,
        (1039, MarriageFailureReason.LovePointLimit) => -2,
        (1035, MarriageFailureReason.AlreadyReserved) when operation == 2 => -8,
        (1035, MarriageFailureReason.TasksIncomplete) when operation == 2 => -9,
        (1035, MarriageFailureReason.WeddingDailyLimit) when operation == 2 => -11,
        (1035, MarriageFailureReason.HallUnavailable) when operation == 2 => -3,
        (1036, MarriageFailureReason.InvalidWedding) => -1,
        (1036, MarriageFailureReason.GiftClosed) => -4,
        (1036, MarriageFailureReason.AlreadyGifted) => -3,
        (1036, MarriageFailureReason.GiftTooSmall) => -6,
        (1036, MarriageFailureReason.GiftTooLarge) => -7,
        (1040, MarriageFailureReason.NoInvitations) => -4,
        (1040, MarriageFailureReason.DuplicateInvitation) => -5,
        (1040, MarriageFailureReason.InvalidInvitationTarget) => -6,
        _ => Unspecified(command, operation)
    };

    public static byte[] Body(ushort command, int result, uint target = 0, byte operation = 0,
        byte response = 1, byte extraA = 0, byte extraB = 0, uint commentId = 0, bool close = false)
    {
        if (result == 0) throw new ArgumentException("失败回复不能携带成功结果", nameof(result));
        return command switch
        {
            1034 => MarriageWeddingPackets.RequestAck(result, target, operation, extraA, extraB),
            1035 => MarriageWeddingPackets.ResponseAck(result, target, operation, response, extraA, extraB),
            1036 => MarriageWeddingPackets.GiftAck(result),
            1037 => [0],
            1038 => MarriageWeddingPackets.RingAck(result, 0),
            1039 => MarriageWeddingPackets.ResultAck(result),
            1040 => MarriageWeddingPackets.LetterAck(result),
            1041 or 1042 => MarriageWeddingPackets.ResultAck(result),
            1043 => MarriageCouplePackets.DeleteCommentAck(result, commentId),
            1044 => MarriageCouplePackets.CommentsAck(result, 0, []),
            1045 => MarriageCouplePackets.PartnerCheckAck(result, 0, 0, 0, 0, 0, 0),
            // 1046为空回调，1049仅结束等待；不能伪装成01成功并追加它们不读取的错误值。
            1046 or 1049 => [0],
            1047 => MarriageCouplePackets.OpenCloseAck(result, close),
            1048 or 1050 or 1051 or 1052 or 1053 => MarriageCouplePackets.Result(result),
            // 这三条先读i32再检查ACK标志；失败仍保留完整结果，不追加成功尾字段。
            1054 or 1055 or 1056 => MarriageCouplePackets.CapsuleAck(command, result, new()),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
    }
}
