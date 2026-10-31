using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

// 保留匹配客户端真实19B提交；语法、资源计划和界面实测分别验证。
internal static class OmittedDimensionsAvatarPackageTests
{
    const int SourceId = 10101850, AvatarId = 101580477;

    const string ObservedHex = "4200010000000000BDFE0D0601BDFE0D060000";

    public static void RunCore()
    {
        RunObservedCore(Convert.FromHexString(ObservedHex), 66, 0);
        const string omitted = "[stackable type]\n`[booster selection]` 0\n[booster selection num]\n0\n" +
            "[booster select category]\n0 0\n[avatar]\n201 2 4 0 202 3 2 1\n[/avatar]\n[/booster select category]";
        var definition = AvatarPackageCatalog.Parse(omitted);
        That(definition.Available && definition.SelectionCount == 0 && definition.Dimensions.SequenceEqual(new[] { 1 }) &&
            definition.Categories is [{ Category: 0, Subcategory: 0, PendingRule: null }] &&
            definition.Categories[0].Rows.SequenceEqual(new AvatarPackageRow[] { new(201, 2, 4, 0), new(202, 3, 2, 1) }),
            "完全省略维数的固定全领仅归一唯一0/0，完整保留任意编号的数量、类型与默认预览");
        That(AvatarPackageCatalog.Parse("[name]\n`文案中的[booster category num]不是维数声明`\n" + omitted).Available,
            "字符串中的维数文字不能冒成真实声明");
        string explicitZero = omitted.Replace("[booster selection num]", "[booster category num]\n0\n[booster selection num]");
        That(AvatarPackageCatalog.Parse(explicitZero.Replace("[booster selection num]\n0", "[booster selection num]\n1")).Available,
            "原已支持的显式0分类自选语义保持，省略维数限制不反向收窄显式声明");
        foreach (string declaration in new[]
        {
            "[booster category num]\n", "[booster category num]\n1\n", "[booster category num]\n0 0\n",
            "[booster category num]\n-1\n", "[booster category num]\nbad\n", "[booster category num ]\n0\n",
            "[BOOSTER CATEGORY NUM]\n0\n", "[ booster category num]\n0\n", "[/booster category num]\n",
            "[booster category num\n", "[booster category num]\n0\n[booster category num]\n0\n",
            "[booster category num]\n0\n[booster category num ]\n0\n"
        })
            Rejected(declaration + omitted, "空、畸形、重复或孤立维数标签不能冒完全省略");
        foreach (string invalid in new[]
        {
            omitted.Replace("[booster selection num]\n0", "[booster selection num]\n1"),
            omitted.Replace("0 0\n[avatar]", "1 0\n[avatar]"),
            omitted.Replace("0 0\n[avatar]", "0 1\n[avatar]"),
            omitted + "\n[booster select category]\n0 0\n[avatar]\n203 1 4 0\n[/avatar]\n[/booster select category]",
            omitted.Replace("[/booster select category]", ""),
            omitted + "\n[/booster select category]",
            omitted.Replace("[/avatar]", "[/avatar]\n[etc]\n3330 1\n[/etc]")
        })
            Rejected(invalid, "省略维数仍拒非全领、非零/多余/未闭合分类及未知混合奖励，不截断奖励");
        foreach (string rootRule in new[] { "usable datetime", "total usable count", "raid booster item", "cooltime maintenance" })
        {
            var rejected = AvatarPackageCatalog.Parse(omitted + "\n[" + rootRule + "]\n1");
            That(rejected.Recognized && !rejected.Available && rejected.PendingRule == rootRule,
                "省略维数不能绕过未闭合源日期、次数、攻坚或冷却根规则");
        }
        var firstGrow = AvatarPackageCatalog.Parse(omitted + "\n[usable first growtype]\n5\n[/usable first growtype]");
        That(firstGrow.Available && firstGrow.AllowsFirstGrowType(0x25) && !firstGrow.AllowsFirstGrowType(4),
            "省略维数保持原首转资格，不从默认坐标清除源限制");
    }

