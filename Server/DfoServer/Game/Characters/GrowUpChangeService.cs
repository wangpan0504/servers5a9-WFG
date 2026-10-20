using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Characters;

// CMD785 原始15B：前13B在两个生产者(0129CEF1/0129D419)里都没有初始化，按未定义栈字节忽略；
// offset13 目标分支，offset14 确认标记（首次0，收费确认窗口回调0129D3E0再发1）。
internal readonly record struct GrowUpChangeRequest(byte Target, bool Confirmed, bool? ChecksumMatches = null);
internal sealed record GrowUpChangePlan(Character Expected, byte Target, byte GrowType, int CountBefore, int Fee,
    InventoryPlan Inventory, CharacterLearningState Learning, ImmutableArray<int> Completed);
internal sealed record GrowUpChangePreparation(ushort Error, string Reason, GrowUpChangePlan? Plan = null);
internal sealed record GrowUpChangeCommit(Character Character, int Count);

internal sealed class GrowUpChangeService(GrowUpChangeCatalog table, ClassChangeService branches)
{
    // ACK785/NOTI1153 的次数字段是有符号单字节；达到上界后不再提交，避免回包回绕。
    public const int MaximumCount = sbyte.MaxValue;
    public const string InventoryReason = "growup_change";

    public GrowUpChangeCatalog Table => table;

    public GrowUpChangePreparation Prepare(Character before, int count, byte target, DateTimeOffset now)
    {
        GrowUpChangePreparation Reject(ushort error, string reason) => new(error, reason);
        if (target is < 1 or > 5) return Reject(1, "invalid-target");
        if (before.Job is 9 or 10) return Reject(235, "job-without-class-change");
        if (count is < 0 or >= MaximumCount) return Reject(23, "change-count-limit");
        if (before.Level < table.MinimumLevel || before.Level > table.MaximumLevel) return Reject(23, "level-out-of-range");
        int first = before.GrowType & 15, stage = before.GrowType >> 4;
        if (first == 0) return Reject(19, "untransferred");
        if (stage != 0) return Reject(23, "awakened");
        if (first == target) return Reject(3, "same-class");
        var branch = branches.PlanBranch(before, target, now);
        if (branch.Plan is not { } planned) return Reject(branch.Error, branch.Reason);
        int fee = table.Fee(count);
        if (before.Inventory.Gold < (uint)fee) return Reject(10, "insufficient-gold");
        var after = fee == 0 ? before.Inventory : before.Inventory with { Gold = before.Inventory.Gold - (uint)fee };
        var inventory = new InventoryPlan(before.Inventory, after, InventoryReason);
        return new(0, "prepared", new(before, target, planned.GrowType, count, fee, inventory, planned.Learning, planned.Completed));
    }
}
