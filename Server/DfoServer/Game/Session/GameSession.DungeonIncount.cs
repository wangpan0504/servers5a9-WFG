using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

// 副本入场代价，全部取自 Etc/DungeonEtc/DungeonIncountInfo.etc 与 dgn：
// - [dungeon incount info] 角色日/周次数（所有表内副本、任意城镇、非练习）；同一行多个副本按“同行共用一份次数”处理
//   （客户端 03FEFB50 只在一行 n>1 时分配组号 +0xb4/+0xa0，无读者，结构推断，见 KB）。
// - [account dungeon incount info] 账号次数，入场时连同 [enter add incount dungeon index] 的连带副本一起记次（账号表无客户端读者、无已知下发包）。
// - [sub incount on clear dungeon] 列出的副本入场只核对不记次，通关结算时记次。
// - dgn [required item] 第三列非 0 时入场消耗（任意城镇的计次范围入场；客户端 018E6EDC：非 0 → 47702「消耗%d个」，0 → 47703「持有%d个」）。
internal sealed record DungeonEntryCost(int DungeonId, DungeonIncountRow? Incount, DungeonIncountPeriod Period, DungeonRequiredItem? Consume,
    EpicPieceCatalog? Pieces)
{
    public ImmutableArray<AccountIncountCharge> Account { get; init; } = [];
    public bool ChargeOnClear { get; init; }
    public ImmutableArray<ItemMaterialCost> AdditionalMaterials { get; init; } = [];
    public ItemMaterialCost[] Materials => [.. Consume is { } first ? new[] { new ItemMaterialCost(first.ItemId, first.Count) } : [], .. AdditionalMaterials];
}

internal sealed partial class GameSession
{
    internal bool CanUseDimensionHidden(uint dungeon) => owner.Execute(() => Selected is { } character && dungeon <= int.MaxValue &&
        incounts?.Dimension is { } dimension && dimension.Contains((int)dungeon) && DungeonIncountPolicy.CanEnter(dimension.HiddenRow,
            repository.LoadDungeonIncount(owner, character.Id, DimensionDungeonCatalog.Entrance, IncountPeriod(dimension.DailyRow.CountKey))));

    public uint? AncientGateEnergy(bool entranceOnly = false) => owner.Execute(() => Selected is { } character &&
        (!entranceOnly || character.Position.Town == 17 && character.Position.Area is 3 or 5)
        ? (uint?)AncientGateProgression.Energy(character.Inventory, IncountNow()) : null);

    public (uint BasicUsed, uint ExtraRemaining, byte HiddenRemaining)? DimensionEntranceInfo(bool entranceOnly = false) => owner.Execute(() =>
    {
        if (Selected is not { } character || incounts?.Dimension is not { } dimension ||
            entranceOnly && !(character.Position.Town == 17 && character.Position.Area is 2 or 4))
            return ((uint, uint, byte)?)null;
        var period = IncountPeriod(dimension.DailyRow.CountKey);
        var used = repository.LoadDungeonIncount(owner, character.Id, dimension.DailyRow.CountKey, period);
        var hidden = repository.LoadDungeonIncount(owner, character.Id, DimensionDungeonCatalog.Entrance, period);
        int extra = DimensionEntryTicketService.Extra(character.Inventory, IncountNow());
        used = DimensionDungeonAdmission.NormalizeLegacyOverflow(used, dimension.BaseDailyLimit, extra);
        return ((uint)Math.Min(used.DailyUsed, dimension.BaseDailyLimit),
            (uint)Math.Max(0, extra - Math.Max(0, used.DailyUsed - dimension.BaseDailyLimit)),
            (byte)Math.Max(0, dimension.HiddenWeeklyLimit - hidden.WeeklyUsed));
    });

    public InventoryResult UseDimensionEntryTicket(InventorySlot position,
        DimensionEntryTicketService service)
    {
        return PlanAndCommitInventory(character => incounts?.Dimension is { } dimension
            ? service.Plan(character, position, dimension.ExtraLimit,
                towns.IsRoom(character.Position, character.CompletedQuests.ToHashSet()) ? "[seria room]" : "[village]")
            : InventoryResult.Reject(19), 19, requireTown: true, allowSettlement: false,
            additionalGuard: HasRequiredBaseUsage, usedItem: position);

        bool HasRequiredBaseUsage()
        {
            // 该活动变体明确要求先耗尽基础次数；其余六款仍允许预存追加额度。
            // 捕获及提交前均在原owner内复核，防止规划期间日切或GM刷新后沿用旧资格。
            if (Selected is not { } character || character.Inventory.Find(position)?.Core.ItemId != 490008217) return true;
            return incounts?.Dimension is { } dimension &&
                repository.LoadDungeonIncount(owner, character.Id, dimension.DailyRow.CountKey,
                    DungeonIncountPolicy.Period(service.Clock.GetUtcNow(), incounts.WeeklyResetDay(dimension.DailyRow.CountKey)))
                    .DailyUsed >= dimension.BaseDailyLimit;
        }
    }

