using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class AntonResourceRewardTests
{
    internal static void Run(TcpTestResources resources)
    {
        var main = SelfTestCatalogs.Dungeons(resources.Archive);
        var raid = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive, main, resources.ItemResources.Items,
            antonMapPath: AntonRaidMapPaths.Bind(main.MapPath), antonMapPoolPaths: AntonRaidMapPaths.Paths).Find(0)!.Resources!;
        var script = raid.Script;
        var volcano = raid.EntryCatalog!.Find(219)!;
        var rawMaze = volcano.Mazes.Single(maze => maze.QuestConnectionTokens.Count == 0);
        var resolved = raid.EntryCatalog.ResolveMaze(volcano, rawMaze, _ => 0);
        That(!rawMaze.MapSpecification!.Any(map => map.Position == rawMaze.Starts.Single()) && resolved.StaticTopology.Supported,
            "黑色火山原DGN漏显式起点，由已登记MAP反向池补齐，原声明不改写");
        That(resolved.MapSpecification!.Single(map => map.Position == resolved.Starts.Single()) is
            { Id: 19206, Position: { X: 0, Y: 1 }, Path: "map/219_blackvolcano/19206(6,0)s.map" },
            "19206按原表编号和MAP归属/BB选择，位置沿DGN而非文件名6,0");
        var factory = new AntonResourceRewards(script, resources.ItemResources.Items, resources.ItemResources.Boosters);
        Reject<InvalidDataException>(() => new AntonResourceRewards(script, resources.ItemResources.Items, null),
            "非空正式奖励声明必须提供共享奖池目录，不能退回原箱绕过依赖校验");
        var first = factory.Create(false, _ => 0);
        var last = factory.Create(true, weight => weight - 1);
        That(first.Length == 2 && first.All(phase => phase.Source == AntonResourceRewards.Source && phase.PublishResult),
            "正式两阶段本服政策记录来源，601正式策略发布");
        var copy = System.Text.Json.JsonSerializer.Deserialize<RaidPhaseRewardPolicy>(
            System.Text.Json.JsonSerializer.Serialize(first[0]))!;
        That(AntonRaidBattle.SameLukeOffers(first[0].Offers, copy.Offers), "600嵌套展示JSON往返按内容确认");
        var row = copy.Offers[0].Display[0];
        var changed = copy.Offers[0] with { Display = [row with { Items = row.Items.SetItem(0,
            row.Items[0] with { RawFlag = 1 }) }] };
        That(!AntonRaidBattle.SameLukeOffers(first[0].Offers, copy.Offers.SetItem(0, changed)), "600展示尾列改变仍拒绝回执");
        for (int index = 0; index < 2; index++)
        {
            var rows = script.Phases[index].Rewards;
            var squad = rows.Where(row => row.Kind == "squad_item").ToArray();
            var normal = first[index].Offers.Single(offer => offer.Kind == 1);
            var pcroom = last[index].Offers.Single(offer => offer.Kind == 1);
            That(first[index].Offers.Select(offer => offer.Kind).Order().SequenceEqual(new byte[] { 1, 3 }) &&
                normal.Card == 0 && first[index].Offers.All(offer => offer.Display is [{ Card: 0 }] && offer.Display[0].Items.Length == offer.Grants.Length), "普通成员只有团队1及自动小队3，kind0不再授予资产");
            That(normal.Grants is [{ ItemId: 10304529 } fate, { ItemId: 10093974, Count: 6 }] &&
                fate.Count == (index == 0 ? 30 : 45), "86单池解析映射现网95产物数量，不发配置箱");
            That(pcroom.Grants is [{ ItemId: 10304529 }, { ItemId: 10093974, Count: 6 }] &&
                last[index].Offers.Single(offer => offer.Kind == 2).Grants is [{ ItemId: 10093974, Count: 11 }] &&
                last[index].Offers.Select(offer => offer.Kind).Order().SequenceEqual(new byte[] { 1, 2, 3 }),
                "PC房128资格使用独立kind2，其它团队与礼遇福利保持原数量");
            That(first[index].Offers.Single(offer => offer.Kind == 3).Grants is [{ ItemId: 10093974 } head] &&
                head.Count == (index == 0 ? 19 : 58) &&
                last[index].Offers.Single(offer => offer.Kind == 3).Grants is [{ Count: 1 } tail] && tail.ItemId == squad[^1].ItemId,
                "箱内权重首项为19/58材料，末项多池booster保持原箱，不套86单池算法");
        }
        Reject<InvalidDataException>(() => factory.Create(false, weight => weight), "越界随机结果不得冻结为合法奖励");
        AntonResourceRewardLifecycleTests.Run(resources);
    }
}
