using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Items;
using System.Text.Json;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;

namespace DfoServer.Testing.Inventory;

internal static class BoosterUsePlacesTests
{
    public static void RunCore()
    {
        const string open = "[action usable place]\n", close = "\n[/action usable place]";
        That(BoosterCatalog.ParseUsePlaces(open + "`[village]` `[seria room]` `[dungeon]`" + close).PendingRule is null,
            "副本与城镇、房间并列的合法地点不能导致整份礼包被拒绝");
        foreach (string bodyPlace in new[] { "`[dungeon]`", "`[dungeon]` `[village]`", "`[seria room]` `[dungeon]`" })
        {
            var place = BoosterCatalog.ParseUsePlaces(open + bodyPlace + close);
            That(place.PendingRule is null && place.Dungeon && place.Village == bodyPlace.Contains("[village]") &&
                place.SeriaRoom == bodyPlace.Contains("[seria room]"), "副本允许位保留独立正向地点，不能补造城镇权限");
        }
        foreach (string invalid in new[] { "`[dungeon]` `[dungeon]`", "`[dungeon]` `[unknown]`" })
            That(BoosterCatalog.ParseUsePlaces(open + invalid + close).PendingRule is not null,
                "新增副本位不放宽重复或未知地点");
        That(BoosterCatalog.ParseUsePlaces(open + "`[dungeon]` `[dont use assult]`" + close) is
            { Dungeon: true, ExcludeAssault: true, Village: false, SeriaRoom: false, PendingRule: null },
            "明确允许副本时仍保留突击排除位，不将其忽略为通用副本许可");
        const string body = "`[seria room]` `[village]` `[dont use assult]`";
        // 合成模板验证地点与奖励合同；真实资源完整性由资源回归另行核对。
        foreach (var (sourceId, hatId, jobs) in new (int, int, string)[]
        {
            (490700651, 101550665, "`[swordman]` `[demonic swordman]`"),
            (490700652, 102550646, "`[fighter]`"),
            (490700653, 104550655, "`[gunner]`"),
            (490700654, 106550625, "`[mage]` `[creator mage]`"),
            (490700655, 108550698, "`[priest]`"),
            (490700656, 105550532, "`[at gunner]`"),
            (490700657, 109550488, "`[thief]`"),
            (490700658, 103550403, "`[at fighter]`"),
            (490700659, 107550321, "`[at mage]`"),
            (490700660, 112550202, "`[at swordman]`"),
            (490700661, 413550083, "`[knight]`"),
            (490700662, 414550037, "`[demonic lancer]`"),
        })
        {
            string text = "[stackable type]\n`[usable cera package]` 0\n[minimum level]\n1\n" +
                "[usable job]\n" + jobs + "\n[/usable job]\n[package data]\n" +
                hatId + " 1 490700663 100 490700664 100 490700665 1\n[/package data]\n" + open + body + close;
            var places = BoosterCatalog.ParseUsePlaces(text);
            var definition = BoosterCatalog.Parse(text);
            That(places is { Village: true, SeriaRoom: true, PendingRule: null },
                "突击排除位不覆盖城镇与房间正向地点 " + sourceId);
            That(definition.Available && definition.PackageRewards.SequenceEqual(new BoosterReward[]
                { new(hatId, 1, 1), new(490700663, 1, 100), new(490700664, 1, 100), new(490700665, 1, 1) }),
                "合成十二包保留四奖顺序、权重及原数量 " + sourceId);
        }
        foreach (string invalid in new[] { "`[dont use assult]`", "`[village]` `[unknown]` `[dont use assult]`",
            "`[village]` `[dont use assult]` `[dont use assult]`" })
            That(BoosterCatalog.ParseUsePlaces(open + invalid + close).PendingRule is not null,
                "仅排除位或混入未知地点不能变成通用许可");
        foreach (string allowed in new[] { "`[village]`", "`[seria room]`" })
        {
            var places = BoosterCatalog.ParseUsePlaces(open + allowed + " `[dont use assult]`" + close);
            That(places.PendingRule is null && places.Village == (allowed == "`[village]`") &&
                places.SeriaRoom == (allowed == "`[seria room]`"),
                "排除声明保留原正向地点，不能将仅房间放宽为城镇");
        }
        That(BoosterCatalog.ParseUsePlaces("") == new BoosterUsePlacesDefinition() &&
            BoosterCatalog.ParseUsePlaces(open + "`[village]` `[seria room]`" + close) == new BoosterUsePlacesDefinition(true, true),
            "无声明及既有正向地点定义保持兼容");
    }

