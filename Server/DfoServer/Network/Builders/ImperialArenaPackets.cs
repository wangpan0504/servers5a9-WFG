using System.Collections.Immutable;
using DfoServer.Game.ImperialArena;

namespace DfoServer.Network.Builders;

internal static class ImperialArenaPackets
{
    public static byte[] Result(long elapsedMilliseconds)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        // 0177B5F0固定读取四个u32并打开303结果窗；0178AA70按毫秒绘制第三项。
        // 旧成绩与两个未接入的旧榜字段保持0；原生在旧成绩为0时复用本次真实耗时。
        writer.Write(0u); writer.Write(0u);
        writer.Write(checked((uint)elapsedMilliseconds)); writer.Write(0u);
        return stream.ToArray();
    }

    public static byte[] PreviousRank(uint rank)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); writer.Write(rank); writer.Write((byte)0);
        return stream.ToArray();
    }
    // 0177C190一次读13B；03C5D6B0把首u32显示为本人成绩、末u32显示为本周目标。
    public static byte[] Challenge(ImperialArenaChallenge challenge)
    {
        if (challenge.Team.Length != 4 || challenge.TargetMilliseconds == 0)
            throw new InvalidDataException("帝国每周挑战通知不完整");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(challenge.CurrentMilliseconds);
        foreach (byte value in challenge.Team) writer.Write(value);
        writer.Write(challenge.TargetMilliseconds);
        writer.Write(challenge.Claimable ? (byte)1 : (byte)0);
        return stream.ToArray();
    }

    public static byte[] ActiveTeam(ImperialArenaProjection projection)
    {
        var configuration = projection.State.Configuration;
        configuration.Validate();
        if (configuration.IsEmpty) return [0, 255, 0, 255];
        int first = projection.Roster.Entries.IndexOf(projection.Roster.Entries.SingleOrDefault(row => row.Id == configuration.First)!);
        int second = projection.Roster.Entries.IndexOf(projection.Roster.Entries.SingleOrDefault(row => row.Id == configuration.Second)!);
        if (first < 0 || second < 0) throw new InvalidDataException("帝国竞技场配置不在已发布名册");
        return [projection.Server, checked((byte)first), projection.Server, checked((byte)second)];
    }

    public static byte[] Configured(ImperialArenaProjection projection) => [1, .. ActiveTeam(projection)];
    public static byte[] Enterable(ImperialArenaState state)
    {
        // 01530E95用第三字节显示各对手的已清遮罩；017859C0只在首次清过更后房间时推进。
        int progress = state.PaidMode == ImperialArenaMode.Hard ? state.HardRewardRound : state.RewardedMonsters.Count;
        int maximum = state.PaidMode switch { ImperialArenaMode.None => 0, ImperialArenaMode.Normal => 5, ImperialArenaMode.Hard => 7, _ => -1 };
        if (progress < 0 || progress > maximum) throw new InvalidDataException("帝国当天已清前缀超出当前模式");
        return [state.PaidMode == ImperialArenaMode.None ? (byte)0 : (byte)1, (byte)state.PaidMode, checked((byte)progress)];
    }

    public static byte[] Opponents(ImperialArenaDailyRoute route)
    {
        if (route.Normal.Length != 5 || route.Hard.Length != 7 || route.NormalWeakness.Length != 5 || route.HardWeakness.Length != 7)
            throw new InvalidDataException("帝国每日对手通知必须完整对应双难度路线");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)4);
        for (int i = 0; i < 4; i++) { writer.Write(route.Normal[i].MonsterId); writer.Write(route.NormalWeakness[i]); }
        writer.Write((byte)1); writer.Write(route.Normal[4].MonsterId); writer.Write(route.NormalWeakness[4]);
        writer.Write((byte)7);
        for (int i = 0; i < 7; i++) { writer.Write(route.Hard[i].MonsterId); writer.Write(route.HardWeakness[i]); }
        writer.Write(route.CommonWeakness);
        return stream.ToArray();
    }

    // 0177B770收到1402后无论count是否为0都会打开201逐关KO奖励窗；仅普通模式实发单关奖励时构造。
    public static byte[] Reward(ImmutableArray<ImperialArenaRewardItem> reward)
    {
        if (reward.IsEmpty || reward.Length > 2) throw new InvalidDataException("帝国逐关奖励通知必须为1至2行");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(checked((byte)reward.Length));
        foreach (var row in reward) { writer.Write(checked((uint)row.ItemId)); writer.Write(checked((ushort)row.Count)); }
        return stream.ToArray();
    }
}
