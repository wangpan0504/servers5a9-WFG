using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.BattlePass;

// 只由原攻坚资格与奖励writer取得材料；投邮尚非实物入包，不以旧余额或箱子计数。
internal static class BattlePassUndefeatedSongSource
{
    public const int ItemId = 10308807;
    public const string SourceKind = "raid_undefeated_song_acquisition";

    public static bool DeclaresSong(PreparedRaidReward prepared) =>
        !prepared.Grants.IsDefault && prepared.Grants.Any(row => row.ItemId == ItemId && row.Count > 0);

    public static bool Matches(byte category, BattlePassMission definition) => category == 1 &&
        definition is { Id: 48, RawTypeCode: 43, RawParameter: -1, RequiredCount: 300, Experience: 1000 };

    public static long NextCount(BattlePassMission definition, long current, int acquired)
    {
        if (!Matches(1, definition) || current < 0 || current > definition.RequiredCount || acquired <= 0)
            throw new InvalidDataException("不渝之咏来源金额或任务定义无效");
        return Math.Min(definition.RequiredCount, checked(current + acquired));
    }

    public static int DirectAmount(PreparedRaidReward prepared, InventoryPlan plan, DateTimeOffset? utcNow = null)
    {
        if (!DeclaresSong(prepared)) return 0;
        if (plan.Reason != "raid_reward") throw new InvalidDataException("不渝之咏必须沿原攻坚奖励规划取得");
        long declared = 0;
        foreach (var row in prepared.Grants.Where(row => row.ItemId == ItemId))
        {
            if (row.Count <= 0) throw new InvalidDataException("原攻坚材料奖励数量无效");
            declared = checked(declared + row.Count);
        }
        long overflow = 0;
        foreach (var item in plan.OverflowItems.Where(item => item.Core.ItemId == ItemId))
        {
            if (item.Core.Kind != ItemKind.Material || item.Core.Count <= 0)
                throw new InvalidDataException("原攻坚材料投邮核心无效");
            overflow = checked(overflow + item.Core.Count);
        }
        long physical = checked(MainAmount(plan.After) - MainAmount(plan.Before));
        long account = (long)plan.After.AccountWallet.Material(ItemId).Balance - plan.Before.AccountWallet.Material(ItemId).Balance;
        if (physical < 0 || account < 0 || overflow > declared)
            throw new InvalidDataException("不渝之咏授权数量、入包及投邮规划不一致");
        long acquired;
        if (plan.AccountMaterialRaidDefinitions.TryGetValue(ItemId, out var definition))
        {
            var now = utcNow ?? DateTimeOffset.UtcNow;
            if (physical != 0 || overflow != 0 ||
                (definition.WeeklyAccountLimit != 0 || definition.WeeklyCharacterLimit != 0) &&
                plan.AccountMaterialRaidWeek != AccountMaterialService.RaidWeek(now))
                throw new InvalidDataException("不渝之咏攻坚账号材料规划不一致");
            var expected = plan.Before;
            foreach (var row in prepared.Grants.Where(row => row.ItemId == ItemId))
            {
                var grant = AccountMaterialService.Grant(expected, prepared.Character, ItemId, row.Count, definition, now, raidReward: true);
                if (!grant.Accepted || grant.Plan is null)
                    throw new InvalidDataException("不渝之咏攻坚材料额度无效");
                expected = grant.Plan.After;
            }
            if (!expected.AccountWallet.Material(ItemId).Same(plan.After.AccountWallet.Material(ItemId)))
                throw new InvalidDataException("不渝之咏攻坚材料实际余额与额度规划不一致");
            acquired = account;
        }
        else
        {
            if (account != 0 || physical != declared - overflow)
                throw new InvalidDataException("不渝之咏授权数量、入包及投邮规划不一致");
            acquired = physical;
        }
        if (acquired > int.MaxValue) throw new InvalidDataException("不渝之咏攻坚实际增量无效");
        return checked((int)acquired);
    }

    static long MainAmount(InventorySnapshot inventory)
    {
        long amount = 0;
        foreach (var item in inventory.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == ItemId))
        {
            if (item.Core.Kind != ItemKind.Material || item.Core.Count <= 0)
                throw new InvalidDataException("不渝之咏原物理材料核心无效");
            amount = checked(amount + item.Core.Count);
        }
        return amount;
    }

    public static string SourceId(PreparedRaidReward prepared)
    {
        if (prepared.Instance == Guid.Empty || prepared.Character == 0 || !Enum.IsDefined(prepared.RaidType) ||
            prepared.Phase is < 0 or > 1 || string.IsNullOrWhiteSpace(prepared.Source))
            throw new InvalidDataException("不渝之咏原攻坚身份无效");
        string identity = FormattableString.Invariant($"{(byte)prepared.RaidType}:{prepared.Instance:N}:{prepared.Character}:{prepared.Phase}:{prepared.Kind}:{prepared.Card}:{prepared.Source}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceKind + ":" + identity)).AsSpan(0, 16)).ToLowerInvariant();
    }
}
