using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SessionPlanningTests
{
    static void EnchantmentTownPlanning()
    {
        // 复用既有Fixture与公开requiredPhase门禁；这不替代272/601调用方的接线回归。
        using var f = new Fixture();
        const int beadId = 990131, cardId = 990132, gearId = 990133;
        var beforeSeed = f.Read();
        f.Repository.CommitInventory(f.Owner, beforeSeed, new(beforeSeed.Inventory, beforeSeed.Inventory with
        {
            Items = [new(0, new(InventoryListType.Main, 82), new(beadId, ItemKind.Consumable, 3)),
                new(0, new(InventoryListType.Main, 9), new(gearId, ItemKind.Equipment, 1234567)
                { EnchantCardId = 3175, EnchantUpgrade = 2, Extension97 = 0x00012711 })]
        }, "enchant_place_fixture"));
        var bead = new ItemTemplate(beadId, ItemKind.Consumable, 0, 1, 0, 1000, 1, 2, 1,
            "[free]", ushort.MaxValue, 0, "Bead");
        var gear = new ItemTemplate(gearId, ItemKind.Equipment, 13, 1, 0, 1, 1, 2, 1,
            "[free]", ushort.MaxValue, 0, "Title");
        var definition = new EnchantmentDefinition(beadId, 0, 0, [(ItemKind.Equipment, (byte)13)], [])
        { RandomCards = new([cardId, cardId]) };
        var request = new EnchantmentRequest(new(InventoryListType.Main, 82), new(InventoryListType.Main, 9), uint.MaxValue);
        int draws = 0;
        InventoryResult Plan(Character character) => InventoryEnchantmentService.Plan(character, request, 2_000_000_000,
            definition, bead, gear, maximum => { That(maximum == 2, "Town门禁保留多卡位置域"); draws++; return 1; });
        InventoryResult Use() => f.Session.PlanAndCommitInventory(Plan, InventoryEnchantmentService.Rejection,
            requireTown: false, requiredPhase: SessionPhase.Town);
        string Snapshot() => JsonSerializer.Serialize(f.Read());
        string before = Snapshot();
        That(f.Session.CaptureExpertJobTrade() is null && !Use().Accepted && draws == 0 && Snapshot() == before,
            "未选角在272规划门禁与601既有捕获入口均拒绝，零抽样零扣源");
        f.Select(ready: false);
        That(f.Session.CaptureExpertJobTrade() is null && !Use().Accepted && draws == 0 && Snapshot() == before,
            "未Ready在计划前拒绝，不因源和目标合法而提前抽样");
        f.Session.MarkReady();
        That(ReferenceEquals(f.Session.CaptureExpertJobTrade(), f.Session.Selected) && Use().Accepted && draws == 1 &&
            f.Read().Inventory.Find(request.Source)!.Core.Count == 2,
            "Town含赛丽亚房间可计划并原子提交一珠，601现成捕获返回同一当前快照");
        var captured = f.Session.Selected!;
        string inventoryBeforeTravel = JsonSerializer.Serialize(captured.Inventory);
        var stale = f.Session.PlanAndCommitInventory(character =>
        {
            var planned = Plan(character);
            That(f.Session.LeaveRoom(), "在锁外规划期间通过正式出口离开房间");
            return planned;
        }, InventoryEnchantmentService.Rejection, requireTown: false, requiredPhase: SessionPhase.Town);
        That(!stale.Accepted && stale.Plan is null && draws == 2 && f.Read().Revision == captured.Revision + 1 &&
            JsonSerializer.Serialize(f.Read().Inventory) == inventoryBeforeTravel && f.Session.Phase == SessionPhase.Town,
            "计划后位置及角色快照变化时提交拒绝，不扣源、不覆盖目标、不自动重抽");
        That(ReferenceEquals(f.Session.CaptureExpertJobTrade(), f.Session.Selected),
            "村庄资格覆盖房间外Town，不把本批全部村庄声明收窄为赛丽亚房间");
        f.Session.ReturnToRoster();
        before = Snapshot();
        That(f.Session.CaptureExpertJobTrade() is null && !Use().Accepted && draws == 2 && Snapshot() == before,
            "退出Town后仍在计划前拒绝，不用旧地点快照消费剩余源");
    }
}
