using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Settings;
using DfoServer.Game.Currency;

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
        if (!definition.Available || !definition.AllowsFirstGrowType(character.GrowType) || category is null || !TrySelect(definition, category, request.ItemIds, out var rows, out var equipmentRows, out var materialRows))
            return InventoryResult.Reject(19);
        return Grant(character, source, request.Count, rows, request.Choices, false, shopExpiration, ignoreUsablePeriod, out receipt, now, equipmentRows, materialRows);
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
            if (!TrySelect(definition, category, ids, out var rows) || rows.Any(row => row.ForcedAbility is byte forced &&
                !request.Choices.Any(choice => choice.ItemId == row.ItemId && choice.Option == forced))) continue;
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
        out BoosterReceipt? receipt, DateTimeOffset now, AvatarPackageEquipmentRow[]? equipmentRows = null,
        AvatarPackageMaterialRow[]? materialRows = null)
    {
        receipt = null;
        equipmentRows ??= [];
        materialRows ??= [];
        var selectedIds = rows.Select(row => row.ItemId).ToHashSet();
        if (choices.Any(choice => choice.ItemId <= 0) || choices.Select(choice => choice.ItemId).Distinct().Count() != choices.Length ||
            choices.Any(choice => !selectedIds.Contains(choice.ItemId)))
            return InventoryResult.Reject(19);
        var options = choices.ToDictionary(choice => choice.ItemId, choice => choice.Ability);
        var definitions = new List<(AvatarPackageRow Row, ItemTemplate Template, AvatarPackageType Type, byte Ability)>();
        var totals = new Dictionary<int, int>();
        var equipmentDefinitions = new List<(AvatarPackageEquipmentRow Row, ItemTemplate Template)>();
        var materialDefinitions = new List<(ItemTemplate Template, int Amount)>();
        long total = 0, equipmentTotal = 0, materialStacks = 0;
        try
        {
            foreach (var row in rows)
            {
                var template = items.Get(row.ItemId);
                var avatar = packages.GetAvatar(row.ItemId);
                // 没有可选属性的光环、武器装扮由客户端带一项哨兵值；它不是玩家选择，统一落到0。
                bool hasChoice = options.TryGetValue(row.ItemId, out byte ability);
                if (ValidateRowDefinition(row, template, avatar) is not null) return InventoryResult.Reject(19);
                if (row.ForcedAbility is byte forced && (!hasChoice || ability != forced || !avatar.Abilities.Contains(forced)))
                    return InventoryResult.Reject(19);
                if (avatar.RequiresAbilityChoice)
                {
                    if (!hasChoice || !avatar.Abilities.Contains(ability)) return InventoryResult.Reject(19);
                }
                else
                {
                    // 无常量属性声明的部位只接受"未选择"哨兵；其他数值说明客户端与资源不一致。
                    if (hasChoice && ability is not (0 or byte.MaxValue)) return InventoryResult.Reject(19);
                    ability = 0;
                }
                int amount = checked(row.Count * count);
                totals[row.ItemId] = checked(totals.GetValueOrDefault(row.ItemId) + amount);
                total = checked(total + amount);
                // 未声明期限表时使用模板期限与默认孔位，不补造7天或30天类型行。
                var type = avatar.Types.IsEmpty ? new AvatarPackageType(template.Expiration.Days, template.AvatarSockets) : avatar.Types[row.Type];
                definitions.Add((row, template, type, ability));
            }
            foreach (var row in equipmentRows)
            {
                var template = items.Get(row.ItemId);
                if (row.Count == 0 || template.Kind != ItemKind.Equipment || !template.Expiration.SupportsCreation ||
                    template.IsPremiumService || template.HasAcquisitionEffect)
                    return InventoryResult.Reject(19);
                int amount = checked(row.Count * count);
                totals[row.ItemId] = checked(totals.GetValueOrDefault(row.ItemId) + amount);
                equipmentTotal = checked(equipmentTotal + amount);
                equipmentDefinitions.Add((row, template));
            }
            foreach (var row in materialRows)
            {
                var template = items.Get(row.ItemId);
                if (MaterialRowDefinitionError(row, template, pieces) is not null) return InventoryResult.Reject(19);
                int amount = checked(row.Count * count);
                if (amount > template.StackLimit) return InventoryResult.Reject(4);
                totals[row.ItemId] = checked(totals.GetValueOrDefault(row.ItemId) + amount);
                materialStacks = checked(materialStacks + 1);
                materialDefinitions.Add((template, amount));
            }
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            return InventoryResult.Reject(19);
        }
        if (totals.Count > BoosterReceipt.MaximumItems || total + equipmentTotal > int.MaxValue) return InventoryResult.Reject(4);
        var before = character.Inventory;
        var paid = InventoryDeleteService.Plan(before, source.Position, source.Core.Value, count).Plan!.After;
        if (!allowOverflowMail)
        {
            var range = ItemSlotBoundService.Range(ItemKind.Avatar, paid);
            int occupied = paid.Items.Count(item => item.Position.List == InventoryListType.Avatar &&
                item.Position.Index >= range.Start && item.Position.Index <= range.End);
            if (total > range.End - range.Start + 1 - occupied) return InventoryResult.Reject(4);
            if (equipmentTotal != 0)
            {
                var equipmentRange = ItemSlotBoundService.Range(ItemKind.Equipment, paid);
                int equipmentOccupied = paid.Items.Count(item => item.Position.List == InventoryListType.Main &&
                    item.Position.Index >= equipmentRange.Start && item.Position.Index <= equipmentRange.End);
                if (equipmentTotal > equipmentRange.End - equipmentRange.Start + 1 - equipmentOccupied)
                    return InventoryResult.Reject(4);
            }
        }
        short materialStart = 0;
        if (materialStacks != 0)
        {
            var materialRange = ItemSlotBoundService.Range(ItemKind.Material, paid);
            materialStart = materialRange.Start;
            // 兼容材料页和快捷栏已有堆的宽上界；实际空槽与首个兼容堆仍由统一放置器判定。
            int materialCapacity = materialRange.End - materialRange.Start + 1 +
                ItemSlotBoundService.QuickSlotEnd - ItemSlotBoundService.QuickSlotStart + 1;
            if (materialStacks > materialCapacity) return InventoryResult.Reject(4);
        }
        var prototypes = new List<InventoryItem>((int)(total + equipmentTotal + materialStacks));
        foreach (var definition in definitions)
        {
            var (row, template, type, ability) = definition;
            int days = template.Expiration.Days > 0 ? template.Expiration.Days : type.Days;
            var expiration = template.Expiration with { Days = days };
            int expires = expiration.Resolve(now, shopExpiration, ignoreUsablePeriod);
            if (expires != 0 && expires <= now.ToUnixTimeSeconds()) return InventoryResult.Reject(19);
            var core = InventoryRewardGrantService.Create(template, utcNow: now) with { Durability = ability, ExpireTime = expires };
            var prototype = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Avatar, 0), core);
            var sockets = type.Sockets.WithDefaults(template.AvatarSockets);
            prototype = prototype with { Avatar = prototype.Avatar! with
            {
                Socket0 = AvatarSocket.Empty(sockets.First), Socket1 = AvatarSocket.Empty(sockets.Second),
                Socket2 = AvatarSocket.Empty(sockets.Third), Socket3 = AvatarSocket.Empty(sockets.Fourth), Socket4 = AvatarSocket.Empty(sockets.Fifth)
            } };
            for (int i = 0; i < checked(row.Count * count); i++) prototypes.Add(prototype);
        }
        foreach (var (row, template) in equipmentDefinitions)
        {
            for (int i = 0; i < checked(row.Count * count); i++)
            {
                var core = InventoryRewardGrantService.Create(template, utcNow: now, shopExpiration: shopExpiration,
                    ignoreUsablePeriod: ignoreUsablePeriod);
                if (core.IsExpired(now.ToUnixTimeSeconds())) return InventoryResult.Reject(19);
                prototypes.Add(InventoryRewardGrantService.CreateItem(template,
                    new(InventoryListType.Main, ItemSlotBoundService.Range(ItemKind.Equipment, paid).Start), core));
            }
        }
        foreach (var (template, amount) in materialDefinitions)
        {
            var core = InventoryRewardGrantService.Create(template, amount, now, shopExpiration: shopExpiration,
                ignoreUsablePeriod: ignoreUsablePeriod);
            if (core.IsExpired(now.ToUnixTimeSeconds())) return InventoryResult.Reject(19);
            prototypes.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, materialStart), core));
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
        // 518仅确认过纯装扮选择；混合分类不能按装扮子集成功并漏掉装备。
        return category.EquipmentRows.IsEmpty && TrySelect(definition, category, ids, out rows, out _);
    }

    internal static bool TrySelect(AvatarPackageDefinition definition, AvatarPackageCategory category, int[] ids,
        out AvatarPackageRow[] rows, out AvatarPackageEquipmentRow[] equipmentRows)
    {
        rows = [];
        equipmentRows = [];
        // 旧调用方没有材料输出，不能接受装扮和装备子集后漏发材料。
        return category.MaterialRows.IsEmpty && TrySelect(definition, category, ids, out rows, out equipmentRows, out _);
    }

    internal static bool TrySelect(AvatarPackageDefinition definition, AvatarPackageCategory category, int[] ids,
        out AvatarPackageRow[] rows, out AvatarPackageEquipmentRow[] equipmentRows, out AvatarPackageMaterialRow[] materialRows)
    {
        rows = [];
        equipmentRows = [];
        materialRows = [];
        if (category.PendingRule is not null || category.Rows.IsEmpty) return false;
        if (!category.EquipmentRows.IsEmpty || !category.MaterialRows.IsEmpty)
        {
            if (definition.SelectionCount != 0 || !ids.Order().SequenceEqual(category.Rows.Select(row => row.ItemId)
                .Concat(category.EquipmentRows.Select(row => row.ItemId)).Concat(category.MaterialRows.Select(row => row.ItemId)).Order()))
                return false;
            rows = category.Rows.ToArray();
            equipmentRows = category.EquipmentRows.ToArray();
            materialRows = category.MaterialRows.ToArray();
            return true;
        }
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
        .ThenBy(row => row.Count).ThenBy(row => row.Type).ThenBy(row => row.DefaultAbility).ThenBy(row => row.ForcedAbility)
        .SequenceEqual(right.OrderBy(row => row.ItemId).ThenBy(row => row.Count).ThenBy(row => row.Type)
            .ThenBy(row => row.DefaultAbility).ThenBy(row => row.ForcedAbility));
    internal static string? MaterialRowDefinitionError(AvatarPackageMaterialRow row, ItemTemplate template, EpicPieceCatalog pieces)
    {
        if (row.ItemId <= 0 || row.Count == 0 || template.Id != row.ItemId || template.Kind != ItemKind.Material ||
            template.StackLimit <= 0 || !template.Expiration.SupportsCreation || template.IsPremiumService || template.HasAcquisitionEffect ||
            CurrencyWallet.RewardKind(row.ItemId) is not null || pieces.ByPiece.ContainsKey(row.ItemId))
            return "avatar_material_row_definition";
        return null;
    }

    internal static string? ValidateRowDefinition(AvatarPackageRow row, ItemTemplate template, AvatarPackageItemDefinition avatar)
    {
        if (row.Count == 0 || template.Id != row.ItemId || template.Kind != ItemKind.Avatar ||
            !template.Expiration.SupportsCreation || avatar.PendingRule is not null ||
            !avatar.Types.IsEmpty && row.Type >= avatar.Types.Length)
            return "avatar_row_definition";
        if (avatar.RequiresAbilityChoice && avatar.Abilities.IsEmpty ||
            row.ForcedAbility is byte forced && (!avatar.Abilities.Contains(forced) ||
                !avatar.RequiresAbilityChoice && forced is not (0 or byte.MaxValue)))
            return "avatar_row_ability";
        return null;
    }
}
