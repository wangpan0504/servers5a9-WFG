using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

// 匹配客户端实际CMD160：完整光环与称号双奖励，不根据目录生成预期请求。
internal static class MixedAvatarPackageTests
{
    const int SourceId = 10164558, AvatarId = 101590348, EquipmentId = 100331604;
    const short SourceSlot = 66;
    internal static byte[] CapturedBody() => Convert.FromHexString("42000100000000004C250E0654F0FA050000");

    public static void RunCore()
    {
        // 预期编号来自真实请求及源资源，不从待测目录生成。
        var request = AvatarPackageRequests.Read(CapturedBody(), 2, 2);
        That(request is { Slot: SourceSlot, Count: 1, Category: 0, Subcategory: 0 } &&
            request.ItemIds.SequenceEqual(new[] { AvatarId, EquipmentId }) && request.Choices.Length == 0,
            "真实18B混合CMD160完整保存光环、称号两个主ID且无装扮options");
    }

    public static void RunResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        RunCore();
        var now = new DateTimeOffset(2026, 10, 31, 9, 49, 40, TimeSpan.Zero);
        var template = resources.Items.Get(SourceId);
        That(template.Kind == ItemKind.Consumable && template.MinimumLevel == 1 &&
            resources.Items.Get(AvatarId).Kind == ItemKind.Avatar &&
            resources.Items.Get(EquipmentId).Kind == ItemKind.Equipment,
            "匹配源及两个不同Kind叶模板存在，使用源资格不继承奖励穿戴等级");
        var source = new InventoryItem(900001, new(InventoryListType.Main, SourceSlot),
            InventoryRewardGrantService.Create(template, utcNow: now));
        var sentinel = source with { Uid = 900002, Position = new(InventoryListType.Main, 67) };
        var before = new InventorySnapshot([source, sentinel], Gold: 321);
        var role = new Character(1, 0, "ObservedMixed", 0, towns.Room, towns.InitialReturn)
            { Job = 0, Level = 4, Inventory = before };
        That(InventoryAvatarPackageService.SourceEligible(role, source, template) && !source.IsExpired(now.ToUnixTimeSeconds()),
            "真实4级角色满足源min1与有效期限，空的两目标页具备空间");
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces);
        var request = AvatarPackageRequests.Read(CapturedBody(), 2, 2);
        var result = service.Open(role, request, out var receipt, now);
        // 先核解析、模板与源资格，再校验完整双奖行为。
        That(result.Accepted,
            "MIXED_OBSERVED_OPEN_REJECTED error=" + result.Error +
            "：真实18B请求应一次消费源并同时发光环和称号，不截断为装扮子集");
        That(result.Plan is not null && ReferenceEquals(result.Plan.Before, before) &&
            ReferenceEquals(role.Inventory, before) && before.Find(source.Position) == source,
            "纯规划以原完整before为基准，提交之前角色和源不变");
        var plan = result.Plan!;
        var after = plan.After;
        That(after.Find(source.Position) is null && after.Find(sentinel.Position) == sentinel &&
            after.Gold == before.Gold && after.Items.Length == 3 && plan.OverflowItems.IsEmpty,
            "只消费所选源，保无关同ID实例与金币，不通过邮件分开发奖");
        var aura = after.Items.Single(item => item.Core.ItemId == AvatarId);
        var title = after.Items.Single(item => item.Core.ItemId == EquipmentId);
        That(aura.Position.List == InventoryListType.Avatar && aura.Avatar is not null &&
            aura.Core.Kind == ItemKind.Avatar && aura.Core.Count == 1 && aura.Core.Durability == 0 &&
            title.Position.List == InventoryListType.Main && title.Core.Kind == ItemKind.Equipment && title.Core.Count == 1,
            "两原奖各一件进入真实独立容器，无能力光环保属性0与完整详情");
        var range = ItemSlotBoundService.Range(ItemKind.Equipment, before);
        That(title.Position.Index >= range.Start && title.Position.Index <= range.End &&
            plan.ChangedSlots.Any(slot => slot.List == InventoryListType.Main) &&
            plan.ChangedSlots.Any(slot => slot.List == InventoryListType.Avatar),
            "称号放合法装备分页，单计划完整标记两个容器变化");
        That(receipt is { ItemId: SourceId, Slot: SourceSlot } && receipt.Rewards.Length == 2 &&
            receipt.Rewards.OrderBy(row => row.ItemId).SequenceEqual(new[] {
                new BoosterGrantedItem(EquipmentId, 1), new BoosterGrantedItem(AvatarId, 1) }.OrderBy(row => row.ItemId)),
            "成功回执完整列出两奖，不能只回光环或只回称号");
        That(plan.CharacterPrecondition is not null && plan.CharacterPrecondition.Matches(role, now.ToUnixTimeSeconds()),
            "双奖单计划保提交时角色、位置与期限资格");
    }

    public static void RunBoundaryResources(TownCatalog towns, ItemResources resources, AvatarPackageCatalog packages)
    {
        MixedAvatarPackageTests.RunResources(towns, resources, packages);
        const int sourceId = 10164558, avatarId = 101590348, equipmentId = 100331604;
        const short sourceSlot = 66;
        var now = new DateTimeOffset(2026, 10, 31, 9, 49, 40, TimeSpan.Zero);
        var source = new InventoryItem(900001, new(InventoryListType.Main, sourceSlot),
            InventoryRewardGrantService.Create(resources.Items.Get(sourceId), utcNow: now));
        var sentinel = source with { Uid = 900002, Position = new(InventoryListType.Main, 67) };
        var before = new InventorySnapshot([source, sentinel], Gold: 321);
        var role = new Character(1, 0, "MixedBoundary", 0, towns.Room, towns.InitialReturn)
            { Job = 0, Level = 4, Inventory = before };
        var service = new InventoryAvatarPackageService(resources.Items, packages, resources.GoldLimits, resources.EpicPieces);
        var request = AvatarPackageRequests.Read(MixedAvatarPackageTests.CapturedBody(), 2, 2);

        foreach (var denied in new[]
        {
            request with { ItemIds = [avatarId] },
            request with { ItemIds = [equipmentId] },
            request with { ItemIds = [avatarId, avatarId] },
            request with { ItemIds = [avatarId, equipmentId, avatarId] },
            request with { ItemIds = [avatarId, equipmentId, sourceId] },
            request with { Category = 1 },
            request with { Subcategory = 1 },
            request with { Choices = [new(equipmentId, 0)] },
            request with { Choices = [new(avatarId, 1)] }
        })
            Reject(role, denied, 19, "混合全领不收缺失、重复、未知或跨类集合及错误能力");

        foreach (var filledKind in new[] { ItemKind.Equipment, ItemKind.Avatar })
        {
            var filled = Filled(filledKind, leaveLast: false);
            var actor = role with { Inventory = before with { Items = [source, sentinel, .. filled] } };
            Reject(actor, request, 4, "仅一个目标容器满页时整笔拒绝，另一页空及源消耗品腾格均不改变结果");
        }
        var oneEach = before with { Items = [source, sentinel,
            .. Filled(ItemKind.Equipment, leaveLast: true), .. Filled(ItemKind.Avatar, leaveLast: true)] };
        var exact = role with { Inventory = oneEach };
        var accepted = service.Open(exact, request, out var receipt, now);
        That(accepted.Accepted && receipt is { Rewards.Length: 2 } && accepted.Plan!.OverflowItems.IsEmpty &&
            accepted.Plan.After.Items.Count(item => item.Core.ItemId == avatarId) == Filled(ItemKind.Avatar, true).Length + 1 &&
            accepted.Plan.After.Items.Count(item => item.Core.ItemId == equipmentId) == Filled(ItemKind.Equipment, true).Length + 1,
            "两个目标分页恰各剩一格时完整双奖一次成功，不要求多余消耗品或快捷栏容量");

        var normal = service.Open(role, request, out _, now);
        var normalPlan = normal.Plan!;
        var aura = normalPlan.After.Items.Single(item => item.Core.ItemId == avatarId);
        var declaredType = packages.GetAvatar(avatarId).Types[2];
        That(declaredType.Days == 364 && declaredType.Sockets == default &&
            aura.ExpireTime == now.AddDays(364).ToUnixTimeSeconds() && aura.Core.ExpireTime == 0 &&
            aura.Avatar is { Socket0: { Type: 239, EmblemId: -1 }, Socket1: { Type: 239, EmblemId: -1 },
                Socket2.Type: 0, Socket3.Type: 0, Socket4.Type: 0 } &&
            aura.Core.Durability == 0 && source.ExpireTime == now.AddDays(30).ToUnixTimeSeconds(),
            "实际光环type2为364天且类型行未声明孔，保模板默认双M空孔；不套源30天或原生七天，空能力落0");
        var sourceDeadline = source.ExpireTime;
        That(normalPlan.CharacterPrecondition!.Matches(role, sourceDeadline - 1) &&
            !normalPlan.CharacterPrecondition.Matches(role, sourceDeadline),
            "双奖与源的最早资格期限冻结到一次提交，源到期等号拒绝旧计划");
        var expired = source with { Core = source.Core with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) } };
        var expiredRole = role with { Inventory = before with { Items = [expired, sentinel] } };
        var expiredResult = service.Open(expiredRole, request, out var expiredReceipt, now);
        That(!expiredResult.Accepted && expiredResult.Error == 19 && expiredResult.Plan is null && expiredReceipt is null &&
            expiredRole.Inventory.Find(expired.Position) == expired && expiredRole.Inventory.Find(sentinel.Position) == sentinel,
            "过期源不消费或生成任一奖励");
        foreach (var choices in new CeraPackageChoice[][] { [], [new(avatarId, 0)] })
        {
            var result = service.OpenCeraPackage(role, new(sourceSlot, choices), out var ceraReceipt, now);
            That(!result.Accepted && result.Plan is null && ceraReceipt is null &&
                ReferenceEquals(role.Inventory, before) && before.Find(source.Position) == source &&
                before.Find(sentinel.Position) == sentinel,
                "160混合已支持不放开未经证明的518空或仅光环选择，不能漏发称号");
        }

        void Reject(Character actor, AvatarPackageOpenRequest denied, ushort error, string label)
        {
            var original = actor.Inventory;
            var result = service.Open(actor, denied, out var rejectedReceipt, now);
            That(!result.Accepted && result.Error == error && result.Plan is null && rejectedReceipt is null &&
                ReferenceEquals(actor.Inventory, original) && original.Find(source.Position) == source &&
                original.Find(sentinel.Position) == sentinel && original.Gold == 321, label);
        }
        ImmutableArray<InventoryItem> Filled(ItemKind kind, bool leaveLast)
        {
            var template = resources.Items.Get(kind == ItemKind.Avatar ? avatarId : equipmentId);
            var range = ItemSlotBoundService.Range(kind, before);
            return Enumerable.Range(range.Start, range.End - range.Start + 1 - (leaveLast ? 1 : 0)).Select(index =>
            {
                var slot = new InventorySlot(ItemSlotBoundService.List(kind), checked((short)index));
                var item = InventoryRewardGrantService.CreateItem(template, slot,
                    InventoryRewardGrantService.Create(template, utcNow: now));
                long uid = (kind == ItemKind.Avatar ? 400000 : 300000) + index;
                return item with { Uid = uid, Core = kind == ItemKind.Avatar ? item.Core with { Value = checked((int)uid) } : item.Core };
            }).ToImmutableArray();
        }
    }
}
