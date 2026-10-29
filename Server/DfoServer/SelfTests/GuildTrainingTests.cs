using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Projection;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildTrainingTests
{
    public static void RunCore()
    {
        int before = Count;
        const long now = 2_000_000_000;
        var pieces = new EpicPieceCatalog("[piece list]\n990001 990002\n[/piece list]\n");
        var coin = new InventoryItem(91, new(InventoryListType.Main, 9), new(GuildNpcCatalog.MemberCoinItem, ItemKind.Consumable, 8));
        var second = coin with { Uid = 92, Position = new(InventoryListType.Main, 10) };
        var other = new InventoryItem(93, new(InventoryListType.Main, 11), new(990003, ItemKind.Consumable, 7));
        var inventory = new InventorySnapshot([coin, second, other], Gold: 1234);
        string[] effects = ["guild trainer attack raid buff", "guild trainer critical raid buff",
            "guild trainer speed raid buff", "guild trainer attribute raid buff"];
        GuildNpcBenefitDefinition Definition(int group, int level) => new((uint)(5001 + group * 4 + level - 1),
            400001064, level, false, effects[group], "member coins", 200, "gold", 300000, 0, [1281, level, 25]);
        var selected = Enumerable.Range(0, 4).Select(group => Definition(group, 4)).ToArray();
        var paid = GameSession.PlanGuildTraining(inventory, selected, pieces, now, 180)!;
        That(paid.Snapshot.Coins == 16 && paid.Snapshot.Deadline == now + 180 &&
            paid.Snapshot.States.Length == 16 && paid.Snapshot.States.Count(value => value == 3) == 4 &&
            new[] { 3, 7, 11, 15 }.All(index => paid.Snapshot.States[index] == 3),
            "四类最高档只消耗入图参数各4币，冻结原16格选中状态与180秒期限");
        That(paid.Inventory.Before == inventory && paid.Inventory.After.Items.SequenceEqual([other]) &&
            paid.Inventory.After.Gold == 1234 && paid.Inventory.ChangedSlots.Length == 2,
            "训练币可跨堆叠精确扣除，其他资产与金币保持不变");
        var shortInventory = inventory with { Items = [coin, second with { Core = second.Core with { Value = 7 } }, other] };
        That(GameSession.PlanGuildTraining(shortInventory, selected, pieces, now, 180) is null &&
            shortInventory.Items[0].Core.Count == 8 && shortInventory.Items[1].Core.Count == 7,
            "选中总额差一币时整组跳过，不部分发动也不留下部分扣费");
        That(GameSession.PlanGuildTraining(inventory with { Items = [coin with { Core = coin.Core with { EquipmentLockId = 1 } }, second] },
            selected, pieces, now, 180) is null,
            "锁定币不能补足总额，未锁定堆叠仍不部分扣除");
        var entry = InventoryDeleteService.Plan(inventory, other.Position, other.Core.ItemId, 2).Plan!;
        var trainingAfterEntry = GameSession.PlanGuildTraining(entry.After, [Definition(0, 1)], pieces, now, 180)!;
        var combined = entry.WithAfter(trainingAfterEntry.Inventory.After);
        That(combined.Before == inventory && combined.After.Find(other.Position)!.Core.Count == 5 &&
            combined.After.Find(coin.Position)!.Core.Count == 7 && combined.ChangedSlots.Length == 2,
            "训练费用与已有入场票合并同一计划，保留最初资产快照和两类差量");
        That(paid.Snapshot.Remaining(now) == 180 && paid.Snapshot.Remaining(now + 1) == 179 &&
            paid.Snapshot.Remaining(now + 179) == 1 && paid.Snapshot.Remaining(now + 180) == 0 &&
            paid.Snapshot.Remaining(now + 181) == 0,
            "移房重放只读取剩余期限，不延长效果，到期及到期后均为零");
        That(GameSession.PlanGuildTraining(inventory, [], pieces, now, 180) is null,
            "没有选择训练时不建立扣币或效果计划");
        Reject<InvalidDataException>(() => GameSession.PlanGuildTraining(inventory,
            [Definition(0, 1), Definition(0, 2)], pieces, now, 180), "同类训练不能同时发动两档");
        Reject<InvalidDataException>(() => GameSession.PlanGuildTraining(inventory,
            [Definition(0, 1) with { Parameters = [1281, 0, 25] }], pieces, now, 180), "训练入图扣币不能为零");
        Bytes(GuildTrainingProjection.ActiveBuff(180),
            [1, 1, 5, 0, 0, 0, 0, 0, 0, 180, 0, 0, 0, 0, 0, 0, 0],
            "NOTI487训练1281与剩余180秒使用实际字段边界");
        Bytes(GuildTrainingProjection.ActiveBuff(1),
            [1, 1, 5, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0], "训练最后一秒重放不重置期限");
        Reject<InvalidDataException>(() => GuildTrainingProjection.ActiveBuff(0), "过期训练不能重发零期限永久buff");
        var projection = new GuildTrainingProjectionState();
        That(projection.Plan(1, false) is null && projection.Plan(1, false) is null,
            "从未发动训练的玩家在登录和移房时不追加清除包");
        long first = projection.Plan(1, true)!.Value;
        long replay = projection.Plan(1, true)!.Value;
        That(!projection.IsCurrent(1, first) && projection.IsCurrent(1, replay),
            "移房重放使上一次训练投影失效");
        long cleared = projection.Plan(1, false)!.Value;
        That(!projection.IsCurrent(1, replay) && projection.IsCurrent(1, cleared) && projection.Plan(1, false) is null,
            "返城或到期只清除一次，并阻止旧active投影迟到恢复效果");
        long previousSelection = projection.Plan(1, true)!.Value;
        That(projection.Plan(2, false) is null && !projection.IsCurrent(1, previousSelection),
            "重新选角丢弃旧训练展示记录，不给新角色发送旧效果清除包");
        Console.WriteLine($"PASS {Count - before} guild training entry and projection assertions");
    }
}
