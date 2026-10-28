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
        var factory = new AntonResourceRewards(script, resources.ItemResources.Items);
        var first = factory.Create(false, _ => 0);
        var last = factory.Create(true, weight => weight - 1);
        That(first.Length == 2 && first.All(phase => phase.Source == AntonResourceRewards.Source && !phase.PublishResult),
            "正式两阶段本服政策记录来源，601未知列不发布");
        for (int index = 0; index < 2; index++)
        {
            var rows = script.Phases[index].Rewards;
            var squad = rows.Where(row => row.Kind == "squad_item").ToArray();
            var normal = first[index].Offers.Single(offer => offer.Kind == 1);
            var pcroom = last[index].Offers.Single(offer => offer.Kind == 1);
            That(first[index].Offers.Select(offer => offer.Kind).Order().SequenceEqual(new byte[] { 0, 1 }) &&
                normal.Card == 0 && first[index].Offers.All(offer => offer.Display.IsEmpty), "两个独立牌种沿已证0/1请求，未知600展示列保持空");
            That(normal.Grants.Length == 2 && normal.Grants.All(grant => grant.Count == 1) &&
                normal.Grants.Any(grant => grant.ItemId == rows.Single(row => row.Kind == "party_card").ItemId) &&
                normal.Grants.Any(grant => grant.ItemId == rows.Single(row => row.Kind == "privilege_card").ItemId),
                "本服每人party箱1和privilege福利1，编号完全取资源");
            That(pcroom.Grants.Length == 3 && pcroom.Grants.Any(grant => grant.ItemId == rows.Single(row => row.Kind == "pcroom_card").ItemId),
                "只有既有PC房资格增加pcroom箱1，不把RawA或RawD猜成数量");
            That(first[index].Offers.Single(offer => offer.Kind == 0).Grants is [{ Count: 1 } head] && head.ItemId == squad[0].ItemId &&
                last[index].Offers.Single(offer => offer.Kind == 0).Grants is [{ Count: 1 } tail] && tail.ItemId == squad[^1].ItemId,
                "本服相对权重抽样覆盖首尾边界，原始98/1/1与665/300/5/25/5不当数量发放");
        }
        Reject<InvalidDataException>(() => factory.Create(false, weight => weight), "越界随机结果不得冻结为合法奖励");
    }
}
