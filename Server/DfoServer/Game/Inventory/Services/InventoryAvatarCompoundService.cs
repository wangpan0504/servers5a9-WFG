using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryAvatarCompoundService(ItemCatalog items, AvatarCompoundCatalog compounds,
    AvatarPackageCatalog avatarPackages, Func<long, long>? random = null, TimeProvider? clock = null)
{
    const ushort Rejection = 22;
    readonly Func<long, long> draw = random ?? Random.Shared.NextInt64;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Apply(Character character, AvatarCompoundRequest request,
        out AvatarCompoundReceipt? receipt, out string? rejection, DateTimeOffset? utcNow = null)
    {
        receipt = null;
        rejection = null;
        var plannedAt = Clock.GetUtcNow();
        var now = utcNow ?? plannedAt;
        long seconds = now.ToUnixTimeSeconds();
        var before = character.Inventory;
        if (request.MaterialSlot < 0 || request.SourceSlot0 < 0 || request.SourceSlot1 < 0 ||
            request.SourceSlot0 == request.SourceSlot1 || request.TargetItemId <= 1 ||
            request.TargetIsRare != (request.TargetItemId == 1 ? 1 : 0))
            return Reject("invalid-request", out rejection);

        AvatarCompoundJobDefinition config;
        InventoryItem? source0 = before.Find(request.Source0), source1 = before.Find(request.Source1);
        InventoryItem? material = before.Find(request.Material);
        try { config = compounds.ForCharacterJob(character.Job); }
        catch (InvalidDataException) { return Reject("job-config", out rejection); }
        if (source0 is null || source1 is null)
            return Reject("source-missing", out rejection);
        if (source0.Core.ItemId != request.SourceItemId0 || source1.Core.ItemId != request.SourceItemId1)
            return Reject("source-mismatch", out rejection);
        if (!SourceEligible(source0, seconds) || !SourceEligible(source1, seconds))
            return Reject("source-state", out rejection);
        if (material is null || material.Position.List != InventoryListType.Main || !material.Core.IsStackable ||
            material.Core.Count < 1 || material.Core.EquipmentLockId != 0 || material.IsExpired(seconds))
            return Reject("material-state", out rejection);

        ItemTemplate sourceTemplate0, sourceTemplate1, materialTemplate, targetTemplate;
        AvatarCompoundPartPool pool;
        string part;
        try
        {
            sourceTemplate0 = items.Get(source0.Core.ItemId);
            sourceTemplate1 = items.Get(source1.Core.ItemId);
            materialTemplate = items.Get(material.Core.ItemId);
            targetTemplate = items.Get(request.TargetItemId);
            part = AvatarCompoundCatalog.Part(sourceTemplate0.EquipmentSlot) ?? "";
            if (part.Length == 0 || AvatarCompoundCatalog.Part(sourceTemplate1.EquipmentSlot) != part ||
                AvatarCompoundCatalog.Part(targetTemplate.EquipmentSlot) != part || !config.Parts.TryGetValue(part, out pool!))
                return Reject("part-mismatch", out rejection);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            return Reject("item-metadata", out rejection);
        }
        int grade0 = sourceTemplate0.Grade, grade1 = sourceTemplate1.Grade;
        if (!TemplateMatches(character, source0, sourceTemplate0) || !TemplateMatches(character, source1, sourceTemplate1) ||
            grade0 != config.Grade && grade0 != config.UpperGrade ||
            grade1 != config.Grade && grade1 != config.UpperGrade)
            return Reject("source-template", out rejection);
        if (materialTemplate.Id != material.Core.ItemId || materialTemplate.Kind != material.Core.Kind)
            return Reject("material-template", out rejection);

        int materialBonus;
        if (material.Core.ItemId == config.MaterialItemId) materialBonus = 0;
        else if (material.Core.ItemId == config.UpperMaterialItemId) materialBonus = 200;
        else if (material.Core.ItemId == config.MasterMaterialItemId) materialBonus = 400;
        else return Reject("material-id", out rejection);
        if (!pool.TargetPool.Any(candidate => candidate.ItemId == request.TargetItemId) ||
            !OutputTemplateMatches(character, targetTemplate, part) ||
            targetTemplate.Grade != config.Grade && targetTemplate.Grade != config.UpperGrade ||
            !AbilityAllowed(targetTemplate.Id, request.Ability))
            return Reject("target-selection", out rejection);

        int rareRate = config.RareRates[part];
        int upperRate = config.UpperRareRates[part];
        int combinedRate = grade0 == config.Grade && grade1 == config.Grade ? rareRate :
            grade0 == config.UpperGrade && grade1 == config.UpperGrade ? checked(rareRate + upperRate) :
            checked(rareRate + upperRate / 2);
        combinedRate = Math.Clamp(checked(combinedRate + materialBonus), 0, 10000);
        int rareRoll;
        try { rareRoll = checked((int)Next(10000)); }
        catch (InvalidDataException) { return Reject("random-range", out rejection); }
        bool hitRare = rareRoll < combinedRate && !pool.RarePool.IsEmpty;
        var outputTemplates = new List<ItemTemplate> { targetTemplate };
        if (hitRare)
        {
            AvatarCompoundCandidate rare;
            try { rare = Pick(pool.RarePool); }
            catch (Exception error) when (error is InvalidDataException or OverflowException)
            {
                return Reject("rare-pool", out rejection);
            }
            ItemTemplate rareTemplate;
            try { rareTemplate = items.Get(rare.ItemId); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
            {
                return Reject("rare-metadata", out rejection);
            }
            if (!OutputTemplateMatches(character, rareTemplate, part) || !AbilityAllowed(rareTemplate.Id, request.Ability))
                return Reject("rare-selection", out rejection);
            outputTemplates.Add(rareTemplate);
        }

        var paid = Delete(before, source0, source0.Core.Value);
        if (paid is null)
            return Reject("delete-conflict", out rejection);
        paid = Delete(paid, source1, source1.Core.Value);
        if (paid is null)
            return Reject("delete-conflict", out rejection);
        paid = Delete(paid, material, material.Core.ItemId);
        if (paid is null)
            return Reject("delete-conflict", out rejection);
        var range = ItemSlotBoundService.Range(ItemKind.Avatar, paid);
        var free = Enumerable.Range(range.Start, range.End - range.Start + 1).Select(index => checked((short)index))
            .Where(slot => paid.Find(new(InventoryListType.Avatar, slot)) is null)
            .Take(outputTemplates.Count).ToArray();
        if (free.Length != outputTemplates.Count)
            return Reject("avatar-capacity", out rejection);

        var outputs = ImmutableArray.CreateBuilder<InventoryItem>(outputTemplates.Count);
        try
        {
            for (int index = 0; index < outputTemplates.Count; index++)
            {
                var template = outputTemplates[index];
                var position = new InventorySlot(InventoryListType.Avatar, free[index]);
                var core = InventoryRewardGrantService.Create(template, utcNow: now) with { Durability = request.Ability };
                var output = InventoryRewardGrantService.CreateItem(template, position, core);
                if (output.IsExpired(seconds))
                    return Reject("output-expired", out rejection);
                outputs.Add(output);
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return Reject("output-metadata", out rejection);
        }
        var after = paid with
        {
            Items = paid.Items.AddRange(outputs).OrderBy(item => item.Position.List)
                .ThenBy(item => item.Position.Index).ToImmutableArray()
        };
        after.Validate(allocating: true);
        long? deadline = EarliestDeadline([source0, source1, material, .. outputs]);
        receipt = new(material.Core.ItemId, request.MaterialType, part, combinedRate, rareRoll, hitRare,
            outputs.Select(output => new AvatarCompoundOutput(output.Position, output.Core.ItemId)).ToImmutableArray());
        return new(true, 0, new InventoryPlan(before, after, "avatar_compound",
            new InventoryCharacterPrecondition(character, deadline))
        {
            AvatarCompound = new(Guid.NewGuid(), plannedAt, Clock, request, receipt, source0, source1,
                material, before, paid, after)
        });
    }

    static bool SourceEligible(InventoryItem source, long seconds) =>
        source.Position.List == InventoryListType.Avatar && source.Core.Kind == ItemKind.Avatar &&
        source.Avatar is not null && source.Core.EquipmentLockId == 0 && !source.IsExpired(seconds);

    static bool TemplateMatches(Character character, InventoryItem source, ItemTemplate template) =>
        template.Id == source.Core.ItemId && template.Kind == ItemKind.Avatar && source.Core.Kind == template.Kind &&
        AvatarCompoundCatalog.Part(template.EquipmentSlot) is not null && character.Job < 16 &&
        (template.Jobs & (1 << character.Job)) != 0;

    static bool OutputTemplateMatches(Character character, ItemTemplate template, string part) =>
        template.Kind == ItemKind.Avatar && AvatarCompoundCatalog.Part(template.EquipmentSlot) == part &&
        character.Job < 16 && (template.Jobs & (1 << character.Job)) != 0;

    bool AbilityAllowed(int itemId, ushort ability)
    {
        if (ability > byte.MaxValue)
            return false;
        try
        {
            var definition = avatarPackages.GetAvatar(itemId);
            return definition.PendingRule is null && definition.Abilities.Contains((byte)ability);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            return false;
        }
    }

    AvatarCompoundCandidate Pick(ImmutableArray<AvatarCompoundCandidate> candidates)
    {
        long total = candidates.Aggregate(0L, (sum, candidate) => checked(sum + candidate.Weight));
        long value = Next(total);
        foreach (var candidate in candidates)
        {
            if (value < candidate.Weight)
                return candidate;
            value -= candidate.Weight;
        }
        throw new InvalidDataException("装扮合成加权抽取失败");
    }

    long Next(long bound)
    {
        if (bound <= 0)
            throw new InvalidDataException("装扮合成随机上界无效");
        long value = draw(bound);
        if (value < 0 || value >= bound)
            throw new InvalidDataException("装扮合成随机值越界");
        return value;
    }

    static InventorySnapshot? Delete(InventorySnapshot inventory, InventoryItem item, int value)
    {
        var result = InventoryDeleteService.Plan(inventory, item.Position, value, 1);
        return result.Accepted ? result.Plan!.After : null;
    }

    static long? EarliestDeadline(IEnumerable<InventoryItem> items)
    {
        long? deadline = null;
        foreach (var item in items)
            if (item.ExpireTime > 0 && (deadline is null || item.ExpireTime < deadline.Value))
                deadline = item.ExpireTime;
        return deadline;
    }

    static InventoryResult Reject(string reason, out string? rejection)
    {
        rejection = reason;
        return InventoryResult.Reject(Rejection);
    }
}
