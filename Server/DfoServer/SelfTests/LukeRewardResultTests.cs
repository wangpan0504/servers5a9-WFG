using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeRewardResultTests
{
    internal static void Run()
    {
        int before = Count;
        var spec = new LukeRewardResultSpec(7, 0x12345678, 3, 9, 0x89abcdef, 0);
        var offer = new RaidRewardOffer(1, 2, [new(0x12345678, 3)], []) { LukeResult = spec };
        That(spec.Matches(offer), "完整显式结果与单grant一一一致");
        Bytes(LukeRewardPackets.Result(new(1, 0x4321, 2, spec)),
            [1,1,0x21,0x43,2,1,7,0x78,0x56,0x34,0x12,3,0,9,0xef,0xcd,0xab,0x89,0,0,0,0],
            "Luke单行原箱600精确22B，两尾u32不截断最高位");
        Bytes(LukeRewardPackets.TeamResults([]), [3, 0], "P1空团队页使用原生wire3，不新增选牌kind");
        Bytes(LukeRewardPackets.TeamResults([new(0, 0x4321, 0, spec), new(0, 0x1234, 2, spec)]),
            [3,2,0x21,0x43,0,1,7,0x78,0x56,0x34,0x12,3,0,9,0xef,0xcd,0xab,0x89,0,0,0,0,
                0x34,0x12,2,1,7,0x78,0x56,0x34,0x12,3,0,9,0xef,0xcd,0xab,0x89,0,0,0,0],
            "团队权益页两名成员完整16B物品行，owner来自封存资格不借party槽号");
        Reject<InvalidDataException>(() => LukeRewardPackets.TeamResults([new(1, 1, 0, spec)]), "个人奖励不得误投团队页");
        Reject<InvalidDataException>(() => LukeRewardPackets.TeamResults([new(0, 1, 0, spec), new(0, 1, 1, spec)]), "团队权益页拒绝重复角色");
        Bytes(RaidCombatPackets.Reward(1, [new(0x4321, 2, [new(7,0x12345678,3,9)])]),
            [1,1,0x21,0x43,2,1,7,0x78,0x56,0x34,0x12,3,0,9], "Anton既有8B物品布局不变");
        foreach (var incomplete in new[] { spec with {RawCode=null}, spec with {ItemId=null}, spec with {DisplayNumber=null},
            spec with {RawFlag=null}, spec with {RawTail1=null}, spec with {EnchantUpgrade=null}, spec with {EnchantUpgrade=256} })
        {
            That(!incomplete.Matches(offer), "每一缺列及附魔升级越界明确拒绝");
            bool rejected=false; try { LukeRewardPackets.Result(new(1,1,1,incomplete)); } catch(InvalidDataException){rejected=true;}
            That(rejected,"encoder不为缺字段补零");
        }
        That(!spec.Matches(offer with {Grants=[new(0x12345678,4)]}) && !spec.Matches(offer with {Grants=[new(1,3)]}) &&
            !spec.Matches(offer with {Grants=[new(0x12345678,3),new(0x12345678,3)]}),"物品/数量/重复grant不靠set比较通过");
        That(!spec.Matches(offer with {Grants=default}) && !spec.Matches(offer with {Grants=[]}),"缺失/空grant不能生成展示结果");
        var roundtrip=JsonSerializer.Deserialize<RaidRewardOffer>(JsonSerializer.Serialize(offer))!;
        That(AntonRaidBattle.SameLukeOffers([offer],[roundtrip]),"持久JSON往返内容深比较，不依赖ImmutableArray引用");
        That(!AntonRaidBattle.SameLukeOffers([offer],[roundtrip with {LukeResult=spec with {RawTail1=0}}]),"封存后tail变化拒绝");
        That(!AntonRaidBattle.SameLukeOffers([offer],[roundtrip with {LukeResult=null}]),"缺预览不等于全零预览");
        var legacy=JsonSerializer.Deserialize<RaidRewardOffer>("{\"Kind\":1,\"Card\":2,\"Grants\":[{\"ItemId\":305419896,\"Count\":3}],\"Display\":[]}")!;
        That(legacy.LukeResult is null && AntonRaidBattle.SameLukeOffers([legacy],[offer with {LukeResult=null}]),"旧JSON无属性保持null并兼容原资格深比较");
        var missing=JsonSerializer.Deserialize<LukeRewardResultSpec>("{\"RawCode\":0,\"ItemId\":305419896,\"DisplayNumber\":3,\"RawFlag\":0,\"EnchantUpgrade\":0}")!;
        That(missing.RawTail1 is null&&!missing.Matches(offer),"JSON缺tail1与显式零区分");
        That((spec with {RawCode=0,RawFlag=0,RawTail1=0,EnchantUpgrade=0}).Matches(offer),"显式零可用，不把零当缺字段");
        var multiGoldFirst = new LukeRewardResultSpec(1, 10159648, 4, 0, 0, 0);
        var multiGoldSecond = new LukeRewardResultSpec(1, 10159623, 30, 0, 0, 0);
        var multiGoldOffer = new RaidRewardOffer(0, 0, [new(10159648, 4), new(10159623, 30)], [])
            { LukeResult = multiGoldFirst, LukeAdditionalResults = [multiGoldSecond] };
        That(multiGoldFirst.Matches(multiGoldOffer), "多项金牌团队奖励全部条目code=1一致匹配");
        var mismatchedCodeOffer = multiGoldOffer with { LukeAdditionalResults = [multiGoldSecond with { RawCode = 0 }] };
        That(!multiGoldFirst.Matches(mismatchedCodeOffer), "同一团队奖励展开项code不一致拒绝，防止客户端按itemId排序后金牌判定失真");
        Reject<InvalidDataException>(() => LukeRewardPackets.Result(new(0, 0x4321, 1, multiGoldFirst)
            { AdditionalItems = [multiGoldSecond with { RawCode = 0 }] }), "encoder拒绝同槽展开项code不一致");
        Console.WriteLine($"PASS {Count-before} 卢克结果600合同断言");
    }
}
