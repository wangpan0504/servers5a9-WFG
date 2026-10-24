using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using System.Buffers.Binary;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GriefTowerRewardTests
{
    internal static void RunCore()
    {
        int checks = Count;
        ImmutableArray<GriefTowerRewardSlot> slots =
            [new(3,[new(10160931,2850,1)]), new(10,[new(10160931,2800,1),new(10303266,500,1)])];
        // 穷举每个区间，验证准确端点及空奖，避免概率测试的偶发误差。
        int material = 0, pot = 0, blank = 0;
        for (int roll = 0; roll < GriefTowerRewardPolicy.Denominator; roll++)
        {
            int calls = 0;
            var reward = GriefTowerRewardPolicy.Select("normal",slots,bound =>
            {
                if (bound != 10000) throw new InvalidDataException("随机上界发生变化");
                return calls++ == 0 ? 9999 : roll;
            });
            if (calls != 2 || reward.Items.Length > 1) throw new InvalidDataException("奖励槽重抽或同槽多次命中");
            if (reward.Items.IsEmpty) blank++;
            else if (reward.Items[0] is { Slot: 10, ItemId: 10160931, Count: 1 }) material++;
            else if (reward.Items[0] is { Slot: 10, ItemId: 10303266, Count: 1 }) pot++;
            else throw new InvalidDataException("命中了其它候选");
        }
        That((material,pot,blank) == (2800,500,6700),"槽内原始万分值互斥，空奖不归一为必出");
        var independent = GriefTowerRewardPolicy.Select("normal",slots,_ => 0);
        That(independent.Items is [{ Slot: 3, ItemId: 10160931 },{ Slot: 10, ItemId: 10160931 }] &&
            independent.Rule == "grief-pvf-independent-slots-inferred-v1","各槽分别抽取，结果携带明确推定规则版本");
        foreach (int roll in new[] { 0, 9999 })
        {
            var special = GriefTowerRewardPolicy.Select("special",[new(10,[new(10160930,10000,1)])],_ => roll);
            That(special.Items is [{ ItemId: 10160930, Count: 1 }],"50/100层万分固定奖励覆盖两个端点");
        }
        int invalidCalls = 0;
        foreach (ImmutableArray<GriefTowerRewardSlot> invalid in new ImmutableArray<GriefTowerRewardSlot>[]
        {
            [], [new(0,[new(1,1,1)])], [new(11,[new(1,1,1)])], [new(3,[])],
            [new(3,[new(1,5000,1),new(2,5001,1)])], [new(3,[new(1,int.MaxValue,1),new(2,int.MaxValue,1)])],
            [new(3,[new(1,1,1)]),new(3,[new(2,1,1)])], [new(3,[new(1,-1,1)])], [new(3,[new(0,1,1)])]
        })
            Reject<InvalidDataException>(() => GriefTowerRewardPolicy.Select("invalid",invalid,_ => ++invalidCalls),
                "无效整表在随机抽样前拒绝");
        That(invalidCalls == 0,"非法配置不消耗随机序列");
        foreach (int invalid in new[] { -1,10000 })
            Reject<InvalidDataException>(() => GriefTowerRewardPolicy.Select("normal",slots,_ => invalid),"随机值不允许越界");
        byte[] notice=GriefTowerPackets.ClearReward(123456,50,new("special",[new(9,10303266,1),new(10,10160930,1)]));
        That(notice.Length==23 && BinaryPrimitives.ReadUInt32LittleEndian(notice)==123456 &&
            BinaryPrimitives.ReadUInt16LittleEndian(notice.AsSpan(4))==50 && notice[6]==2 &&
            BinaryPrimitives.ReadUInt32LittleEndian(notice.AsSpan(7))==10303266 &&
            BinaryPrimitives.ReadUInt32LittleEndian(notice.AsSpan(11))==1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(notice.AsSpan(15))==10160930 &&
            BinaryPrimitives.ReadUInt32LittleEndian(notice.AsSpan(19))==1,
            "1267按匹配客户端毫秒、层数、条数及物品数量顺序构造，不混入普通翻牌字段");
        That(GriefTowerPackets.ClearReward(0,1,new("normal",[])).Length==7,"空奖仍保留耗时和已通关楼层");
        Reject<InvalidDataException>(()=>GriefTowerPackets.ClearReward(0,101,new("normal",[])),"1267不能用下一层101冒充刚通关层");
        Console.WriteLine($"PASS {Count-checks} 悲叹推定奖励区间、互斥、空奖与固定奖励");
    }
}
