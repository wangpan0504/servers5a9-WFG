using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using PvfLib;
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
            Directory.CreateDirectory(root);
            var items = resources.ItemResources;
            var catalog = resources.Quests;
            bool questRegistered = catalog.TryGet(10000, out var quest);
            if (!questRegistered)
            {
                const string physicalQuest = "n_quest/new_quest/metrocenter/beast_acce_01.qst";
                That(!PvfScript.List(resources.Archive.ReadText("n_quest/quest.lst")).ContainsKey(10000) &&
                    resources.Archive.Contains(physicalQuest), "10000物理正文存在但正式索引未登记");
                RejectExactly(() => resources.Quests.Get(10000), "任务目录没有这个任务: 10000", "正式任务目录精确拒绝缺索引10000");
                quest = QuestDefinitionReader.Read(10000, physicalQuest, resources.Archive.ReadTokens(physicalQuest), resources.Archive.ResolveString);
                catalog = new QuestCatalog(resources.Quests.All.Append(quest).ToDictionary(row => row.Id));
            }
            int[] assetIds = [..BeastDungeonRotation.StageRewardItems,10303237,10303238,10303495,10303496];
            var installedStackable = PvfScript.List(resources.Archive.ReadText("stackable/stackable.lst"));
            var registeredBefore = assetIds.Select(id => installedStackable.ContainsKey(id)).ToArray();
            var itemCatalog = items.Items;
            var boosters = items.Boosters;
            bool localItems = registeredBefore.Any(registered => !registered);
            if (localItems)
            {
                var localStackable = new Dictionary<int,string>();
                foreach (int id in assetIds)
                {
                    if (installedStackable.TryGetValue(id, out var path)) localStackable.Add(id,path);
                    else
                    {
                        // 当前索引没有ID到文件的关联；数字basename只作显式测试映射，不推定生产登记。
                        string physicalPath = "10303001/" + id + ".stk";
                        That(!items.Items.TryGet(id,out _) && resources.Archive.Contains("stackable/" + physicalPath),
                            "正式目录缺项且对应候选物理正文存在：" + id);
                        RejectExactly(() => items.Items.Get(id), "PVF 物品不存在: " + id, "正式物品目录精确拒绝缺索引：" + id);
                        if (BeastDungeonRotation.StageRewardItems.Contains(id))
                            That(items.Boosters.Get(id) is { Available:false,PendingRule:"非礼包物品" },
                                "正式开罐目录不因候选物理文件存在而补登记：" + id);
                        localStackable.Add(id,physicalPath);
                    }
                }
                // 独立目录共享只读归档，完整脚本仍由正式reader解析；不Prefill或替换共享资源。
                itemCatalog = new ItemCatalog(resources.Archive,new Dictionary<int,string>(),localStackable,0);
                boosters = new BoosterCatalog(resources.Archive,localStackable);
            }
            VerifyCompletePools(boosters);
            That(assetIds.All(id => itemCatalog.Get(id).Kind ==
                    (BeastDungeonRotation.StageRewardItems.Contains(id) ? ItemKind.Consumable : ItemKind.Material)),
                "局部目录中的十项真实物理脚本保留源罐及四种材料类型");
            var repository = new CharacterRepository(database, catalog);
            new Game.Auth.AuthenticationService(new AuthenticationRepository(database)).Create("beast-pots", "fixture-password");
            using var owner = new AccountOwners().Acquire("beast-pots", () => { });
            foreach (bool last in new[] { false, true })
            {
                var current = repository.Create(owner, new("BeastPot" + (last ? "1" : "0"), resources.Towns.Room, resources.Towns.InitialReturn)
                    { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [5927] });
                current = repository.CommitQuestLifecycle(owner, QuestLifecyclePlan.Accept(current, quest, catalog,
                    itemCatalog, items.GoldLimits, items.EpicPieces, DateTimeOffset.UtcNow)).Character;
                current = InventoryService.Commit(repository, owner, current, InventoryRewardGrantService.TryGrantBatch(current,
                    [..Enumerable.Range(0, 6).Select(stage => new InventoryReward(BeastDungeonRotation.StageRewardItem(stage)!.Value, 1))],
                    itemCatalog, items.GoldLimits, items.EpicPieces));
                var service = new InventoryBoosterService(itemCatalog, boosters, items.GoldLimits, items.EpicPieces, new EdgeRandom(last));
                for (int stage = 0; stage < 6; stage++)
                {
                    int pot = BeastDungeonRotation.StageRewardItem(stage)!.Value;
                    var source = current.Inventory.Items.Single(row => row.Core.ItemId == pot);
                    var before = current;
                    var opened = service.Open(current, new(source.Position.Index, 1), out var receipt);
                    That(opened.Accepted && receipt is not null, $"完整阶段罐可由通用开罐服务打开：{stage + 1}/{last}");
                    int first = stage < 2 ? 7 : last ? 4 : 3;
                    int second = stage switch { 0 => 11, 1 => 3, 2 or 5 => 7, 3 => last ? 6 : 5, _ => last ? 9 : 8 };
                    var expected = new Dictionary<int, int> { [10303237] = first, [10303238] = second };
                    if (stage == 5) expected[last ? 10303496 : 10303495] = 1;
                    That(receipt!.ItemId == pot && receipt.Rewards.Length == expected.Count &&
                        receipt.Rewards.All(row => expected.TryGetValue(row.ItemId, out int count) && row.Count == count),
                        "六罐完整奖池保留两件任务材料及第六罐随机附加物，不把罐编号当材料到账");
                    byte[] ack = BoosterPackets.Opened(opened, receipt);
                    That(ack.Length == 18 + expected.Count * 8 && ack[0] == 1 &&
                        BinaryPrimitives.ReadInt32LittleEndian(ack.AsSpan(2)) == pot &&
                        BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(16)) == expected.Count,
                        "六罐ACK160按实际开罐回执完整构包");
                    current = InventoryService.Commit(repository, owner, current, opened);
                    current = new CharacterRepository(database, catalog).LoadCharacter(owner, current.Slot);
                    That(!current.Inventory.Items.Any(row => row.Core.ItemId == pot) && current.Revision == before.Revision + 1 &&
                        receipt.Rewards.All(row => Quantity(current, row.ItemId) == Quantity(before, row.ItemId) + row.Count),
                        "源罐消耗与全部产物同笔持久提交，重开仓储不丢材料");
                    That(!service.Open(current, new(source.Position.Index, 1), out _).Accepted,
                        "已消费罐的原请求不能重发材料");
                }
                That(current.ActiveMainQuests.Single(row => row.QuestId == 10000).ConditionState == 1 &&
                    Quantity(current, 10303237) == (last ? 30 : 26) && Quantity(current, 10303238) == (last ? 43 : 41),
                    "六阶段材料累计，未达到220/180时10000保持未完成");
                current = InventoryService.Commit(repository, owner, current, InventoryRewardGrantService.TryGrantBatch(current,
                    [new(10303237, 220 - Quantity(current, 10303237)), new(10303238, 180 - Quantity(current, 10303238))],
                    itemCatalog, items.GoldLimits, items.EpicPieces));
                current = new CharacterRepository(database, catalog).LoadCharacter(owner, current.Slot);
                That(current.ActiveMainQuests.Single(row => row.QuestId == 10000).ConditionState == 0,
                    "累计材料达到资源目标后10000库存进度真实持久更新");
            }
            That(resources.Quests.TryGet(10000,out _) == questRegistered &&
                assetIds.Select(id => items.Items.Contains(id)).SequenceEqual(registeredBefore),
                "局部完整资源测试结束后，正式任务及十项物品登记状态不变");
            Console.WriteLine(questRegistered && !localItems
                ? "beast stage pots：正式目录完整资源的六罐双侧资产链完成"
                : "beast stage pots：正式缺索引精确拒绝及显式局部资源六罐双侧资产链完成；不代表生产索引恢复或生产开罐成功");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"PASS {Count - checks} beast stage pot asset assertions");
    }

    static void VerifyCompletePools(BoosterCatalog boosters)
    {
        // 独立期望来自root保存的完整物理脚本，包含中间行和原权重，不从随机端点反推。
        BoosterReward[][][] expected =
        [
            [[new(10303237,1000,7)],[new(10303238,1000,11)]],
            [[new(10303237,1000,7)],[new(10303238,1000,3)]],
            [[new(10303237,1000,3),new(10303237,1000,4)],[new(10303238,1000,7)]],
            [[new(10303237,1000,3),new(10303237,1000,4)],[new(10303238,7500,5),new(10303238,2500,6)]],
            [[new(10303237,1000,3),new(10303237,1000,4)],[new(10303238,2500,8),new(10303238,7500,9)]],
            [[new(10303237,1000,3),new(10303237,1000,4)],[new(10303238,1000,7)],[new(10303495,1000,1),new(10303496,1000,1)]]
        ];
        for (int stage=0;stage<expected.Length;stage++)
        {
            var definition = boosters.Get(BeastDungeonRotation.StageRewardItem(stage)!.Value);
            That(definition.Available && definition.Pools.Length == expected[stage].Length &&
                definition.Pools.Select((pool,index) => pool.Draws == 1 && pool.Rewards.SequenceEqual(expected[stage][index])).All(value => value),
                "完整物理奖池的抽数、全部奖励行、数量和权重匹配：" + (stage+1));
        }
    }

    static void RejectExactly(Action action, string reason, string message)
    {
        bool rejected = false;
        try { action(); }
        catch (InvalidDataException error) when (error.Message == reason) { rejected = true; }
        That(rejected,message);
    }


    static int Quantity(Character character, int id) => character.Inventory.Items.Where(row => row.Core.ItemId == id).Sum(row => row.Core.Count);
    sealed class EdgeRandom(bool last) : Random
    {
        public override long NextInt64(long maxValue) => last ? maxValue - 1 : 0;
    }
}
