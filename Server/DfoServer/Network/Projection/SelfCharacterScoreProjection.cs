using DfoServer.Game.Characters;
using DfoServer.Game.Chat;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 本服有意扩展普通聊天的一条精确命令，不是任意文本/GM命令解释器。
internal sealed class SelfCharacterScoreProjection(
    Func<CharacterInvestmentSnapshot, CharacterInvestmentResult> calculate,
    Func<CharacterInvestmentSnapshot, long?> boxExpiration)
{
    public SelfCharacterScoreProjection(CharacterInvestmentService service) : this(service.Calculate, service.BoxExpiration) { }

    public static bool Matches(ChatSend send) => send.Type == (byte)ChatKind.Normal &&
        send.TargetName is null && string.Equals(send.Text, "#战力", StringComparison.Ordinal);

    public string Query(GameConnectionContext context)
    {
        var (capture, status) = context.Session.TryBeginSelfCharacterScore();
        if (capture is null) return status;
        try
        {
            return CharacterScoreProjection.Send(context.Session, capture, calculate, boxExpiration,
                (opcode, body, action, current) => context.Sender.TryPost(0, opcode, body, action,
                    () => !context.Stop.IsCancellationRequested && current())).Status;
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FileNotFoundException or FormatException or OverflowException)
        {
            // 不把不可计算伪作零分，不广播输入或吐出资源路径；下一次合法查询仍可重试。
            return "unavailable";
        }
        finally { context.Session.EndSelfCharacterScore(capture); }
    }
}
