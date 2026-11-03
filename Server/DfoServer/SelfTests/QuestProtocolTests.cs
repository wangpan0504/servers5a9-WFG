using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Game.Quests;
using DfoServer.Network.Parsers;

namespace DfoServer;

internal static class QuestProtocolTests
{
    public static void Run()
    {
        int checks = 0;
        Check(QuestPackets.StoryBookInfo([]).SequenceEqual(new byte[4]), "空703明确清除旧故事簿视图");
        var story = QuestPackets.StoryBookInfo([3108]);
        Check(story.Length == 52 && story[0] == 1 && story[4] == 1 && story[8] == 36 && story[9] == 12 &&
            story.AsSpan(12).IndexOfAnyExcept((byte)0) < 0, "703数量头和48字节完成态记录的字段偏移独立核对");
        Reject(() => QuestPackets.StoryBookInfo([3108,3108]), "703拒绝重复章节");
        Reject(() => QuestPackets.StoryBookInfo([0]), "703不能把无任务标记投影为故事");
        Check(QuestPackets.Finish(100,3,0,[],0,[],1).SequenceEqual(Convert.FromHexString("016400030000000001010000")),
            "首次转职mode1携带实际分支与两树页空计数，完整12B");
        Check(QuestPackets.Finish(100,4,30,[],101,[],5).SequenceEqual(Convert.FromHexString("016400041E00000001050000")),
            "转职模式不借用物品数组；声明金币另按绝对钱包通知校准");
        Reject(() => QuestPackets.Finish(100,3,0,[],0,[],0), "转职分支0不得编码首次转职成功");
        Check(QuestPackets.Finish(100,44,0,[],0,[]).SequenceEqual(Convert.FromHexString("0164002C000000000000")),
            "顺序父任务分类44沿无材料奖励正文，不伪造清图分类3");
        Check(QuestPackets.Finish(100,2,0,[],0,[]).SequenceEqual(Convert.FromHexString("01640002000000000000")),
            "猎杀怪物分类2没有寻物消耗段，使用独立零奖励成功头");
        Check(QuestPackets.Finish(100,1,0,[],0,[]).SequenceEqual(Convert.FromHexString("01640001000000000000")),
            "普通计数清图分类1跳过寻物消耗段，沿通用奖励正文");
        Check(QuestPackets.Finish(100,6,0,[],0,[]).SequenceEqual(Convert.FromHexString("01640006000000000000")),
            "猎杀分类6沿已确认的无材料可变奖励正文，零奖励完整10B");
        Check(QuestRequests.TryAccept(Convert.FromHexString("1F00490C"), out var acceptedId) && acceptedId == 3145 &&
            QuestRequests.TryGiveup(Convert.FromHexString("2000490C"), out var giveupId) && giveupId == 3145,
            "接取、放弃独立echo与u16编号形状");
        Check(!QuestRequests.TryAccept(Convert.FromHexString("2000490C"), out _) &&
            !QuestRequests.TryGiveup(Convert.FromHexString("2000490C00"), out _), "接取放弃拒绝错echo与额外尾部");
        Check(QuestRequests.TryTrigger(Convert.FromHexString("2100490C100000"), out var multi) &&
            multi == new QuestClientTriggerRequest(3145, 0x10, 0, 0) &&
            QuestRequests.TryTrigger(Convert.FromHexString("2100490C0000"), out var single) && single.Channel is null,
            "匹配客户端原生产器分开保留6B普通与7B三通道条件");
        Check(!QuestRequests.TryTrigger(Convert.FromHexString("2100490C110000"), out _) &&
            !QuestRequests.TryTrigger(Convert.FromHexString("2100490C10000000"), out _), "未知通道和附加尾部拒绝");
        Check(QuestPackets.AvailableList(95, [1, 3145, 29999]).SequenceEqual(Convert.FromHexString("0E00000008151000185F220601C918AFEA01")),
            "NOTI21 length与protobuf required0、guide等级和packed编号独立金字节");
        Check(QuestPackets.AvailableList(1, []).SequenceEqual(Convert.FromHexString("06000000081510001801")),
            "空可见列表仍携带required字段，不写入伪任务");
        Check(QuestPackets.Accepted(3145, 1, []).SequenceEqual(Convert.FromHexString("01490C0100000000")) &&
            QuestPackets.GivenUp(3145).SequenceEqual(Convert.FromHexString("01490C")), "ACK31完整8B与ACK32完整3B无赠品形状");
        Check(QuestPackets.ActiveList([new(29, 100, 0), new(0, 3145, 1)]).SequenceEqual(Convert.FromHexString("02000000490C01000000640000000000")),
            "NOTI574保持槽位顺序与全宽条件状态");
        Check(QuestPackets.Accepted(3868,QuestQuestionProgress.Unanswered,[]).SequenceEqual(Convert.FromHexString("011C0F0100000000")) &&
            QuestPackets.ActiveList([new(0,3868,QuestQuestionProgress.Unanswered)]).SequenceEqual(Convert.FromHexString("010000001C0F01000000")),
            "未回答只在存档用哨兵，ACK31和574不得让客户端误判活动问题不存在");
        Check(QuestPackets.Finish(100, 4, 0, [], 0, []).SequenceEqual(Convert.FromHexString("01640004000000000000")),
            "普通会面零经验、零物品完整10B成功合同");
        Check(QuestPackets.Finish(8581,19,600,[],40,[]).SequenceEqual(Convert.FromHexString("0185211358020000000100000000000028000000000000000000000000")),
            "租赁任务19保留真实分类、经验及完整19B金币奖励行，不插入材料数组");
        Reject(() => QuestPackets.Finish(8581,19,600,[new(177,1)],40,[]),
            "租赁任务19不能冒充寻物条件消费材料");
        Check(QuestPackets.Finish(2929,45,17,[],0,[]).SequenceEqual(Convert.FromHexString("01710B2D110000000000")),
            "列表通关45使用原生分类和完整10B零物品正文，不插入材料数组或计数字段");
        Reject(() => QuestPackets.Finish(2929,45,17,[new(177,1)],0,[]),"列表通关45不能消费寻物材料");
        Check(QuestPackets.Finish(100, 0, 0, [new(177, 3)], 0, []).SequenceEqual(Convert.FromHexString("01640000000000000100B100030000000000")),
            "普通寻物先读本次扣除7B行，再读mode0和奖励数，不填剩余库存");
        Check(QuestPackets.Finish(100, 25, 0, [new(177, 3)], 0, []).SequenceEqual(Convert.FromHexString("01640019000000000100B100030000000000")),
            "寻物会面分类25按匹配客户端原码读取同一7B材料数组，再读奖励mode");
        Check(QuestPackets.Finish(100,64,0,[],0,[]).SequenceEqual(Convert.FromHexString("01640040000000000000")) &&
            QuestPackets.Finish(100,65,0,[],0,[]).SequenceEqual(Convert.FromHexString("01640041000000000000")),
            "范围64和演出65跳过材料数组，保留真实分类和零奖励完整正文");
        Check(QuestPackets.Finish(100,4,0,[],0,[],17).SequenceEqual(Convert.FromHexString("016400040000000002010000")) &&
            QuestPackets.Finish(100,4,0,[],0,[],33).SequenceEqual(Convert.FromHexString("016400040000000002020000")),
            "一觉、二觉使用mode2和真实stage，不把packed growtype当转职branch写入");
        byte[] goldOnly = QuestPackets.Finish(100, 4, 0, [], 101, []);
        Check(goldOnly.Length == 29 && goldOnly[9] == 1 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(goldOnly.AsSpan(16)) == 101 &&
            goldOnly.AsSpan(20).ToArray().All(value => value == 0), "金币占完整19B虚拟持有域行，不伪造UID或实体Core");
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
        byte[] complete = QuestPackets.Finish(3145, 3, 1780, [], 0, fixedRewards);
        using (var stream = new MemoryStream(complete))
        using (var reader = new BinaryReader(stream))
        {
            Check(complete.Length == 143 && reader.ReadByte() == 1 && reader.ReadUInt16() == 3145 && reader.ReadByte() == 3 &&
                reader.ReadUInt32() == 1780 && reader.ReadByte() == 0 && reader.ReadByte() == 7,
                "完整ACK34窄头分类3/实际EXP/item模式0/七项精确143B，无空材料数组");
            for (int index = 0; index < fixedItems.Length; index++)
            {
                Check(reader.ReadUInt16() == fixedRewards[index].Item.Position.Index && reader.ReadUInt32() == fixedItems[index] &&
                    reader.ReadUInt32() == (uint)(index == 0 ? 1 : 999_999_998) && reader.ReadByte() == 0x80 &&
                    reader.ReadUInt16() == 0 && reader.ReadUInt32() == 0x12345678 && reader.ReadByte() == 1 && reader.ReadByte() == 0x92,
                    "独立读器逐项消费实际槽/Core与资源原序，隐藏未净化属性 " + index);
            }
            Check(stream.Position == stream.Length, "完整奖励数组后没有猜造金币或额外收尾字段");
        }
        Check(QuestPackets.Finish(3145,3,0,[],0,[]).SequenceEqual(Convert.FromHexString("01490C03000000000000")),
            "单清图允许资源零经验、零奖励，不按3145设置正经验门禁");
        Check(QuestPackets.Finish(100,3,17,[],0,fixedRewards[..2]).Length == 48,
            "其他清图任务沿同一编码，奖励数量由已提交投影提供");
        byte[] countedClear = QuestPackets.Finish(100,1,17,[],0,fixedRewards[..2]);
        Check(countedClear[3] == 1 && countedClear.AsSpan(4).SequenceEqual(QuestPackets.Finish(100,3,17,[],0,fixedRewards[..2]).AsSpan(4)),
            "计数清图与单清图共用完整奖励行，保留真实条件分类");
        Check(QuestPackets.Finish(100,70,17,[],0,[]).SequenceEqual(Convert.FromHexString("01640046110000000000")),
            "阈值父任务70的XP后直接奖励模式，不虚构额外条件字段");
        Check(QuestPackets.Finish(100,4,0,[],123,[],tpAmount: 2).SequenceEqual(Convert.FromHexString("01640004000000001D")),
            "TP模式29固定展示2点，不追加金额、数量或奖励count，金币用绝对持有域通知");
        Reject(() => QuestPackets.Finish(100,4,0,[],0,[],tpAmount: 1),"TP模式不能接受未确认的赠点数量");
        Reject(() => QuestPackets.Finish(100,4,0,[],0,fixedRewards[..1],tpAmount: 2),"TP专用模式不能吞掉真实物品奖励行");
        Check(QuestPackets.Finish(6080,4,17,[],123,[],hellChallenge: true).SequenceEqual(Convert.FromHexString("01C017041100000003")),
            "深渊解锁模式3保留实际经验，末尾没有物品count或金币行，金币用14绝对同步");
        Reject(() => QuestPackets.Finish(6080,4,0,[],0,fixedRewards[..1],hellChallenge: true),"深渊模式不能吞掉普通物品奖励");
        Reject(() => QuestPackets.Finish(6080,4,0,[],0,[],1,hellChallenge: true),"深渊模式不能混合转职奖励");
        Reject(() => QuestPackets.Finish(6080,4,0,[],0,[],tpAmount: 2,hellChallenge: true),"深渊模式不能混合TP奖励");
        Reject(() => QuestPackets.Finish(6080,4,0,[],0,[],currencies: [new(354,3033,1)],hellChallenge: true),"深渊模式不能吞掉钱包材料奖励");

        Check(QuestPackets.Finish(2702,0,0,[new(358,100)],0,[]).SequenceEqual(
                Convert.FromHexString("018E0A000000000001006601640000000000")),
            "附魔师资格是普通寻物成功：消耗后再读 mode0 和零奖励，不发 ACK34 mode20");
        Check(QuestPackets.Finish(2710,0,0,[new(358,100)],0,[]).SequenceEqual(
                Convert.FromHexString("01960A000000000001006601640000000000")),
            "分解师资格同样走寻物 mode0，不把类型 3 写成 stage");
        foreach (byte branch in Enumerable.Range(1,5).Select(value => (byte)value))
            foreach (byte stage in Enumerable.Range(0,3).Select(value => (byte)value))
            {
                byte target = (byte)(branch | stage << 4);
                byte[] career = QuestPackets.Finish(100,3,0,[],0,[],target);
                using var stream = new MemoryStream(career); using var reader = new BinaryReader(stream);
                Check(reader.ReadByte() == 1 && reader.ReadUInt16() == 100 && reader.ReadByte() == 3 && reader.ReadUInt32() == 0 &&
                    reader.ReadByte() == (stage == 0 ? 1 : 2) && reader.ReadByte() == (stage == 0 ? branch : stage),
                    "转职与两级觉醒响应写入实际branch/stage " + target);
                Check(reader.ReadByte() == 0 && reader.ReadByte() == 0 && stream.Position == stream.Length,
                    "匹配客户端职业响应独立读器完整读取两树页，不以提前19替代第二页count " + target);
            }
        Reject(() => QuestPackets.Finish(3145,3,1780,[],0,
            [fixedRewards[0] with { Item = fixedRewards[0].Item with { Uid = 0 } }, .. fixedRewards.Skip(1)]), "临时UID不能领成功");
        var pet = new InventoryItem(0x11223344,new(InventoryListType.Pet,7),new(63493,ItemKind.Creature,0x11223344))
            { Creature=new() };
        Check(QuestPackets.FixedRewardRows([new(pet,1)]).SequenceEqual(
            Convert.FromHexString("01070005F8000044332211000000000000000000")),
            "ACK34宠物仍为19B项，模板决定Pet持有域，value写提交后UID而非数量1");
        Reject(() => QuestPackets.FixedRewardRows([new(pet with { Position=new(InventoryListType.Main,7) },1)]),
            "宠物奖励不能伪装普通背包槽");
        Reject(() => QuestPackets.FixedRewardRows([new(pet with { Creature=null },1)]),"宠物缺详情拒绝");
        Reject(() => QuestPackets.FixedRewardRows([new(pet with { Core=pet.Core with { Value=1 } },1)]),"宠物UID与核心必须一致");
        Reject(() => QuestPackets.FixedRewardRows([new(pet,2)]),"同一宠物实例不能投影数量2");
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
