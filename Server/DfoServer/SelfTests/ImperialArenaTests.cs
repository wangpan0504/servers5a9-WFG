using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.ImperialArena;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ImperialArenaTests
{
    internal static void Run()
    {
        var before = DateTimeOffset.Parse("2026-10-29T05:59:59+08:00");
        var after = before.AddSeconds(1);
        That(ImperialArenaPeriod.At(before) == new ImperialArenaPeriod(20261028, 20261022) &&
            ImperialArenaPeriod.At(after) == new ImperialArenaPeriod(20261029, 20261029), "帝国06点同时跨日及周四排名周界");
        That(!ImperialArenaPeriod.RankingClosed(before.AddMinutes(-30)) && ImperialArenaPeriod.RankingClosed(before.AddMinutes(-29)) &&
            ImperialArenaPeriod.RankingClosed(before) && !ImperialArenaPeriod.RankingClosed(after), "困难周四结算窗口边界准确");
        var rankingPeriod = ImperialArenaPeriod.At(before);
        var open = before.AddMinutes(-30);
        That(!ImperialArenaRankingPolicy.Eligible(rankingPeriod, 30000, 29999, open) &&
            ImperialArenaRankingPolicy.Eligible(rankingPeriod, 30000, 30000, open) &&
            !ImperialArenaRankingPolicy.Eligible(rankingPeriod, 30000, (long)uint.MaxValue + 1, open),
            "有效困难成绩严格核对30秒下限与客户端u32耗时上限");
        That(!ImperialArenaRankingPolicy.Eligible(rankingPeriod, 30000, 80000, before) &&
            !ImperialArenaRankingPolicy.Eligible(rankingPeriod, 30000, 80000, after),
            "关闭窗口和跨周迟完成不回写旧周成绩");
        That(ImperialArenaRequests.Configuration([2, 7, 2, 3]) == (2, 7, 2, 3), "1460按两个server/index消费四字节");
        foreach (int length in new[] { 0, 1, 3, 5, 13, 75 })
            Reject<InvalidDataException>(() => ImperialArenaRequests.Configuration(new byte[length]), "帝国编队不接纳再战或截断正文");
        foreach (byte value in new byte[] { 0, 3, 255 })
            Reject<InvalidDataException>(() => ImperialArenaRequests.Purchase([value]), "购券只接纳已证普通及困难模式");
        That(ImperialArenaRequests.Purchase([2]) == ImperialArenaMode.Hard, "购券模式保留客户端选择");
        Bytes(ImperialArenaPackets.Reward([new(10304532, 3), new(10304533, 6)]),
            [2, 0x14, 0x3C, 0x9D, 0, 3, 0, 0x15, 0x3C, 0x9D, 0, 6, 0], "1402为u8行数及u32物品/u16数量金样");
        Bytes(ImperialArenaPackets.Challenge(new(new(20261027, 20261022), Guid.NewGuid(), 80000, [0, 1, 17, 18], 240000, true, 0)),
            [0x80, 0x38, 1, 0, 0, 1, 17, 18, 0x80, 0xA9, 3, 0, 1], "1632首时间、双职业双转职、目标时间及领奖位金样");
        var display = new ImperialArenaState(ImperialArenaConfiguration.Empty, ImperialArenaPeriod.At(after), ImperialArenaMode.Normal,
            ImmutableHashSet.Create(1, 2, 3), 0);
        Bytes(ImperialArenaPackets.Enterable(display), [1, 1, 3], "1403普通真实已清前缀恢复三张灰显头像");
        Bytes(ImperialArenaPackets.Enterable(display with { PaidMode = ImperialArenaMode.Hard, HardRewardRound = 7 }),
            [1, 2, 7], "1403困难取账号累计最高回合而非当前实例进度");
        var participants = TagEvildomRunTests.Participants();
        participants = participants with { Primary = participants.Primary with { Level = 90 }, Standby = participants.Standby with { Level = 90 } };
        Reject<InvalidDataException>(() => new TagEvildomRunState(Guid.NewGuid(), participants), "再战仍拒绝90级角色");
        var state = new TagEvildomRunState(Guid.NewGuid(), participants, imperialArena: true, switchCooldownMilliseconds: 2000);
        That(state.Switch(1, 1000).Outcome == DungeonRunOutcome.Accepted && state.Switch(0, 2999).Outcome == DungeonRunOutcome.Rejected &&
            state.Switch(0, 3000).Outcome == DungeonRunOutcome.Accepted, "帝国切角按资源2秒冷却，不受报文重复加速");
        var death = state.ReportDeath(0, 3001);
        That(death.SwitchTo == 1 && state.NextRevival() is null && !death.Snapshot.Slots[0].Alive &&
            state.Switch(0, 999999).Outcome == DungeonRunOutcome.Rejected, "帝国死角不复活、不能被切回");
        That(state.ReportDeath(1, 3002).DefeatAcknowledgement && state.Snapshot.Defeated, "双角死亡才发终局败北");
        var catalog = Make();
        var normal = ImperialArenaRewardPolicy.Normal(catalog, false, _ => 0);
        That(normal.SequenceEqual(new ImperialArenaRewardItem[] { new(10304532, 2), new(10304533, 3) }), "普通最低随机勋章和固定奖章来自资源");
        That(ImperialArenaRewardPolicy.Normal(catalog, false, _ => 99)[0].Count == 3, "普通另一权重分支不丢失");
        That(ImperialArenaRewardPolicy.HardDelta(catalog, 0, 7).SequenceEqual(new ImperialArenaRewardItem[] { new(10304532, 27), new(10304533, 30) }) &&
            ImperialArenaRewardPolicy.HardDelta(catalog, 5, 7).SequenceEqual(new ImperialArenaRewardItem[] { new(10304532, 10), new(10304533, 11) }) &&
            ImperialArenaRewardPolicy.HardDelta(catalog, 7, 3).IsEmpty, "困难累计表只发新增差额，重打低回合不刷奖");
        var period = ImperialArenaPeriod.At(after);
        var route = ImperialArenaDailyRoute.Create(catalog, period);
        var again = ImperialArenaDailyRoute.Create(catalog, period);
        route.Validate(catalog);
        Reject<InvalidDataException>(() => (route with { Hard = route.Hard.SetItem(1, route.Hard[0]) }).Validate(catalog), "持久路线不能重复同一对手");
        Reject<InvalidDataException>(() => (route with { Normal = route.Normal.SetItem(0, new(999, 999)) }).Validate(catalog), "持久路线不能引用候选外对手");
        Reject<InvalidDataException>(() => (route with { HardWeakness = route.HardWeakness.SetItem(0, 4) }).Validate(catalog), "困难不能沿用特殊王无元素弱点");
        That(route.Normal.SequenceEqual(again.Normal) && route.Hard.SequenceEqual(again.Hard) && route.Normal.Length == 5 && route.Hard.Length == 7 &&
            route.Normal.Take(4).All(catalog.Normal.Contains) && catalog.Special.Contains(route.Normal[^1]), "同日各账号及重启使用相同合法对手组合");
        Reject<InvalidDataException>(() => Make(badFloat: true), "整数位图不能冒充倍率浮点token");
        Reject<InvalidDataException>(() => Make(decreasingReward: true), "困难累计奖励不能倒退以免后续差额重新增发");
    }

    internal static void RunResources(PvfArchive pvf)
    {
        ImperialArenaPersistenceTests.RunResources(pvf);
        var catalog = new ImperialArenaCatalog(pvf);
        var dungeons = Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        That(catalog.EntryGold == 150000 && catalog.SwitchCooldownMilliseconds == 2000 && catalog.QuickItems.Length == 6,
            "正式资源费用、切角及六专用快槽配置可完整读取");
        foreach (int id in new[] { 320, 336 })
        {
            var definition = dungeons.Get(id);
            That(ImperialArenaEntryPolicy.Supports(definition), "帝国正式DGN具备独立准入合同");
            var mode = id == 320 ? ImperialArenaMode.Normal : ImperialArenaMode.Hard;
            var route = ImperialArenaDailyRoute.Create(catalog, new(20261027, 20261022));
            var maze = dungeons.ResolveImperialArenaMaze(definition, definition.Mazes.Single(), route.Opponents(mode).Select(row => row.MapId).ToArray());
            foreach (var map in maze.MapSpecification!)
            {
                var room = rooms.Get(map);
                if (map.Position.X > 0)
                    That(room.Monsters.Any(row => row.MonsterId == route.Opponents(mode)[map.Position.X - 1].MonsterId), "资源房间含配置对应的真实领奖对手");
            }
        }
    }

    static ImperialArenaCatalog Make(bool badFloat = false, bool decreasingReward = false)
    {
        var strings = new List<string>(); var tokens = new List<PvfToken>();
        void Tag(string value) { strings.Add(value); tokens.Add(new(3, strings.Count - 1)); }
        void Field(string tag, params int[] values) { Tag("[" + tag + "]"); foreach (int value in values) tokens.Add(new(0, value)); }
        Field("tag tournament enter gold", 150000); Field("tag tournament dungeon charac tag cool time", 2);
        Field("tag tournament buff add stat", 4000); Field("tag tournament hard mode buff add state", 10000);
        Field("tag tournament hard mode min clear time", 30000);
        Field("tag tournament hard mode time attack standard rank", 20000, 240000);
        Field("tag tournament hard mode time attack reward", 10304868, 1668, 1669, 15);
        Field("tag tournament random map", 4, 1, 11, 2, 12, 3, 13, 4, 14, 5, 15);
        Field("tag tournament special map", 1, 6, 16, 7, 17);
        Field("tag tournament hard map", 7, 8, 18, 9, 19, 10, 20, 11, 21, 12, 22, 13, 23, 14, 24);
        Field("tag tournament reward", 0, 10304532, 2, 50, 0, 10304532, 3, 50, 1, 10304533, 3, 100);
        Field("tag tournament special reward", 0, 10304532, 5, 100, 1, 10304533, 3, 100);
        var rewards = new List<int>(); int[] medals = [3, 6, 9, 13, 17, 21, 27], marks = [3, 6, 10, 14, 19, 24, 30];
        if (decreasingReward) medals[1] = 2;
        for (int i = 0; i < 7; i++) rewards.AddRange([i, 10304532, medals[i], i, 10304533, marks[i]]);
        Field("tag tournament hard reward", rewards.ToArray()); Tag("[/tag tournament reward hard]");
        Field("tag tournament stackable", 1, 2, 3, 4, 5, 6);
        Field("tag tournament hard mode balance", 7);
        foreach (float value in new float[] { 1, 1.1f, 1.3f, 1.7f, 2.1f, 2.5f, 3.1f }) tokens.Add(new(badFloat ? (byte)4 : (byte)2, BitConverter.SingleToInt32Bits(value)));
        Field("tag tournament dungeon list", 320, 336, 9608, 400000095);
        return new(tokens, index => strings[index]);
    }
}
