using System.Collections.Immutable;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassRequestTests
{
    public static void RunCore()
    {
        int before = Count;
        byte[] mission = [
            0x17, 0xA3, 0x5D, 0xE1, 0x39, 0xC7, 0x6B, 0xF2, 0x84, 0x1E, 0xB6, 0x49, 0xDA,
            0xD7, 0x31, 0xE2, 0xEF, 0xCD, 0xAB, 0x89, 0x97, 0xB5, 0xD1, 0xF3];
        byte[] reward = [
            0xE3, 0x21, 0x9B, 0x47, 0xD5, 0x68, 0xA1, 0x3F, 0xC9, 0x72, 0x16, 0xBD, 0x54,
            0xBC, 0xE5, 0x87, 0xA9, 0xCB, 0xED];
        byte[] missionPrefix = [0x17, 0xA3, 0x5D, 0xE1, 0x39, 0xC7, 0x6B, 0xF2, 0x84, 0x1E, 0xB6, 0x49, 0xDA];
        byte[] rewardPrefix = [0xE3, 0x21, 0x9B, 0x47, 0xD5, 0x68, 0xA1, 0x3F, 0xC9, 0x72, 0x16, 0xBD, 0x54];
        var parsedMission = BattlePassRequests.Mission(mission);
        var parsedReward = BattlePassRequests.Reward(reward);
        CheckMission(parsedMission, missionPrefix);
        CheckReward(parsedReward, rewardPrefix);
        byte[] reusedMission = (byte[])mission.Clone();
        byte[] reusedReward = (byte[])reward.Clone();
        var snapshotMission = BattlePassRequests.Mission(reusedMission);
        var snapshotReward = BattlePassRequests.Reward(reusedReward);
        Array.Fill(reusedMission, (byte)0xFF);
        Array.Fill(reusedReward, (byte)0xFF);
        CheckMission(snapshotMission, missionPrefix);
        CheckReward(snapshotReward, rewardPrefix);
        byte[] zeroMission = (byte[])mission.Clone();
        byte[] zeroReward = (byte[])reward.Clone();
        Array.Clear(zeroMission, 0, 13);
        Array.Clear(zeroReward, 0, 13);
        CheckMission(BattlePassRequests.Mission(zeroMission), new byte[13]);
        CheckReward(BattlePassRequests.Reward(zeroReward), new byte[13]);
        Reject<InvalidDataException>(() => BattlePassRequests.Mission([]), "战令任务拒绝空正文");
        Reject<InvalidDataException>(() => BattlePassRequests.Reward([]), "战令奖励拒绝空正文");
        for (int size = 1; size < mission.Length; size++)
        {
            byte[] shortBody = mission[..size];
            Reject<InvalidDataException>(() => BattlePassRequests.Mission(shortBody), "战令任务拒绝所有短正文");
        }
        for (int size = 1; size < reward.Length; size++)
        {
            byte[] shortBody = reward[..size];
            Reject<InvalidDataException>(() => BattlePassRequests.Reward(shortBody), "战令奖励拒绝所有短正文");
        }
        Reject<InvalidDataException>(() => BattlePassRequests.Mission([.. mission, 0x9A]), "战令任务拒绝多余尾字节");
        Reject<InvalidDataException>(() => BattlePassRequests.Reward([.. reward, 0xC3]), "战令奖励拒绝多余尾字节");
        Reject<InvalidDataException>(() => BattlePassRequests.Mission([0xD7, 0x31, 0xE2, 0xEF, 0xCD, 0xAB, 0x89, 0x97, 0xB5, 0xD1, 0xF3]),
            "11B业务尾不能冒完整任务正文");
        Reject<InvalidDataException>(() => BattlePassRequests.Reward([0xBC, 0xE5, 0x87, 0xA9, 0xCB, 0xED]),
            "6B业务尾不能冒完整奖励正文");
        Reject<InvalidDataException>(() => BattlePassRequests.Mission([0x01, 0xCF, 0x06, 0x25, 0, 0, 0, 0x2A, 0x19, 0x77, 0x83, 0x35, 0x91, .. mission]),
            "任务Parser不接受37B外帧");
        Reject<InvalidDataException>(() => BattlePassRequests.Reward([0x01, 0xD0, 0x06, 0x20, 0, 0, 0, 0x3B, 0x28, 0x66, 0x92, 0x44, 0x80, .. reward]),
            "奖励Parser不接受32B外帧");
        Console.WriteLine($"PASS battle pass request parser assertions {Count - before}");
    }

    static void CheckMission(BattlePassMissionRequest request, byte[] prefix) =>
        That(Prefix(request.OpaquePrefix13, prefix) && request.RawActionCode == 0xD7 && request.RowKey == 0xE231 &&
            request.RawCategoryCode == 0x89ABCDEFu && request.RowIndex == 0xF3D1B597u,
            "战令任务完整保留13B前缀与raw无符号LE字段，不把未知值当业务动作");

    static void CheckReward(BattlePassRewardRequest request, byte[] prefix) =>
        That(Prefix(request.OpaquePrefix13, prefix) && request.RawActionCode == 0xBC && request.RawTrackCode == 0xE5 &&
            request.Key == 0xEDCBA987u, "战令奖励完整保留13B前缀与raw无符号LE字段，不缩窄轨道");

    static bool Prefix(ImmutableArray<byte> actual, byte[] expected) =>
        !actual.IsDefault && actual.AsSpan().SequenceEqual(expected);
}