    static void Rejected(string text, string message)
    {
        var definition = AvatarPackageCatalog.Parse(text);
        That(!definition.Available || definition.Categories.Any(category => category.PendingRule is not null || category.Rows.IsEmpty), message);
    }

    public static void RunResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        RunObservedResources(towns, resources, packages, Convert.FromHexString(ObservedHex), 66, 0);
        var definition = packages.Get(SourceId);
        That(definition.Available && definition.SelectionCount == 0 && definition.Dimensions.SequenceEqual(new[] { 1 }) &&
            definition.Categories is [{ Category: 0, Subcategory: 0, PendingRule: null }] &&
            definition.Categories[0].Rows.SequenceEqual(new[] { new AvatarPackageRow(AvatarId, 1, 4, 0) }),
            "真实478B省略源保持唯一完整类别和全部四列奖励，不写ID准入白名单");
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var template = resources.Items.Get(SourceId);
        var source = new InventoryItem(170101, new(InventoryListType.Main, 66),
            InventoryRewardGrantService.Create(template, utcNow: now));
        var before = new InventorySnapshot([source], Gold: 321);
        var role = new Character(1, 0, "OmittedBoundary", 0, towns.Room, towns.InitialReturn)
            { Job = 0, Level = 4, Inventory = before };
        var request = AvatarPackageRequests.Read(Convert.FromHexString(ObservedHex), 1, 1);
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces);
        // 以下是独立领域边界，能力1不是本次实收body，不能冒另一实机选择。
        var alternate = service.Open(role, request with { Choices = [new(AvatarId, 1)] }, out _, now);
        That(alternate.Accepted && alternate.Plan!.After.Items.Single(item => item.Core.ItemId == AvatarId).Core.Durability == 1,
            "资源合法能力1也保留，不把源默认0或本次观察0强制给其他请求");
        foreach (var invalid in new[]
        {
            request with { Count = 0 }, request with { Category = 1 }, request with { Subcategory = 1 },
            request with { ItemIds = [] }, request with { ItemIds = [AvatarId, AvatarId] },
            request with { ItemIds = [AvatarId + 1] }, request with { Choices = [] },
            request with { Choices = [new(AvatarId, 2)] }, request with { Choices = [new(AvatarId + 1, 0)] }
        })
        {
            var rejected = service.Open(role, invalid, out var receipt, now);
            That(!rejected.Accepted && rejected.Error == 19 && rejected.Plan is null && receipt is null &&
                ReferenceEquals(role.Inventory, before) && before.Find(source.Position) == source,
                "省略维数的非法坐标/数量/完整ID集合/能力不消费源、不规划部分发奖");
        }
    }

    public static void RunObservedCore(ReadOnlySpan<byte> observedBody, short observedSlot, byte observedAbility)
    {
        That(observedAbility is 0 or 1 && observedSlot >= 0,
            "真实取证独立记录须给出非负源槽与叶原文的能力索引0或1，不能以默认0替代观察值");
        var request = AvatarPackageRequests.Read(observedBody, 1, 1);
        That(request is { Count: 1, Category: 0, Subcategory: 0 } && request.Slot == observedSlot &&
            request.ItemIds.SequenceEqual(new[] { AvatarId }) && request.Choices.Length == 1 &&
            request.Choices[0] == new AvatarPackageChoice(AvatarId, observedAbility),
            "原生完全省略维数源的真实请求须独立对应单奖励与实际所选能力；语法或选项不符先停，不猜变体");
    }

    public static void RunObservedResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages,
        ReadOnlySpan<byte> observedBody, short observedSlot, byte observedAbility)
    {
        RunObservedCore(observedBody, observedSlot, observedAbility);
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var template = resources.Items.Get(SourceId);
        var avatarTemplate = resources.Items.Get(AvatarId);
        var avatarDefinition = packages.GetAvatar(AvatarId);
        That(template.Kind == ItemKind.Consumable && template.MinimumLevel == 1 &&
            avatarTemplate.Kind == ItemKind.Avatar && avatarTemplate.MinimumLevel == 1 &&
            avatarTemplate.AvatarSockets == new AvatarSocketTypes(239, 239, 0, 0, 0),
            "具名source和skin叶Kind/min1正确，叶原永久双M亦成为特殊皮肤模板默认孔");
        That(avatarDefinition.PendingRule is null && avatarDefinition.Types.Length == 5 &&
            avatarDefinition.Types[4] == new AvatarPackageType(0, new(239, 239, 0, 0, 0), 0, 0, 4400) &&
            avatarDefinition.RequiresAbilityChoice && avatarDefinition.Abilities.SetEquals(new byte[] { 0, 1 }),
            "独立叶raw证明type4是永久双M及原Cera列4400，能力必须从完整0/1索引选取，不强制source默认0");
        var source = new InventoryItem(170001, new(InventoryListType.Main, observedSlot),
            InventoryRewardGrantService.Create(template, utcNow: now));
        short sentinelSlot = observedSlot == 95 ? (short)96 : (short)95;
        var sentinel = source with { Uid = 170002, Position = new(InventoryListType.Main, sentinelSlot) };
        var before = new InventorySnapshot([source, sentinel], Gold: 321);
        var role = new Character(1, 0, "OmittedRank", 0, towns.Room, towns.InitialReturn)
            { Job = 0, Level = 4, Inventory = before };
        That(source.ExpireTime == 0 && InventoryAvatarPackageService.SourceEligible(role, source, template),
            "源无保存期限段且实际4级Job0满足源min1，空Avatar页具备容量");
        var request = AvatarPackageRequests.Read(observedBody, 1, 1);
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces);
        var result = service.Open(role, request, out var receipt, now);
        // 原请求在维数缺失门被拒，成功计划必须仍完整保留所选能力与原事务前提。
        That(result.Accepted, "OMITTED_DIMENSIONS_OBSERVED_OPEN_REJECTED error=" + result.Error);
        That(result.Plan is not null && ReferenceEquals(result.Plan.Before, before) &&
            ReferenceEquals(role.Inventory, before) && before.Find(source.Position) == source,
            "单计划以原before规划，实际提交前不消费源、不修改角色");
        var plan = result.Plan!;
        var after = plan.After;
        That(after.Find(source.Position) is null && after.Find(sentinel.Position) == sentinel &&
            after.Items.Length == 2 && after.Gold == before.Gold && after.AccountWallet == before.AccountWallet &&
            plan.OverflowItems.IsEmpty && plan.ChangedSlots.Count(slot => slot.List == InventoryListType.Main) == 1 &&
            plan.ChangedSlots.Count(slot => slot.List == InventoryListType.Avatar) == 1,
            "只消费所选源，保同ID哨兵/Gold及账号钱包，原礼包发奖不另扣type表Cera列，不溢出邮寄");
        var reward = after.Items.Single(item => item.Core.ItemId == AvatarId);
        That(reward.Position == new InventorySlot(InventoryListType.Avatar, 0) && reward.Core.Kind == ItemKind.Avatar &&
            reward.Core.Count == 1 && reward.Core.Durability == observedAbility && reward.Core.ExpireTime == 0 &&
            reward.Avatar == new AvatarDetail { Socket0 = AvatarSocket.Empty(239), Socket1 = AvatarSocket.Empty(239) } &&
            reward.ExpireTime == 0,
            "真实所选能力0或1完整保留，type4永久双M空徽章，后三孔及颜色保持原零值，不套7/30天");
        That(receipt is { ItemId: SourceId } && receipt.Slot == observedSlot &&
            receipt.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(AvatarId, 1) }) &&
            plan.CharacterPrecondition is not null && plan.CharacterPrecondition.Matches(role, now.ToUnixTimeSeconds()),
            "单源/完整单奖回执与原位置/角色提交资格保留");
    }
}
