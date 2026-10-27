using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

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
}

internal sealed partial class GameSession
{
    DateTimeOffset IncountNow() => (timeProvider ?? TimeProvider.System).GetUtcNow();

    DungeonIncountPeriod IncountPeriod(int dungeon) =>
        DungeonIncountPolicy.Period(IncountNow(), incounts?.WeeklyResetDay(dungeon) ?? DungeonIncountCatalog.DefaultWeeklyResetDay);

    DungeonIncountRow? IncountRow(uint dungeon) =>
        incounts is not null && dungeon <= int.MaxValue && incounts.Find((int)dungeon) is { Limit: > 0 } row ? row : null;

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
        var stored = repository.LoadDungeonIncounts(owner, character.Id).ToDictionary(row => row.Dungeon);
        var now = IncountNow();
        var result = new List<DungeonEnterCount>();
        foreach (var row in incounts.Rows)
            if (row.Limit > 0)
            {
                var period = DungeonIncountPolicy.Period(now, incounts.WeeklyResetDay(row.CountKey));
                var usage = stored.TryGetValue(row.CountKey, out var saved) ?
                    DungeonIncountPolicy.Current(saved.Usage, saved.Period, period) : default;
                // 两祭坛由专用入场账本投影；通用账本从不扣这两项，不能随后以满次数覆盖真实剩余。
                result.AddRange(DungeonIncountPolicy.Views(row, usage)
                    .Where(count => count.Dungeon is not (11006 or 11007)));
            }
        return result;
    });

    DungeonEntryCost? EntryCost(Character expected, uint dungeonId, DungeonDefinition definition, EpicPieceCatalog? pieces, bool channelTown,
        out string? rejection)
    {
        rejection = null;
        // 未装载 DungeonIncountInfo（旧测试夹具）时保持原行为：不计次也不消耗。
        if (incounts is null || dungeonId > int.MaxValue) return null;
        int id = (int)dungeonId;
        var row = IncountRow(dungeonId);
        var period = IncountPeriod(row?.CountKey ?? id);
        if (row is not null && !DungeonIncountPolicy.CanEnter(row, repository.LoadDungeonIncount(owner, expected.Id, row.CountKey, period)))
        {
            rejection = "dungeon-incount-exhausted";
            return null;
        }
        var account = AccountCharges(id);
        if (account.Any(charge => charge.Check && repository.LoadAccountDungeonIncount(owner, charge.Dungeon, charge.Period) >= charge.Limit))
        {
            rejection = "dungeon-account-incount-exhausted";
            return null;
        }
        // [required item] 第三列非 0（客户端 018E6EDC 选 47702「消耗%d个」）：与次数同一范围（任意城镇、非练习的普通入场）都在入场提交时扣除。
        // 频道专属城镇已由 ChannelTownDungeonRequirement 核对持有；其它城镇在这里按入场前背包核对，缺物品拒绝，与 400000016 相同。
        // 第三列为 0（只持有）在普通城镇仍不核对：只有频道专属城镇启用持有检查。
        var consume = definition.RequiredItem is { Flag: not 0, Count: > 0 } required ? required : null;
        if (consume is not null && pieces is null)
        {
            rejection = channelTown ? "channel-town-required-item-unverifiable" : "dungeon-required-item-unverifiable";
            return null;
        }
        if (consume is not null && !channelTown && !DfoServer.Game.Inventory.Services.InventoryMaterialConsumptionService.Plan(expected.Inventory,
                [new ItemMaterialCost(consume.ItemId, consume.Count)], pieces!, DateTimeOffset.UtcNow).Accepted)
        {
            rejection = "dungeon-required-item:" + consume.ItemId;
            return null;
        }
        return row is null && account.IsEmpty && consume is null ? null
            : new(id, row, period, consume, pieces) { Account = account, ChargeOnClear = incounts.ChargesOnClear(id) };
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
