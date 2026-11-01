using DfoServer.Game.Characters;
using DfoServer.Game.Chat;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 本服有意扩展普通聊天的一条精确命令，不是任意文本/GM命令解释器。
internal sealed class SelfCharacterScoreProjection(
    Func<CharacterInvestmentSnapshot, CharacterInvestmentResult> calculate,
    Func<CharacterInvestmentSnapshot, long?> boxExpiration,
    Func<CharacterInvestmentSnapshot, CombatPowerResult>? combat = null)
{
    public SelfCharacterScoreProjection(CharacterInvestmentService service, CombatPowerService? combat = null)
        : this(service.Calculate, service.BoxExpiration, combat is null ? null : combat.Calculate) { }

    public static bool Matches(ChatSend send) => send.Type == (byte)ChatRequestType.Normal &&
        send.TargetName is null && string.Equals(send.Text, "#战力", StringComparison.Ordinal);

    internal const string MachineCommand = "#cp-score/1";
    // 版本2（官方风格战斗力乘区）；与版本1同长度、同nonce格式，回 CP_SCORE/2。
    internal const string CombatMachineCommand = "#cp-score/2";
    internal static bool MachineNamespace(ChatSend send) => send.Type == (byte)ChatRequestType.Normal && send.TargetName is null &&
        (send.Text.StartsWith(MachineCommand, StringComparison.Ordinal) || send.Text.StartsWith(CombatMachineCommand, StringComparison.Ordinal)) &&
        (send.Text.Length == MachineCommand.Length || char.IsWhiteSpace(send.Text[MachineCommand.Length]));
    internal static int MachineVersion(ChatSend send) =>
        MachineNamespace(send) && send.Text.StartsWith(CombatMachineCommand, StringComparison.Ordinal) ? 2 : 1;
    internal static string? MachineNonce(ChatSend send) => MachineNamespace(send) &&
        send.Text.Length == MachineCommand.Length + 33 && send.Text[MachineCommand.Length] == ' ' &&
        MachineCharacterScorePackets.Nonce(send.Text[(MachineCommand.Length + 1)..]) ? send.Text[(MachineCommand.Length + 1)..] : null;

    public string Query(GameConnectionContext context, string? nonce = null, int machineVersion = 1)
    {
        if (machineVersion == 2 && (nonce is null || combat is null)) return "unavailable";
        var (capture, status) = context.Session.TryBeginSelfCharacterScore();
        if (capture is null) return status;
        try
        {
            return CharacterScoreProjection.Send(context.Session, capture, calculate, boxExpiration,
                (opcode, body, action, current) => context.Sender.TryPost(0, opcode, body, action,
                    () => !context.Stop.IsCancellationRequested && current()), nonce,
                machineVersion == 2 ? combat : null).Status;
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FileNotFoundException or FormatException or OverflowException)
        {
            // 不把不可计算伪作零分，不广播输入或吐出资源路径；下一次合法查询仍可重试。
            return "unavailable";
        }
        finally { context.Session.EndSelfCharacterScore(capture); }
    }
}
