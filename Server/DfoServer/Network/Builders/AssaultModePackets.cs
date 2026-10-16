namespace DfoServer.Network.Builders;

// #233 实验：强袭模式每日信息 NOTI1536。匹配客户端 00A81C80 连读5个u8，依次写入强袭管理器 +1C..+20；
// +1E 为今日 day 索引，00A810F0 用它查 assaultmodedungeoninfo.etc 的 [daily open dungeon]，默认 0xFF 时提示“今天无法入场。”(60723)。
// +1C/+1D 为当日计数（方向待确认），+1F/+20 含义未确认，实验固定为0；不代表强袭准入、计数或扣材料已实现。
internal static class AssaultModePackets
{
    // 实验覆盖：DFO_ASSAULT_DAY_INDEX=0..6 时直接使用该索引，便于实机核对映射。
    public const string DayIndexOverrideVariable = "DFO_ASSAULT_DAY_INDEX";

    // ACK1547（客户端 00A81E00）：通用 ACK 前缀 success:u8；成功后连读两个 u32（3C85290×2）。
    // 第一个为任务副本 id，-1 时不继续；交给 A81B50 校验（A80D50 按任务副本索引查 mission info list）后发 C2S15/C2S16。
    // 第二个 u32 读入后客户端未使用，含义待确认，固定写0。失败为 00 + error:u16，仅 236(0xEC) 有专用提示(22EB600)，
    // 其余错误码静默并在选图模块(==2)时刷新；当前统一回1，具体错误码映射待确认。
    public static byte[] SelectAccepted(uint missionDungeon)
    {
        var body = new byte[9];
        body[0] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), missionDungeon);
        return body;
    }

    public const ushort SelectRejectedError = 1;
    public static byte[] SelectRejected(ushort error = SelectRejectedError) => [0, (byte)error, (byte)(error >> 8)];

    public static byte[] DailyInfo(byte clearCount, byte hiddenCount, byte dayIndex) => [clearCount, hiddenCount, dayIndex, 0, 0];

    // PVF day0/day6 的隐藏副本均为7326(weekend)，day1..5 依次为周一..周五，故按 DayOfWeek（0=周日）映射；重置时刻待确认，暂用本地自然日。
    public static byte DayIndex(DateTimeOffset localNow, string? overrideValue)
    {
        if (byte.TryParse(overrideValue, out byte forced) && forced <= 6) return forced;
        return (byte)localNow.DayOfWeek;
    }

    // b0=当日普通任务通关次数，b1=隐藏关卡通关次数（客户端以 3-b0 / 1-b1 显示剩余），b3/b4 含义待确认固定0。
    public static byte[] Today(TimeProvider clock, DfoServer.Game.Dungeons.AssaultDailyCounts counts = default) =>
        DailyInfo((byte)Math.Clamp(counts.Clears, 0, 255), (byte)Math.Clamp(counts.Hidden, 0, 255),
            DayIndex(clock.GetLocalNow(), Environment.GetEnvironmentVariable(DayIndexOverrideVariable)));
}