    DateTimeOffset IncountNow() => (timeProvider ?? TimeProvider.System).GetUtcNow();
    SingleDungeonAdmission? SingleDungeonBinding(DungeonEntryPlan entry) => !entry.IsPractice &&
        incounts?.SingleGroups.FirstOrDefault(group => group.Id == 1 && group.Dungeons.Contains(checked((int)entry.DungeonId))) is { } group
        ? SingleDungeonAdmission.At(group, timeProvider ?? TimeProvider.System) : null;

    DungeonIncountPeriod IncountPeriod(int dungeon) =>
        DungeonIncountPolicy.Period(IncountNow(), incounts?.WeeklyResetDay(dungeon) ?? DungeonIncountCatalog.DefaultWeeklyResetDay);

    DungeonIncountRow? IncountRow(uint dungeon) =>
        incounts is not null && dungeon <= int.MaxValue && incounts.Find((int)dungeon) is { Limit: > 0 } row
            ? incounts.Dimension?.Contains((int)dungeon) == true && Selected is { } character
                ? row with { Limit = row.Limit + DimensionEntryTicketService.Extra(character.Inventory, IncountNow()) } : row : null;

    // 进入 dungeon 要记的账号次数：自身（Check）+ [enter add incount dungeon index] 连带副本（只记不拦）。
    ImmutableArray<AccountIncountCharge> AccountCharges(int dungeon)
    {
        if (incounts is null) return [];
        var now = IncountNow();
        var charges = ImmutableArray.CreateBuilder<AccountIncountCharge>();
        if (incounts.FindAccount(dungeon) is { Limit: > 0 } own)
            charges.Add(new(dungeon, DungeonIncountPolicy.AccountPeriod(incounts, own, now), own.Limit, true));
        foreach (int linked in incounts.Linked(dungeon))
            if (incounts.FindAccount(linked) is { Limit: > 0 } other)
                charges.Add(new(linked, DungeonIncountPolicy.AccountPeriod(incounts, other, now), other.Limit, false));
        return charges.ToImmutable();
    }

    // 所有有次数上限的角色副本当前日/周剩余（NOTI 550 两个 u8），入城时逐个下发；同行副本取同一份使用量。
    // 账号表副本不下发：客户端世界地图上限只读 [dungeon incount info]，账号次数无已知下发包。
    public IReadOnlyList<DungeonEnterCount> EnterCounts() => owner.Execute(() =>
    {
        if (incounts is null || Selected is not { } character) return (IReadOnlyList<DungeonEnterCount>)[];
        var now = IncountNow();
        var result = new List<DungeonEnterCount>();
        foreach (var row in incounts.Rows)
            if (row.Limit > 0)
            {
                // 追加券写在 character_dungeon_incount_extra，批量读只含已用次数；投影必须走含追加额度的单项读。
                var usage = repository.LoadDungeonIncount(owner, character.Id, row.CountKey,
                    DungeonIncountPolicy.Period(now, incounts.WeeklyResetDay(row.CountKey)));
                // 两祭坛由专用入场账本投影；通用账本从不扣这两项，不能随后以满次数覆盖真实剩余。
                var viewRow = incounts.Dimension?.Contains(row.CountKey) == true
                    ? row with { Limit = row.Limit + DimensionEntryTicketService.Extra(character.Inventory, IncountNow()) } : row;
                result.AddRange(DungeonIncountPolicy.Views(viewRow, usage)
                    .Where(count => count.Dungeon is not (11006 or 11007)));
            }
        return result;
    });

    // 当前副本所属 [dungeon incount info] 行的全部 NOTI 550 视图（同行共用剩余）。
    public IReadOnlyList<DungeonEnterCount> EnterCountsFor(uint dungeon) => owner.Execute(() =>
    {
        if (incounts is null || Selected is not { } character || IncountRow(dungeon) is not { } row)
            return (IReadOnlyList<DungeonEnterCount>)[];
        var usage = repository.LoadDungeonIncount(owner, character.Id, row.CountKey,
            DungeonIncountPolicy.Period(IncountNow(), incounts.WeeklyResetDay(row.CountKey)));
        return DungeonIncountPolicy.Views(row, usage).Where(count => count.Dungeon is not (11006 or 11007)).ToArray();
    });

