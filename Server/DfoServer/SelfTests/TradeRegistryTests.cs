using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using DfoServer.Game.Trade;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TradeRegistryTests
{
    public static void RunCore()
    {
        var registry = new TradeRegistry();
        var first = new TradeParticipant(1, 11, 21);
        var second = new TradeParticipant(2, 12, 22);
        var oldFirst = first with { Generation = 10 };
        var item = new InventoryItem(5, new(InventoryListType.Main, 65), new ItemCore(3037, ItemKind.Material, 5));
        That(registry.Invite(first, second, 42, 100, out var tradeId), "建立交易邀请");
        That(registry.Find(first) is { } view && view.Id == tradeId && view.Other(first) == second &&
            registry.Find(oldFirst) is null, "交易查询只向原会话暴露实例和对方身份");
        That(!registry.Invite(second, first, 43, 100, out _), "参与中的角色不可重复邀请");
        That(!registry.Accept(second, oldFirst, 101, out _, out _), "旧代次不能接受邀请");
        That(registry.Accept(second, first, 101, out var accepted, out var acceptedId) && accepted == 42 && acceptedId == tradeId, "接受使用邀请方令牌及交易实例");
        That(registry.Put(first, item, 5, out var slot) && slot == 3, "物品放入服务端分配的交易槽");
        That(!registry.Take(first, slot, out _, tradeId, new(InventoryListType.Main, 66), (uint)item.Core.ItemId, 5, item) &&
            !registry.Take(first, slot, out _, tradeId, item.Position, (uint)item.Core.ItemId + 1, 5, item) &&
            !registry.Take(first, slot, out _, tradeId, item.Position, (uint)item.Core.ItemId, 4, item),
            "取回必须匹配原槽位、物品编号和报价数量");
        That(!registry.Take(first, slot, out _, tradeId, item.Position, (uint)item.Core.ItemId, 5,
            item with { Core = item.Core with { Value = 4 } }), "物品实例变化后不能按旧报价取回");
        That(registry.Take(first, slot, out var taken) && taken.Item == item, "物品按交易行号取回");
        That(registry.Put(first, item, 5, out slot) && slot == 3, "取回后空行可复用");
        That(registry.ChangeGold(first, 100, false, out var gold) && gold == 100 &&
            registry.ChangeGold(first, 25, true, out gold) && gold == 75, "金币取回按增量扣减");
        That(!registry.ChangeGold(first, 76, true, out _) && !registry.ChangeGold(first, uint.MaxValue, false, out _),
            "金币取回不能超额且放置不能溢出");
        That(registry.Lock(first, out var both) && !both && registry.Lock(second, out both) && both, "双方锁定后完成登记");
        That(!registry.ChangeGold(first, 1, false, out _), "锁定后报价不可更改");
        That(!registry.Take(first, slot, out _) && !registry.Put(first, item, 1, out _), "锁定后不能取回或修改物品报价");
        That(registry.Confirm(first, out var ticket) && ticket is null &&
            registry.Confirm(second, out ticket) && ticket is not null, "双方确认后冻结报价");
        That(ticket!.FirstOffer.Gold == 75 && ticket.FirstOffer.Items.Length == 1, "成交快照保留完整报价");
        That(registry.Cancel(oldFirst, tradeId) is null, "旧代次不能取消当前交易");
        That(ticket.TryBeginCommit(), "结算竞争中只有一个提交方");
        That(!ticket.TryBeginCommit() && registry.Cancel(first, tradeId) is null, "提交开始后不能并发取消或重复提交");
        registry.Complete(ticket);
        That(registry.Invite(first, second, 7, 200, out var expiredId), "完成后双方可以重新交易");
        That(registry.Cancel(first, tradeId) is null, "旧交易实例不能清理同会话的新交易");
        That(!registry.Accept(second, first, 231, out _, out _), "过期邀请不能接受");
        That(registry.Invite(first, second, 8, 232, out tradeId) && registry.Accept(second, first, 233, out _, out _), "过期邀请清理双方占用");
        That(!registry.Put(first, item, 5, out _, expiredId) && !registry.ChangeGold(first, 1, false, out _, expiredId) &&
            !registry.Lock(first, out _, expiredId) && !registry.Confirm(first, out _, expiredId),
            "旧交易实例不能修改新实例报价或状态");
        That(registry.Cancel(first, expiredId) is null, "过期实例不能取消后续交易");
        That(registry.Cancel(first, tradeId) is not null && registry.Invite(first, second, 9, 234, out tradeId), "取消清理双方占用");
        TradeTicket? cancelled = null;
        That(registry.Accept(second, first, 235, out _, out _) && registry.Lock(first, out _) && registry.Lock(second, out _) &&
            registry.Confirm(first, out _) && registry.Confirm(second, out cancelled) && cancelled is not null,
            "第二笔交易到达冻结阶段");
        That(registry.Cancel(second, tradeId) is not null && !cancelled!.TryBeginCommit(), "结算开始前取消阻止旧成交快照提交");
        That(registry.Invite(first, second, 10, 300, out _) && registry.Invite(first, second, 11, 331, out _),
            "新邀请自动清理双方过期占用");

        var original = item with { Position = new(InventoryListType.Main, 121) };
        var firstCharacter = new Character(1, 0, "First", 1, new(1, 0, 1, 1), null)
        { Inventory = new([original], Gold: 100) };
        var secondCharacter = new Character(2, 0, "Second", 1, new(1, 0, 1, 1), null)
        { Inventory = new([], Gold: 20) };
        var splitTicket = new TradeTicket(first, second, new(30, [new(3, original, 2)]), new(10, []));
        var split = TradeSettlement.Plan(firstCharacter, secondCharacter, splitTicket);
        That(split is not null && split.First.After.Gold == 80 && split.Second.After.Gold == 40 &&
            split.First.After.Items.Single().Core.Count == 3 && split.Second.After.Items.Single().Core.Count == 2 &&
            split.Second.After.Items.Single().Uid == 0, "拆分交易纯规划保留双方金币和堆叠数量");
        That(TradeSettlement.Plan(firstCharacter with { Inventory = firstCharacter.Inventory with { Items = [] } },
            secondCharacter, splitTicket) is null, "报价物品被移动或使用后不可结算");
        var tooMuch = new TradeTicket(first, second, new(101, []), new(0, []));
        That(TradeSettlement.Plan(firstCharacter, secondCharacter, tooMuch) is null, "金币不足不可结算");
        var coveredByIncoming = new TradeTicket(first, second, new(101, []), new(10, []));
        That(TradeSettlement.Plan(firstCharacter, secondCharacter, coveredByIncoming) is null,
            "对方金币不能弥补报价时的余额不足");
        var fullInventory = secondCharacter.Inventory with
        {
            Items = [.. Enumerable.Range(121, 56).Select(index => new InventoryItem(index + 1000,
                new(InventoryListType.Main, (short)index), new ItemCore(3138, ItemKind.Material, 1)))]
        };
        That(TradeSettlement.Plan(firstCharacter, secondCharacter with { Inventory = fullInventory }, splitTicket) is null,
            "接收方背包满时不可结算");
        var changedItem = firstCharacter with { Inventory = firstCharacter.Inventory with
        { Items = [original with { Core = original.Core with { Value = 4 } }] } };
        That(TradeSettlement.Plan(changedItem, secondCharacter, splitTicket) is null, "报价后物品数量变化不可结算");
    }
}
