using System.Collections.Immutable;

namespace DfoServer.Network.Builders;

// 这里只命名已闭合的字段；未定性的显示值必须由调用方显式提供，不设置伪造奖励的缺省值。
internal sealed record PvpArenaResultRow(ushort Character, uint Value, byte Grade,
    uint FirstValue, uint SecondValue, uint ThirdValue, uint FourthValue);
internal sealed record PvpArenaResultExtra(uint FirstValue, ushort SecondValue, byte ThirdValue);
internal sealed record PvpArenaResultTail(ushort FirstValue, ushort PromptValue, uint SecondValue,
    byte FlagValue, byte EventValue, uint EventArgument, ImmutableArray<PvpArenaResultExtra> Extra);

internal static class PvpArenaResultPackets
{
    // 023AF15F普通分支：11B头、每角色23B、15B尾及每追加项7B。
    // PromptValue=FFFF才跳过相应提示；EventValue原值也参与事件，不能统一清零。
    public static byte[] Ordinary(byte winner, uint winPoints, byte ownGrade, uint headerValue,
        ImmutableArray<PvpArenaResultRow> rows, PvpArenaResultTail tail)
    {
        if (rows.IsDefault || rows.Length > 8 || rows.Any(row => row is null || row.Character is 0 or ushort.MaxValue) ||
            rows.Select(row => row.Character).Distinct().Count() != rows.Length ||
            tail.Extra.IsDefault || tail.Extra.Length > byte.MaxValue || tail.Extra.Any(row => row is null))
            throw new InvalidDataException("决斗结果角色行或追加列表无效");
        using var stream = new MemoryStream(26 + 23 * rows.Length + 7 * tail.Extra.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write(winner);
        writer.Write(winPoints);
        writer.Write(ownGrade);
        writer.Write(headerValue);
        writer.Write(checked((byte)rows.Length));
        foreach (var row in rows)
        {
            writer.Write(row.Character);
            writer.Write(row.Value);
            writer.Write(row.Grade);
            writer.Write(row.FirstValue);
            writer.Write(row.SecondValue);
            writer.Write(row.ThirdValue);
            writer.Write(row.FourthValue);
        }
        writer.Write(tail.FirstValue);
        writer.Write(tail.PromptValue);
        writer.Write(tail.SecondValue);
        writer.Write(tail.FlagValue);
        writer.Write(tail.EventValue);
        writer.Write(tail.EventArgument);
        writer.Write(checked((byte)tail.Extra.Length));
        foreach (var extra in tail.Extra)
        {
            writer.Write(extra.FirstValue);
            writer.Write(extra.SecondValue);
            writer.Write(extra.ThirdValue);
        }
        return stream.ToArray();
    }
}
