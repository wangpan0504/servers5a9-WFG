using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;

namespace DfoServer;

internal static class QuestProtocolTests
{
    public static void Run()
    {
        int checks = 0;
        byte[] finish = Convert.FromHexString("2200490CFFFF0100FFFF");
        Check(QuestRequests.TryFinish(finish, out var request) && request == new QuestFinishRequest(3145, 65535, 1, 65535),
            "实收10B完成形状完整保留，不把FFFF固定奖励筛掉");
        foreach (int length in new[] { 0, 6, 9, 11, 14 })
            Check(!QuestRequests.TryFinish(new byte[length], out _), "完成请求拒绝长度" + length);
        finish[0] = 33;
        Check(!QuestRequests.TryFinish(finish, out _), "完成请求echo必须34");
        byte[] high = Convert.FromHexString("2200FFFFFFFFFFFFFFFF");
        Check(QuestRequests.TryFinish(high, out var highRequest) && highRequest == new QuestFinishRequest(65535, 65535, 65535, 65535),
            "codec不缩窄未知倍率和reserved合法域");
        Check(QuestRequests.TrySingleClearTrigger(Convert.FromHexString("2100490C0000"), out var trigger) &&
            trigger == new QuestTriggerRequest(3145, 0, 0), "单清图Boolean请求精确6B");
        Check(!QuestRequests.TrySingleClearTrigger(Convert.FromHexString("2100490C000000"), out _), "selector附加7B形状不截断");
        Check(QuestPackets.ClearTrigger(3145, 0).SequenceEqual(Convert.FromHexString("01490C00000000")), "ACK33成功完整7B");
        Check(QuestPackets.Rejected(0x8123).SequenceEqual(Convert.FromHexString("002381")), "ACK34中立错误codec保持完整u16");
        var core = new ItemCore(1000, ItemKind.Consumable, 9)
        {
            AmplifyType = 4, AmplifyValue = 0x1234, ExpireTime = 0x56781234, SortLockFlag = 1, RemainUseCount = 0x81
        };
        var item = new InventoryItem(77, new(InventoryListType.Main, 57), core);
        byte[] rows = QuestPackets.FixedRewardRows([new(item, 2)]);
        using (var stream = new MemoryStream(rows))
        using (var reader = new BinaryReader(stream))
        {
            Check(reader.ReadByte() == 1 && reader.ReadUInt16() == 57 && reader.ReadUInt32() == 1000 && reader.ReadUInt32() == 2,
                "任务奖励使用已提交真实槽位和本次堆叠增量");
            Check(reader.ReadByte() == 4 && reader.ReadUInt16() == 0x1234 && reader.ReadUInt32() == 0x56781234 &&
                reader.ReadByte() == 1 && reader.ReadByte() == 0x81 && stream.Position == stream.Length && rows.Length == 20,
                "19B奖励行保留增幅/期限及反序锁和使用次数");
        }
        var equipment = new InventoryItem(78, new(InventoryListType.Main, 10), new(101010438, ItemKind.Equipment, 999_999_998));
        byte[] equipmentRows = QuestPackets.FixedRewardRows([new(equipment, 1)]);
        using (var reader = new BinaryReader(new MemoryStream(equipmentRows)))
        {
            reader.ReadByte();
            reader.ReadUInt16();
            reader.ReadUInt32();
            Check(reader.ReadUInt32() == 999_999_998, "装备奖励值来自实际品级seed，不将数量1写入品质字段");
        }
        Reject(() => QuestPackets.FixedRewardRows([new(item with { Uid = 0 }, 2)]), "规划临时UID不得发布");
        Reject(() => QuestPackets.FixedRewardRows([new(item, 10)]), "增量不能超过实际库存数量");
        Reject(() => QuestPackets.FixedRewardRows([new(equipment, 2)]), "普通装备只能独立一件");
        int[] fixedItems = [10165055, 101010438, 10403, 12403, 14403, 16403, 18403];
        var fixedRewards = fixedItems.Select((id, index) => new QuestRewardItemProjection(new InventoryItem(100 + index,
            new(InventoryListType.Main, checked((short)(index == 0 ? 57 : index - 1))),
            new(id, index == 0 ? ItemKind.Quest : ItemKind.Equipment, index == 0 ? 1 : 999_999_998)
            {
                AmplifyType = 0x81, AmplifyValue = 0xABCD, ExpireTime = 0x12345678, SortLockFlag = 1, RemainUseCount = 0x92
            }), 1)).ToArray();
        byte[] complete = QuestPackets.FinishSingleClearMap(3145, 1780, fixedRewards);
        using (var stream = new MemoryStream(complete))
        using (var reader = new BinaryReader(stream))
        {
            Check(complete.Length == 143 && reader.ReadByte() == 1 && reader.ReadUInt16() == 3145 && reader.ReadByte() == 3 &&
                reader.ReadUInt32() == 1780 && reader.ReadByte() == 0 && reader.ReadByte() == 7,
                "完整ACK34窄头分类3/实际EXP/item模式0/七项精确143B，无空材料数组");
            for (int index = 0; index < fixedItems.Length; index++)
            {
                Check(reader.ReadUInt16() == fixedRewards[index].Item.Position.Index && reader.ReadUInt32() == fixedItems[index] &&
                    reader.ReadUInt32() == (uint)(index == 0 ? 1 : 999_999_998) && reader.ReadByte() == 0x81 &&
                    reader.ReadUInt16() == 0xABCD && reader.ReadUInt32() == 0x12345678 && reader.ReadByte() == 1 && reader.ReadByte() == 0x92,
                    "独立读器逐项消费实际槽/Core与资源原序 " + index);
            }
            Check(stream.Position == stream.Length, "完整奖励数组后没有猜造金币或额外收尾字段");
        }
        Reject(() => QuestPackets.FinishSingleClearMap(3145, 0, fixedRewards), "没有已证零EXP领奖规则时不能发零成功");
        Reject(() => QuestPackets.FinishSingleClearMap(3146, 1780, fixedRewards), "其他任务不能借3145窄成功头");
        Reject(() => QuestPackets.FinishSingleClearMap(3145, 1780, fixedRewards[..^1]), "少发一件不能领成功");
        Reject(() => QuestPackets.FinishSingleClearMap(3145, 1780, fixedRewards.Reverse().ToArray()), "奖励不可重排");
        Reject(() => QuestPackets.FinishSingleClearMap(3145, 1780,
            [fixedRewards[0] with { Item = fixedRewards[0].Item with { Uid = 0 } }, .. fixedRewards.Skip(1)]), "临时UID不能领成功");
        Console.WriteLine($"PASS {checks} 任务与结算独立协议断言");

        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL 任务协议: " + name);
        }
        void Reject(Action action, string name)
        {
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
                Check(true, name);
                return;
            }
            throw new InvalidOperationException("FAIL 任务协议未拒绝: " + name);
        }
    }
}
