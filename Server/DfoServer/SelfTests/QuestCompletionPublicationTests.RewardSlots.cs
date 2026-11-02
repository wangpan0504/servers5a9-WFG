using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class QuestCompletionPublicationTests
{
    internal static async Task RunRewardSlotsAsync(TownCatalog towns, PvfArchive archive, CharacterCatalog characters, ItemResources items)
    {
        int checks = Count;
        var learning = SelfTestCatalogs.SkillLearning(archive);
        var growth = SelfTestCatalogs.Growth(archive, learning);
        var parameters = SelfTestCatalogs.QuestRewardParameters(archive);
        foreach (var scenario in new[] { "seeking", "meeting", "parent", "same-id", "merge", "lost" })
        {
            bool parent = scenario is "parent" or "lost", merge = scenario == "merge";
            int rewardId = scenario == "same-id" ? 10165055 : 10164759;
            var child = QuestEligibilityTests.Quest(101) with
            {
                MainQuest = 100, Grade = "[sub]", Condition = "[seeking]", IntData = [10165055,5],
                Attributes = ["not give exp quest"], Rewards = [new(0,rewardId,null),new(0,2,null)]
            };
            var quest = QuestEligibilityTests.Quest(100) with
            {
                Condition = parent ? "[quest clear]" : scenario == "meeting" ? "[seek n meet npc]" : "[seeking]",
                SubType = parent ? 2 : -1, IntData = parent ? [101] : scenario == "meeting" ? [10165055,5,2] : [10165055,5],
                Attributes = ["not give exp quest"], Rewards = parent ? [] : [new(0,rewardId,null),new(0,2,null)]
            };
            using var f = new QuestOrdinaryCompletionTests.Fixture(towns,characters,learning,growth,parameters,items,quest,
                definitions: parent ? [quest,child] : [quest], active: parent ? [new(0,100,0),new(1,101,0)] : [new(0,100,0)],
                loseResult: scenario == "lost", configure: candidate => merge ? candidate with
                {
                    Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.Add(new(0,new(InventoryListType.Main,178),
                        InventoryRewardGrantService.Create(items.Items.Get(rewardId),7))) }
                } : candidate);
            var before = f.Read();
            var preparation = f.Prepare(ushort.MaxValue);
            var prepared = preparation.Plan ?? throw new InvalidOperationException("奖励复用夹具未能规划：" + scenario + ":" + preparation.Reason);
            var reward = prepared.Rewards.Single();
            Check(reward.Position.Index == (merge ? 178 : 177) && reward.GrantedCount == 2,
                "真实选槽复用完整扣除的任务材料槽，已有同ID堆优先合堆：" + scenario);
            var registry = new GameCommandRegistry();
            using var log = new ServerLog(Path.Combine(Path.GetDirectoryName(f.OpenPath)!,"log"));
            new QuestCompletionHandler(f.Catalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,
                log,new CharacterLevelProjection(new(characters))).Register(registry);
            registry.Seal();
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput();
            await using var sender = new GameConnectionSender(f.Owner,stream,stop,0,(_,_,_,_,_)=>{});
            var context = new GameConnectionContext(new(91,IPAddress.Loopback,stream,stop),f.Owner,f.Session,sender);
            var packet = new Packet(1,34,Convert.FromHexString("22006400FFFF0100FFFF"));
            if (scenario == "lost")
            {
                await RejectAsync(typeof(IOException),()=>registry.DispatchAsync(context,packet),"实际提交返回丢失");
                Check(stream.Frames.Count == 0 && f.Receipts == 1 && QuestCompletionRepositoryTests.Same(before,f.Session.Selected!),
                    "返回丢失不提前清客户端槽，原冻结提交可以恢复");
            }
            Check(await registry.DispatchAsync(context,packet) == CommandResult.Handled,"奖励复用实际Handler成功：" + scenario);
            var saved = f.Read();
            int ackIndex = stream.Frames.FindIndex(frame => frame[0] == 1 && U16(frame,1) == 34);
            Check(ackIndex == (parent ? 1 : 0) && stream.Opcodes.Skip(ackIndex).Take(2).SequenceEqual(new ushort[] {34,14}),
                "仅无原生扣料段的替换旧UID先发删槽14，0/25及已有合堆保持原包序：" + scenario);
            var local = before.Inventory.Items.Where(row => row.Position.List == InventoryListType.Main)
                .ToDictionary(row => row.Position.Index,row => (Id:row.Core.ItemId,Count:row.Core.Count));
            if (parent)
            {
                var reset = stream.Frames[0].AsSpan(16);
                Check(reset.Length == 126 && reset[0] == 0 && U16(reset,1) == 1 && U16(reset,3) == 177 &&
                    BinaryPrimitives.ReadInt32LittleEndian(reset[5..]) == -1,"前置通知只删除实际已消费旧槽，不提前灌入奖励数量");
                local.Remove(177);
            }
            using var reader = new BinaryReader(new MemoryStream(stream.Frames[ackIndex][16..]));
            Check(reader.ReadByte() == 1 && reader.ReadUInt16() == 100 && reader.ReadByte() == (parent ? 44 : scenario == "meeting" ? 25 : 0),
                "保留原条件分类，不为展示改写任务类型");
            reader.ReadUInt32();
            if (!parent)
            {
                Check(reader.ReadByte() == 1 && reader.ReadByte() == 0,"原生寻物消费数组保持Main单行");
                short slot = reader.ReadInt16(); int amount = reader.ReadInt32();
                if (local.TryGetValue(slot,out var old))
                {
                    if (old.Count <= amount) local.Remove(slot);
                    else local[slot] = (old.Id,old.Count-amount);
                }
            }
            Check(reader.ReadByte() == 0 && reader.ReadByte() == 1,"普通物品奖励mode0及真实行数不变");
            short destination = reader.ReadInt16(); int id = reader.ReadInt32(), count = reader.ReadInt32();
            // 复现已核验的2377617..2377A5D规则；原码局部执行另行核对，不将此模型称为客户端验收。
            if (!local.TryGetValue(destination,out var prior)) local[destination] = (id,count);
            else if (prior.Id == id) local[destination] = (id,prior.Count+count);
            Check(local[destination].Id == rewardId && count == 2 && local[destination].Count == (merge ? 9 : 2),
                "奖励窗口消费到正确物品和本次增量，合堆不翻倍：" + scenario);
            Check(saved.Inventory.Find(reward.Position) is { } actual && actual.Core.ItemId == rewardId &&
                actual.Core.Count == local[destination].Count && f.Receipts == 1 &&
                before.Inventory.Items.Where(row => row.Position.Index is not (177 or 178)).All(row => saved.Inventory.Find(row.Position) == row),
                "保存与Selected实际资产一致，原UID/Core不受展示修复影响");
            if (scenario == "lost")
                Check(f.Lost!.SamePlan,"返回丢失后的实际发布重放使用原冻结计划及BeforeIndex，不重新规划奖励");
        }
        Console.WriteLine($"PASS {Count-checks} 任务奖励复用槽发布、合堆、同ID替换及提交丢返回断言（非实机）");

        static ushort U16(ReadOnlySpan<byte> bytes,int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    }
}
