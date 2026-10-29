using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BeastRewardAssetsTests
{
    public static void Run(TcpTestResources resources)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-beast-pots-" + Guid.NewGuid().ToString("N"));
        string database = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(database, resources.Quests);
            new Game.Auth.AuthenticationService(new AuthenticationRepository(database)).Create("beast-pots", "fixture-password");
            using var owner = new AccountOwners().Acquire("beast-pots", () => { });
            var items = resources.ItemResources;
            var quest = resources.Quests.Get(10000);
            foreach (bool last in new[] { false, true })
            {
                var current = repository.Create(owner, new("BeastPot" + (last ? "1" : "0"), resources.Towns.Room, resources.Towns.InitialReturn)
                    { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [5927] });
                current = repository.CommitQuestLifecycle(owner, QuestLifecyclePlan.Accept(current, quest, resources.Quests,
                    items.Items, items.GoldLimits, items.EpicPieces, DateTimeOffset.UtcNow)).Character;
                current = InventoryService.Commit(repository, owner, current, InventoryRewardGrantService.TryGrantBatch(current,
                    [..Enumerable.Range(0, 6).Select(stage => new InventoryReward(BeastDungeonRotation.StageRewardItem(stage)!.Value, 1))],
                    items.Items, items.GoldLimits, items.EpicPieces));
                var service = new InventoryBoosterService(items.Items, items.Boosters, items.GoldLimits, items.EpicPieces, new EdgeRandom(last));
                for (int stage = 0; stage < 6; stage++)
                {
                    int pot = BeastDungeonRotation.StageRewardItem(stage)!.Value;
                    var source = current.Inventory.Items.Single(row => row.Core.ItemId == pot);
                    var before = current;
                    var opened = service.Open(current, new(source.Position.Index, 1), out var receipt);
                    That(opened.Accepted && receipt is not null, $"真实阶段罐可由通用开罐服务打开：{stage + 1}/{last}");
                    int first = stage < 2 ? 7 : last ? 4 : 3;
                    int second = stage switch { 0 => 11, 1 => 3, 2 or 5 => 7, 3 => last ? 6 : 5, _ => last ? 9 : 8 };
                    var expected = new Dictionary<int, int> { [10303237] = first, [10303238] = second };
                    if (stage == 5) expected[last ? 10303496 : 10303495] = 1;
                    That(receipt!.ItemId == pot && receipt.Rewards.Length == expected.Count &&
                        receipt.Rewards.All(row => expected.TryGetValue(row.ItemId, out int count) && row.Count == count),
                        "六罐真实奖池保留两件任务材料及第六罐随机附加物，不把罐编号当材料到账");
                    byte[] ack = BoosterPackets.Opened(opened, receipt);
                    That(ack.Length == 18 + expected.Count * 8 && ack[0] == 1 &&
                        BinaryPrimitives.ReadInt32LittleEndian(ack.AsSpan(2)) == pot &&
                        BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(16)) == expected.Count,
                        "真实六罐ACK160按实际开罐回执完整构包");
                    current = InventoryService.Commit(repository, owner, current, opened);
                    current = new CharacterRepository(database, resources.Quests).LoadCharacter(owner, current.Slot);
                    That(!current.Inventory.Items.Any(row => row.Core.ItemId == pot) && current.Revision == before.Revision + 1 &&
                        receipt.Rewards.All(row => Quantity(current, row.ItemId) == Quantity(before, row.ItemId) + row.Count),
                        "源罐消耗与全部产物同笔持久提交，重开仓储不丢材料");
                    That(!service.Open(current, new(source.Position.Index, 1), out _).Accepted,
                        "已消费罐的原请求不能重发材料");
                }
                That(current.ActiveMainQuests.Single(row => row.QuestId == 10000).ConditionState == 1 &&
                    Quantity(current, 10303237) == (last ? 30 : 26) && Quantity(current, 10303238) == (last ? 43 : 41),
                    "六阶段材料真实累计，未达到220/180时10000保持未完成");
                current = InventoryService.Commit(repository, owner, current, InventoryRewardGrantService.TryGrantBatch(current,
                    [new(10303237, 220 - Quantity(current, 10303237)), new(10303238, 180 - Quantity(current, 10303238))],
                    items.Items, items.GoldLimits, items.EpicPieces));
                current = new CharacterRepository(database, resources.Quests).LoadCharacter(owner, current.Slot);
                That(current.ActiveMainQuests.Single(row => row.QuestId == 10000).ConditionState == 0,
                    "累计材料达到资源目标后10000库存进度真实持久更新");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"PASS {Count - checks} beast stage pot asset assertions");
    }

    static int Quantity(Character character, int id) => character.Inventory.Items.Where(row => row.Core.ItemId == id).Sum(row => row.Core.Count);
    sealed class EdgeRandom(bool last) : Random
    {
        public override long NextInt64(long maxValue) => last ? maxValue - 1 : 0;
    }
}
