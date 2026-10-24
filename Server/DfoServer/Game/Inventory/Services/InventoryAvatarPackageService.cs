using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Settings;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryAvatarPackageService(ItemCatalog items, AvatarPackageCatalog packages,
    GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, GameDebugSettings? debugSettings = null, TimeProvider? clock = null)
{
    public AvatarPackageDefinition Definition(int itemId) => packages.Get(itemId);

    public InventoryResult Open(Character character, AvatarPackageOpenRequest request, out BoosterReceipt? receipt,
        DateTimeOffset? utcNow = null)
    {
        receipt = null;
        bool shopExpiration = debugSettings?.IgnoreCashShopAbsoluteExpiration == true;
        bool ignoreUsablePeriod = debugSettings?.IgnoreCashShopUsablePeriod == true;
        var now = utcNow ?? (clock ?? TimeProvider.System).GetUtcNow();
        var source = Source(character, request.Slot, request.Count, now);
        if (source is null) return InventoryResult.Reject(19);
        var definition = packages.Get(source.Core.ItemId);
        var category = definition.Categories.FirstOrDefault(row => row.Category == request.Category && row.Subcategory == request.Subcategory);
        if (!definition.Available || !definition.AllowsFirstGrowType(character.GrowType) || category is null || !TrySelect(definition, category, request.ItemIds, out var rows))
            return InventoryResult.Reject(19);
        return Grant(character, source, request.Count, rows, request.Choices, false, shopExpiration, ignoreUsablePeriod, out receipt, now);
    }

    public InventoryResult OpenCeraPackage(Character character, CeraPackageOpenRequest request, out BoosterReceipt? receipt,
        DateTimeOffset? utcNow = null)
    {
        receipt = null;
        bool shopExpiration = debugSettings?.IgnoreCashShopAbsoluteExpiration == true;
        bool ignoreUsablePeriod = debugSettings?.IgnoreCashShopUsablePeriod == true;
        // 518成功回执会消耗一个客户端源格；空选择不能当作打开面板成功，也不能自动选职业。
        if (request.Choices.Length == 0) return InventoryResult.Reject(19);
        var now = utcNow ?? (clock ?? TimeProvider.System).GetUtcNow();
        var source = Source(character, request.Slot, 1, now);
        if (source is null) return InventoryResult.Reject(19);
        var definition = packages.Get(source.Core.ItemId);
        if (!definition.Available || !definition.AllowsFirstGrowType(character.GrowType)) return InventoryResult.Reject(19);
        int[] ids = request.Choices.Select(choice => choice.ItemId).ToArray();
        AvatarPackageRow[]? selected = null;
        foreach (var category in definition.Categories)
        {
            if (!TrySelect(definition, category, ids, out var rows)) continue;
            // 男鬼剑/黑暗武士等分类可声明同一套装；完全等价的奖励可匹配，参数冲突则拒绝。
            if (selected is not null && !Equivalent(selected, rows)) return InventoryResult.Reject(19);
            selected ??= rows;
        }
        if (selected is null) return InventoryResult.Reject(19);
        return Grant(character, source, 1, selected, request.Choices.Select(choice => new AvatarPackageChoice(choice.ItemId, choice.Option)).ToArray(),
            true, shopExpiration, ignoreUsablePeriod, out receipt, now);
    }

    InventoryItem? Source(Character character, short slot, int count, DateTimeOffset now)
    {
        var source = character.Inventory.Find(new(InventoryListType.Main, slot));
        if (source is null || !source.Core.IsStackable || count <= 0 || count > source.Core.Count ||
            source.Core.EquipmentLockId != 0 || source.IsExpired(now.ToUnixTimeSeconds())) return null;
        return SourceEligible(character, source, items.Get(source.Core.ItemId)) ? source : null;
    }

    internal static bool SourceEligible(Character character, InventoryItem source, ItemTemplate template) =>
        template.Id == source.Core.ItemId && template.Kind == source.Core.Kind && template.AllowsUse(character.Level, character.Job) &&
        InventoryMoveService.RequiredSkillSatisfied(character, template);

    InventoryResult Grant(Character character, InventoryItem source, int count, AvatarPackageRow[] rows,
        AvatarPackageChoice[] choices, bool allowOverflowMail, bool shopExpiration, bool ignoreUsablePeriod,
        out BoosterReceipt? receipt, DateTimeOffset now)
    {
        receipt = null;
        var selectedIds = rows.Select(row => row.ItemId).ToHashSet();
        if (choices.Any(choice => choice.ItemId <= 0) || choices.Select(choice => choice.ItemId).Distinct().Count() != choices.Length ||
            choices.Any(choice => !selectedIds.Contains(choice.ItemId)))
            return InventoryResult.Reject(19);
        var options = choices.ToDictionary(choice => choice.ItemId, choice => choice.Ability);
        var definitions = new List<(AvatarPackageRow Row, ItemTemplate Template, AvatarPackageType Type, byte Ability)>();
        var totals = new Dictionary<int, int>();
        long total = 0;
        try
        {
            foreach (var row in rows)
            {
                var template = items.Get(row.ItemId);
                var avatar = packages.GetAvatar(row.ItemId);
                // 没有可选属性的光环、武器装扮不发送5B选项；有属性声明的部位仍须显式选择。
                bool hasChoice = options.TryGetValue(row.ItemId, out byte ability);
                if (template.Kind != ItemKind.Avatar || !template.Expiration.SupportsCreation || avatar.PendingRule is not null ||
                    !avatar.Types.IsEmpty && row.Type >= avatar.Types.Length ||
                    !hasChoice && avatar.RequiresAbilityChoice || !avatar.Abilities.Contains(ability)) return InventoryResult.Reject(19);
                int amount = checked(row.Count * count);
                totals[row.ItemId] = checked(totals.GetValueOrDefault(row.ItemId) + amount);
                total = checked(total + amount);
                // 未声明期限表时使用模板期限与默认孔位，不补造7天或30天类型行。
                var type = avatar.Types.IsEmpty ? new AvatarPackageType(template.Expiration.Days, template.AvatarSockets) : avatar.Types[row.Type];
                definitions.Add((row, template, type, ability));
            }
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            return InventoryResult.Reject(19);
        }
        if (totals.Count > BoosterReceipt.MaximumItems || total > int.MaxValue) return InventoryResult.Reject(4);
        var before = character.Inventory;
        var paid = InventoryDeleteService.Plan(before, source.Position, source.Core.Value, count).Plan!.After;
        if (!allowOverflowMail)
        {
            var range = ItemSlotBoundService.Range(ItemKind.Avatar, paid);
            int occupied = paid.Items.Count(item => item.Position.List == InventoryListType.Avatar &&
                item.Position.Index >= range.Start && item.Position.Index <= range.End);
            if (total > range.End - range.Start + 1 - occupied) return InventoryResult.Reject(4);
        }
        var prototypes = new List<InventoryItem>((int)total);
        foreach (var definition in definitions)
        {
            var (row, template, type, ability) = definition;
            int days = template.Expiration.Days > 0 ? template.Expiration.Days : type.Days;
            var expiration = template.Expiration with { Days = days };
            int expires = expiration.Resolve(now, shopExpiration, ignoreUsablePeriod);
            if (expires != 0 && expires <= now.ToUnixTimeSeconds()) return InventoryResult.Reject(19);
            var core = InventoryRewardGrantService.Create(template, utcNow: now) with { Durability = ability, ExpireTime = expires };
            var prototype = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Avatar, 0), core);
            var sockets = type.Sockets;
            prototype = prototype with { Avatar = prototype.Avatar! with
            {
                Socket0 = AvatarSocket.Empty(sockets.First), Socket1 = AvatarSocket.Empty(sockets.Second),
                Socket2 = AvatarSocket.Empty(sockets.Third), Socket3 = AvatarSocket.Empty(sockets.Fourth), Socket4 = AvatarSocket.Empty(sockets.Fifth)
            } };
            for (int i = 0; i < checked(row.Count * count); i++) prototypes.Add(prototype);
        }
        var grant = InventoryRewardGrantService.TryGrantItems(character with { Inventory = paid }, prototypes, items,
            goldLimits, pieces, now, allowOverflowMail: allowOverflowMail);
        if (!grant.Accepted) return InventoryResult.Reject(grant.Error == 19 ? (ushort)19 : (ushort)4);
        receipt = new(source.Core.ItemId, source.Position.Index, totals.Select(pair => new BoosterGrantedItem(pair.Key, pair.Value)).ToArray());
        return new(true, 0, new(before, grant.Plan!.After, "avatar_package_open",
            new(character, EarliestDeadline(source, prototypes), clock, requirePosition: true), grant.Plan.OverflowItems));
    }

    internal static long? EarliestDeadline(InventoryItem source, IReadOnlyList<InventoryItem> rewards)
    {
        long? deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
        // 全部原型先于放置生成；背包与邮件奖励用同一最早期限，不能在等待提交期间带入已到期产物。
        foreach (var reward in rewards)
            if (reward.ExpireTime > 0 && (deadline is null || reward.ExpireTime < deadline.Value)) deadline = reward.ExpireTime;
        return deadline;
    }

    internal static bool TrySelect(AvatarPackageDefinition definition, AvatarPackageCategory category, int[] ids,
        out AvatarPackageRow[] rows)
    {
        rows = [];
        if (category.PendingRule is not null || category.Rows.IsEmpty) return false;
        if (definition.SelectionCount == 0)
        {
            if (!ids.Order().SequenceEqual(category.Rows.Select(row => row.ItemId).Order())) return false;
            rows = category.Rows.ToArray();
            return true;
        }
        if (ids.Length == 0 || ids.Length > definition.SelectionCount || ids.Distinct().Count() != ids.Length) return false;
        var selected = new List<AvatarPackageRow>();
        foreach (int id in ids)
        {
            var matches = category.Rows.Where(row => row.ItemId == id).ToArray();
            if (matches.Length == 0 || matches.Any(row => row != matches[0])) return false;
            selected.Add(matches[0]);
        }
        rows = selected.ToArray();
        return true;
    }

    static bool Equivalent(AvatarPackageRow[] left, AvatarPackageRow[] right) => left.OrderBy(row => row.ItemId)
        .ThenBy(row => row.Count).ThenBy(row => row.Type).ThenBy(row => row.DefaultAbility)
        .SequenceEqual(right.OrderBy(row => row.ItemId).ThenBy(row => row.Count).ThenBy(row => row.Type).ThenBy(row => row.DefaultAbility));
}