    DungeonEntryCost? EntryCost(Character expected, uint dungeonId, DungeonDefinition definition, EpicPieceCatalog? pieces, bool channelTown,
        out string? rejection, DungeonIncountUsage? usageOverride = null, DungeonIncountRow? rowOverride = null)
    {
        rejection = null;
        // 未装载 DungeonIncountInfo（旧测试夹具）时保持原行为：不计次也不消耗。
        if (incounts is null && rowOverride is null || dungeonId > int.MaxValue) return null;
        int id = (int)dungeonId;
        var row = rowOverride ?? IncountRow(dungeonId);
        var period = IncountPeriod(row?.CountKey ?? id);
        if (row is not null && !DungeonIncountPolicy.CanEnter(row, usageOverride ?? repository.LoadDungeonIncount(owner, expected.Id, row.CountKey, period)))
        {
            rejection = "dungeon-incount-exhausted";
            return null;
        }
        var account = AccountCharges(id);
        if (account.Any(charge => charge.Check && repository.LoadAccountDungeonIncount(owner, charge.Dungeon, charge.Period) >=
            checked(charge.Limit + repository.LoadAccountDungeonIncountExtra(owner, charge.Dungeon, charge.Period))))
        {
            rejection = "dungeon-account-incount-exhausted";
            return null;
        }
        // [required item] 第三列非 0（客户端 018E6EDC 选 47702「消耗%d个」）：与次数同一范围（任意城镇、非练习的普通入场）都在入场提交时扣除。
        // 频道专属城镇已由 ChannelTownDungeonRequirement 核对持有；其它城镇在这里按入场前背包核对，缺物品拒绝，与 400000016 相同。
        // 第三列为 0（只持有）在普通城镇仍不核对：只有频道专属城镇启用持有检查。
        var consume = DaybreakEntryCost.Select(id, definition.RequiredItem, expected.Inventory, pieces, IncountNow()) is
            { Flag: not 0, Count: > 0 } required ? required : null;
        ImmutableArray<ItemMaterialCost> additional = [];
        if (definition.IsAncientInfinite)
        {
            // 正式远古脚本同时要求无色与无尽的永恒；不能经单行RequiredItem属性变成免费入场。
            var requiredRows = definition.RequiredItemRows;
            if (requiredRows.IsEmpty || requiredRows.Any(item => item.Third != 1))
            { rejection = "ancient-required-items-invalid"; return null; }
            consume = new(requiredRows[0].ItemId, requiredRows[0].Count, 1);
            additional = [.. requiredRows.Skip(1).Select(item => new ItemMaterialCost(item.ItemId, item.Count))];
        }
        if (consume is not null && pieces is null)
        {
            rejection = channelTown ? "channel-town-required-item-unverifiable" : "dungeon-required-item-unverifiable";
            return null;
        }
        if (consume is not null && (!channelTown || definition.IsAncientInfinite) && !DfoServer.Game.Inventory.Services.InventoryMaterialConsumptionService.Plan(expected.Inventory,
                [new ItemMaterialCost(consume.ItemId, consume.Count), .. additional], pieces!, IncountNow()).Accepted)
        {
            rejection = "dungeon-required-item:" + consume.ItemId;
            return null;
        }
        return row is null && account.IsEmpty && consume is null ? null
            : new(id, row, period, consume, pieces) { Account = account, ChargeOnClear = rowOverride is null && incounts?.ChargesOnClear(id) == true, AdditionalMaterials = additional };
    }

    // [sub incount on clear dungeon]：通关结算提交后补记次数，不再拦截（本次已经打完）；周期按通关时刻重算。
    void ChargeIncountOnClear(DungeonEntryCost cost)
    {
        if (Selected is not { } character || incounts is null) return;
        var row = IncountRow(checked((uint)cost.DungeonId));
        var counted = repository.CommitDungeonIncountEntry(owner, character, row?.CountKey ?? cost.DungeonId, IncountPeriod(row?.CountKey ?? cost.DungeonId),
            row, null, AccountCharges(cost.DungeonId), increment: true, check: false);
        if (counted is not null) Selected = counted.Character;
    }
}
