using System.Buffers.Binary;
using DfoServer.Game.Strongway2019;

namespace DfoServer.Network.Builders;

internal static class Strongway2019ProfilePackets
{
    internal static byte[] UserData(Strongway2019RewardState challenge, Strongway2019RewardState matching)
    {
        // 四项仅保持本客户端构造时的原始默认，不赋予日次数或分难度统计含义。
        const uint RawReserved = 0;
        uint[] fields = [RawReserved, RawReserved, RawReserved, checked((uint)challenge.WeeklyUsed),
            RawReserved, checked((uint)matching.WeeklyUsed)];
        var body = new byte[24];
        for (int i = 0; i < fields.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(i * 4), fields[i]);
        return body;
    }

    internal static byte[] CharacterData(Strongway2019Statistics state, int startDelaySeconds)
    {
        if (startDelaySeconds < 0 || state.NormalBestMode is not (0 or 2))
            throw new InvalidDataException("首期历史模式或开场延迟无效");
        uint rawMatching = state.MatchingBestElapsedMilliseconds == 0 ? 0 :
            checked(state.MatchingBestElapsedMilliseconds + checked((uint)startDelaySeconds * 1000));
        var body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body, state.MatchingWins);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), rawMatching);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), state.NormalBestMode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), state.NormalBestElapsedMilliseconds);
        return body;
    }
}
