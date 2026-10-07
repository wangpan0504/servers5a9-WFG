using System.Buffers.Binary;
using DfoServer.Network.Parsers;
using DfoServer.Testing;

namespace DfoServer;

internal static class SkillLearningDisconnectTests
{
    public static void Run()
    {
        // 正式连接实收11B；旧Parser将发送前追加的检查值误判为多余正文并断开。
        byte[] captured = Convert.FromHexString("0001BE00000100B81A0000");
        var parsed = SkillRequests.ParsePurchase(captured);
        TestCheck.That(parsed.Context == 0 && parsed.Mode == 0 &&
            parsed.Items.SequenceEqual(new[] { new SkillPurchaseItem(190, 0, 1) }) &&
            parsed.BodyChecksum == 0x00001AB8 && parsed.ChecksumMatches == true,
            "真实学习11B正文保留技能190增量及匹配检查值，不因合法尾部断开");
        var direct = SkillRequests.ParsePurchase(captured.AsSpan(0, 7));
        TestCheck.That(direct.Context == parsed.Context && direct.Mode == parsed.Mode &&
            direct.Items.SequenceEqual(parsed.Items) && direct.BodyChecksum is null && direct.ChecksumMatches is null,
            "直接写入7B与真实11B解析相同业务输入，直接形状不补检查值");
        captured[2] = 0;
        TestCheck.That(parsed.Items[0].SkillId == 190 && parsed.BodyChecksum == 0x00001AB8,
            "真实学习请求结果冻结，不借用可变包缓冲区");

        byte[] batch = [1, 2, 5, 0, 0, 1, 46, 0, 0, 1, 1];
        var checkedBatch = SkillRequests.ParsePurchase(AppendChecksum(batch));
        TestCheck.That(checkedBatch.Context == 1 && checkedBatch.Mode == 1 && checkedBatch.Items.Count == 2 &&
            checkedBatch.Items[0].SkillId == 5 && checkedBatch.Items[1].SkillId == 46 && checkedBatch.ChecksumMatches == true,
            "双树批量学习mode读取直接正文末端，不读检查值末字节");
        byte[] changedChecksum = AppendChecksum(batch);
        changedChecksum[^1] ^= 1;
        var mismatch = SkillRequests.ParsePurchase(changedChecksum);
        TestCheck.That(mismatch.ChecksumMatches == false && mismatch.Mode == 1 && mismatch.Items.SequenceEqual(checkedBatch.Items),
            "检查值不匹配保留显式状态，结构解析不替代owner、资源和点数资格");

        byte[] maximum = new byte[3 + byte.MaxValue * 4];
        maximum[1] = byte.MaxValue;
        maximum[^1] = 1;
        TestCheck.That(SkillRequests.ParsePurchase(AppendChecksum(maximum)).Items.Count == 255,
            "带检查值255项正文不发生数量或长度溢出");
        foreach (int extra in new[] { 1, 2, 3, 5 })
            Reject([.. batch, .. new byte[extra]]);
        Reject(batch[..^1]);
        // 缺少直接正文mode的短包不能用不足4B的尾部蒙混通过。
        Reject(Convert.FromHexString("0001BE000001B81A0000"));
        Console.WriteLine("PASS 技能学习实收检查尾、业务字段保真及严格长度边界；非实机学习验收");
    }

    private static byte[] AppendChecksum(byte[] body)
    {
        byte[] result = new byte[body.Length + 4];
        body.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(body.Length), ClientBodyChecksum.Compute(body));
        return result;
    }

    private static void Reject(byte[] body)
    {
        try
        {
            SkillRequests.ParsePurchase(body);
        }
        catch (InvalidDataException)
        {
            TestCheck.That(true, "未证学习正文长度继续拒绝");
            return;
        }
        throw new InvalidOperationException("未拒绝错误学习正文长度");
    }
}