    public static void RunResources(TownCatalog towns, CharacterCatalog characters, ItemResources resources)
    {
        var now = DateTimeOffset.UtcNow;
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
            resources.EpicPieces, contracts: resources.Contracts, avatars: resources.AvatarPackages);
        // 独立期望来自已保存的十二源奖励/职业声明，不从待验证的Catalog结果生成。
        foreach (var (sourceId, hatId, jobs) in new (int, int, ushort)[]
        {
            (490700651, 101550665, 513),
            (490700652, 102550646, 2),
            (490700653, 104550655, 4),
            (490700654, 106550625, 1032),
            (490700655, 108550698, 16),
            (490700656, 105550532, 32),
            (490700657, 109550488, 64),
            (490700658, 103550403, 128),
            (490700659, 107550321, 256),
            (490700660, 112550202, 2048),
            (490700661, 413550083, 4096),
            (490700662, 414550037, 8192),
        })
        {
            var source = resources.Items.Get(sourceId);
            BoosterReward[] expected = [new(hatId, 1, 1), new(490700663, 1, 100), new(490700664, 1, 100), new(490700665, 1, 1)];
            var definition = resources.Boosters.Get(sourceId);
            That(source is { MinimumLevel: 1, MaximumLevel: int.MaxValue, RequiredSkill: 0, UseForbidden: false } &&
                source.Jobs == jobs && definition.Available && definition.PackageRewards.SequenceEqual(expected) &&
                !resources.AvatarPackages.Get(sourceId).Recognized &&
                service.UsePlaces(sourceId) == new BoosterUsePlacesDefinition(true, true),
                "真实十二固定包保留完整四奖、原源资格、固定入口与地点并集 " + sourceId);
            var roles = BoosterNativeSelectionTests.ResourceRoles(towns, characters, source, now).ToArray();
            That(roles.Aggregate(0, (mask, role) => mask | 1 << role.Job) == jobs,
                "真实十二包枚举全部资源合法职业，不能静默跳过角色定义 " + sourceId);
            foreach (var role in roles)
            {
                string snapshot = JsonSerializer.Serialize(role);
                var opened = service.OpenCeraPackage(role, new(65, []), out var receipt, now);
                That(opened.Accepted && opened.Plan is not null && receipt is not null &&
                    receipt.Rewards.SequenceEqual(expected.Select(row => new BoosterGrantedItem(row.ItemId, row.Count))) &&
                    JsonSerializer.Serialize(role) == snapshot,
                    "真实固定十二包按每个合法职业完整规划四奖且不提前消费源 " + sourceId + ":" + role.Job);
                var plan = opened.Plan!;
                var items = plan.After.Items.Concat(plan.OverflowItems).ToArray();
                var totals = items.GroupBy(item => item.Core.ItemId).ToDictionary(group => group.Key, group => group.Sum(item => item.Core.Count));
                That(plan.After.Items.All(item => item.Core.ItemId != sourceId) && totals.Count == 4 &&
                    expected.All(row => totals.GetValueOrDefault(row.ItemId) == row.Count) &&
                    items.Where(item => item.Core.Kind == ItemKind.Avatar).All(item => item.Avatar is not null) &&
                    plan.After.Gold == role.Inventory.Gold && plan.After.ReviveCoins == role.Inventory.ReviveCoins,
                    "真实十二包完整实例数量与回执一致，源只在计划中移除且钱包不变 " + sourceId + ":" + role.Job);
            }
        }
    }
}
