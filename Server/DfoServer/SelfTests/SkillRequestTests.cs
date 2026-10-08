using static DfoServer.Testing.TestCheck;
using DfoServer.Network.Parsers;

namespace DfoServer;

internal static class SkillRequestTests
{
    public static void Run()
    {
        // 合成正文验证发送读宽与保真，不作为真实学习请求或合法业务取值的样本。
        byte[] body = [0xFF, 2, 5, 0, 0, 1, 0x34, 0x12, 0xFE, 0xFF, 3];
        var parsed = SkillRequests.ParsePurchase(body);
        That(parsed.Context == 0xFF && parsed.Mode == 3 &&
            parsed.Items.SequenceEqual(new[] { new SkillPurchaseItem(5, 0, 1), new SkillPurchaseItem(0x1234, 0xFE, 0xFF) }),
            "学习正文保留上下文、未知方向域、变更量及little-endian技能ID");
        body[0] = 0;
        body[2] = 0;
        That(parsed.Context == 0xFF && parsed.Items[0].SkillId == 5, "解析结果不依赖可变输入缓冲区");
        var duplicate = SkillRequests.ParsePurchase([0, 2, 5, 0, 0, 1, 5, 0, 1, 2, 0xFF]);
        That(duplicate.Items[0].SkillId == duplicate.Items[1].SkillId && duplicate.Mode == 0xFF,
            "结构解析不擅自合并技能或规范化未知mode");
        That(SkillRequests.ParsePurchase([0x80, 0, 0xFE]).Items.Count == 0,
            "零条目结构保留，业务准入另行判断");
        byte[] maximum = new byte[3 + byte.MaxValue * 4];
        maximum[1] = byte.MaxValue;
        maximum[^1] = 0x7F;
        That(SkillRequests.ParsePurchase(maximum).Items.Count == 255, "数量255不溢出长度计算");
        Reject([]);
        Reject([0]);
        Reject([0, 0]);
        Reject([0, 1, 0]);
        Reject([0, 0, 0, 0]);
        Reject(maximum[..^1]);
        Reject([.. maximum, 0]);
        Reject([0, 255, 0]);
        Reject([29, 0, 0, 1, 5, 0, 0, 1, 0]);
        Console.WriteLine("PASS 技能学习正文结构、输入保真及长度拒绝");
    }


    private static void Reject(byte[] body)
    {
        try
        {
            SkillRequests.ParsePurchase(body);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException("FAIL: 未拒绝错误学习正文长度");
    }
}
