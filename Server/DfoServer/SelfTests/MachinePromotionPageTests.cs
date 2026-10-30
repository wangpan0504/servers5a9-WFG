using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using DfoServer.Game.CashShopPromotions;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MachinePromotionPageTests
{
    const string Nonce = "0123456789abcdef0123456789abcdef";
    static readonly int[] RewardIds = [490705441, 490705442, 490705443, 490705444, 490705445,
        490705446, 490705447, 490705448, 490705449];

    internal static void RunCore()
    {
        int before = Count;
        List<Exception> failures = [];
        using Stream vectors = typeof(MachinePromotionPageTests).Assembly
            .GetManifestResourceStream("DfoServer.SelfTests.PromotionPageContractVectors")
            ?? throw new InvalidOperationException("共享向量资源未装配");
        using JsonDocument document = JsonDocument.Parse(vectors);
        string Golden(string key) => document.RootElement.GetProperty(key).GetString()!;
        void Check(string name, Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(new InvalidOperationException(name, error)); }
        }

        Check("合法查询及命名空间隔离", Queries);
        Check("真实19段及6段黄金字节", () =>
        {
            Bytes(MachinePromotionPagePackets.Snapshot(Nonce, 0, Available(32)),
                Envelope(Golden("ready32")), "snapshot完整黄金wire，角色0合法");
            Bytes(MachinePromotionPagePackets.Changed(Nonce, ushort.MaxValue, 7, 11),
                Envelope(Golden("changed")), "changed完整6段黄金wire");
        });
        Check("历史累计不受5000钳制", () =>
        {
            Bytes(MachinePromotionPagePackets.Snapshot(Nonce, 0, Available(5001) with { Status = "disabled" }),
                Envelope(Golden("disabled5001")), "历史5001仍原值恢复且maximum为5000");
            Bytes(MachinePromotionPagePackets.Snapshot(Nonce, 0, Available(long.MaxValue) with { Status = "disabled" }),
                Envelope(Golden("disabledI64Max")), "Int64历史原事实无溢出或钳制");
        });
        Check("不可核账本保已知累计", () =>
        {
            var snapshot = Available(5001) with { Status = "unavailable", AwardMask27 = null,
                OverflowAwardCount = null, Rewards = [], OverflowReward = null };
            Bytes(MachinePromotionPagePackets.Snapshot(Nonce, 0, snapshot),
                Envelope(Golden("unavailableKnown")), "账本未知不能伪0或抹去真实累计");
            Bytes(MachinePromotionPagePackets.Snapshot(Nonce, 0, snapshot with { Count = null, ProgressRevision = null }),
                Envelope(Golden("unavailableUnknown")), "真正未知使用短横线");
        });
        Check("十名称超过600字节仍完整", () =>
        {
            var snapshot = Available(32, new string('测', 64));
            byte[] actual = MachinePromotionPagePackets.Snapshot(Nonce, 0, snapshot);
            Bytes(actual, Envelope(Golden("wide192")), "192B真实名称逐字无截断");
            That(actual.Length > 619 && actual.Length <= 4019, "专用机器消费者容量与原公告600B独立");
        });
        Check("无效字段拒绝而非输出截断成功", InvalidSnapshots);
        if (failures.Count != 0) throw new AggregateException("PromotionPage contract behavioral failures", failures);
        Console.WriteLine($"MachinePromotionPageTests: {Count - before} passed");
    }

    static PromotionPageSnapshot Available(long count, string name = "合成奖励") =>
        new("spring-2020", "replica-1", "v1", "default", "package", 7, 11, "ready",
            count, 5000, 134217727, count - 30, 10000,
            RewardIds.Select(id => new PromotionPageReward(id, 1, name)).ToImmutableArray(),
            new(490705415, 1, name));

    static void Queries()
    {
        That(MachinePromotionQuery.Parse(3, null, "#cp-promo/1 " + Nonce, out string? nonce) ==
            PromotionQueryRoute.Query && nonce == Nonce, "精确44字符合法命令必须被接受");
        foreach (string text in new[] { "#cp-promo/1", "#cp-promo/1 bad", "#cp-promo/1  " + Nonce,
            "#cp-promo/1\t" + Nonce, "#cp-promo/1 " + Nonce + " ", "#cp-promo/1 " + Nonce.ToUpperInvariant(),
            "#cp-promo/1 " + Nonce + "\0", "#cp-promo/1 " + Nonce + "\n" })
            That(MachinePromotionQuery.Parse(3, null, text, out nonce) == PromotionQueryRoute.Invalid && nonce is null,
                "已占命名空间坏语法吞掉且不广播");
        foreach (string text in new[] { "#cp-promo/1x", " #cp-promo/1 " + Nonce, "#CP-promo/1 " + Nonce,
            "#cp-score/1 " + Nonce })
            That(MachinePromotionQuery.Parse(3, null, text, out nonce) == PromotionQueryRoute.Ordinary && nonce is null,
                "相似前缀和Score保持独立");
        foreach (byte kind in new byte[] { 0, 6, 11, 13, 36, 46 })
            That(MachinePromotionQuery.Parse(kind, null, "#cp-promo/1 " + Nonce, out nonce) ==
                PromotionQueryRoute.Ordinary && nonce is null, "其他频道不劫持");
        That(MachinePromotionQuery.Parse(3, "target", "#cp-promo/1 " + Nonce, out nonce) ==
            PromotionQueryRoute.Ordinary && nonce is null, "具名目标不进入机器查询");
    }

    static void InvalidSnapshots()
    {
        var snapshot = Available(32);
        void Bad(PromotionPageSnapshot value) => Reject<InvalidDataException>(() =>
            MachinePromotionPagePackets.Snapshot(Nonce, 0, value), "不合法snapshot不可输出机器帧");
        foreach (string nonce in new[] { "", Nonce[..31], Nonce + "0", Nonce.ToUpperInvariant(), "中" + Nonce[1..] })
        {
            Reject<InvalidDataException>(() => MachinePromotionPagePackets.Snapshot(nonce, 0, snapshot), "nonce精确");
            Reject<InvalidDataException>(() => MachinePromotionPagePackets.Changed(nonce, 0, 7, 11), "changed nonce精确");
        }
        foreach (int ttl in new[] { -1, 0, 10001 }) Bad(snapshot with { TtlMs = ttl });
        Bad(snapshot with { CatalogRevision = -1 });
        Bad(snapshot with { ProgressRevision = -1 });
        Bad(snapshot with { ProgressRevision = null });
        Bad(snapshot with { Count = -1 });
        Bad(snapshot with { Count = null });
        Bad(snapshot with { MaximumPurchases = 5001 });
        Bad(snapshot with { AwardMask27 = 134217728 });
        Bad(snapshot with { AwardMask27 = null });
        Bad(snapshot with { OverflowAwardCount = -1 });
        Bad(snapshot with { Status = "ready|disabled" });
        Bad(snapshot with { Campaign = "another" });
        Bad(snapshot with { Rewards = snapshot.Rewards.RemoveAt(0) });
        Bad(snapshot with { Rewards = snapshot.Rewards.SetItem(0, new(490705442, 1, "合成奖励")) });
        Bad(snapshot with { Rewards = snapshot.Rewards.SetItem(0, new(490705441, 2, "合成奖励")) });
        Bad(snapshot with { OverflowReward = new(490705416, 1, "合成奖励") });
        foreach (string name in new[] { "", " ", "a\0b", "a\nb", new string((char)0x85, 1),
            new string((char)0xfeff, 1), new string((char)0xd800, 1), new string('测', 65) })
            Bad(snapshot with { Rewards = snapshot.Rewards.SetItem(0, new(490705441, 1, name)) });
        Reject<InvalidDataException>(() => MachinePromotionPagePackets.Changed(Nonce, 0, -1, 11), "changed目录revision非负");
        Reject<InvalidDataException>(() => MachinePromotionPagePackets.Changed(Nonce, 0, 7, -1), "changed进度revision非负");
    }

    // 预期外壳独立编码；不调用被测Builder生成黄金值。
    static byte[] Envelope(string text)
    {
        byte[] body = Encoding.ASCII.GetBytes(text);
        byte[] result = new byte[19 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 10);
        Encoding.ASCII.GetBytes("CP_PROMO/1").CopyTo(result, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14), (uint)body.Length);
        body.CopyTo(result, 18);
        return result;
    }
}