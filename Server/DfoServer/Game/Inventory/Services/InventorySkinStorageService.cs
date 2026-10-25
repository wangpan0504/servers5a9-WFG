using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventorySkinStorageService(ItemCatalog items, SkinStorageCatalog skins,
    GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, PremiumContractCatalog contracts, TimeProvider? time = null)
{
    public const byte Type = 4;
    public const byte DamageFontType = 2;
    public const byte CutsceneType = 1;
    public const uint AddAction = 199;
    public TimeProvider Clock { get; } = time ?? TimeProvider.System;
    public bool Recognizes(int id) => skins.IsToken(id);
    public bool HasDamageFonts => skins.HasDamageFonts;
    public bool HasCutscenes => skins.HasCutscenes;

    public InventoryResult Make(Character character, MakeWeaponSkinRequest request)
    {
        var now = Clock.GetUtcNow();
        var before = character.Inventory;
        var source = before.Find(request.Source);
        if (request.Type != Type || request.Source.List != InventoryListType.Main || source is null ||
            source.Core.Kind != ItemKind.Equipment || source.Core.IsRental ||
            !skins.Recipes.TryGetValue(request.Recipe, out var recipe) || !skins.Weapons.ContainsKey(source.Core.ItemId))
            return InventoryResult.Reject(3);
        if (source.Core.EquipmentLockId != 0) return InventoryResult.Reject(214);
        var original = Weapon(source.Core.ItemId);
        if (original is null) return InventoryResult.Reject(3) with { RejectReason = "invalid_weapon_template" };
        if (source.ExpireTime != 0 || original.DisjointForbidden) return InventoryResult.Reject(93);
        if (original.EquipmentSlot != 12 || skins.Token(recipe.ResultItemId) is not { Type: Type })
            return InventoryResult.Reject(3);
        var resultTemplate = items.Get(recipe.ResultItemId);
        if (resultTemplate.Kind != ItemKind.Consumable) return InventoryResult.Reject(3) with { RejectReason = "invalid_result_template" };
        bool accountBound = resultTemplate.AttachType.Contains("[account]", StringComparison.OrdinalIgnoreCase);
        if (!accountBound && !CanWear(character, original, now.ToUnixTimeSeconds()))
            return InventoryResult.Reject(106);
        if (!accountBound && before.Skins.Owned.Contains(original.Id)) return InventoryResult.Reject(20);
        if (!accountBound && before.Skins.Owned.Length == ushort.MaxValue) return InventoryResult.Reject(204);

        var removed = InventoryDeleteService.Plan(before, source.Position, source.Core.Value, 1);
        if (!removed.Accepted) return InventoryResult.Reject(3);
        var paid = InventoryMaterialConsumptionService.Plan(removed.Plan!.After, recipe.Materials, pieces, now);
        if (!paid.Accepted) return InventoryResult.Reject(3) with { RejectReason = "missing_material" };
        // 027F7AC0 将123B条目+14送入普通核心+24；028B3E30→020C6300据此恢复武器名和图标。
        var core = InventoryRewardGrantService.Create(resultTemplate, utcNow: now) with { EnchantCardId = original.Id };
        var reward = InventoryRewardGrantService.CreateItem(resultTemplate, new(InventoryListType.Main, 0), core);
        var grant = InventoryRewardGrantService.TryGrantItems(character with { Inventory = paid.Plan!.After }, [reward], items, goldLimits, pieces, now);
        long? deadline = paid.Plan.ChangedSlots.Select(slot => paid.Plan.BeforeIndex.Find(slot)?.ExpireTime ?? 0)
            .Where(expiry => expiry > 0).Select(expiry => (long?)expiry).Min();
        return !grant.Accepted ? InventoryResult.Reject(3) with { RejectReason = "inventory_full" } : new(true, 0,
            new(before, grant.Plan!.After, "weapon_skin_make", new(character, deadline, Clock)));
    }

    public SkinStorageKey? Key(InventoryItem? item) => item is not null && skins.Token(item.Core.ItemId) is { } token
        ? new(token.Type, token.Type == Type ? item.Core.EnchantCardId : token.SkinId) : null;

    public ImmutableArray<SkinStorageKey> Keys(InventoryItem? item) => item is not null && skins.Token(item.Core.ItemId) is { } token
        ? token.SkinIds.Select(id => new SkinStorageKey(token.Type, token.Type == Type ? item.Core.EnchantCardId : id)).ToImmutableArray() : [];

    public InventoryResult Add(Character character, InventorySlot position, bool town)
    {
        var now = Clock.GetUtcNow();
        var before = character.Inventory;
        var token = before.Find(position);
        if (position.List != InventoryListType.Main || token is null ||
            token.Core.Kind != ItemKind.Consumable || token.Core.EquipmentLockId != 0 || token.IsExpired(now.ToUnixTimeSeconds()) ||
            skins.Token(token.Core.ItemId) is not { } definition ||
            !items.Get(token.Core.ItemId).AllowsUse(character.Level, character.Job) ||
            !definition.Places.IsEmpty && !definition.Places.Contains("[all]") &&
                !definition.Places.Contains(town ? "[village]" : "[dungeon]"))
            return InventoryResult.Reject(3) with { RejectReason = "invalid_skin_token_or_context" };
        var state = before.Skins;
        long? deadline = token.ExpireTime > 0 ? token.ExpireTime : null;
        if (definition.Type == Type)
        {
            var weapon = Weapon(token.Core.EnchantCardId);
            if (weapon is null || !SupportsCharacter(character, weapon) ||
                state.Owned.Length == ushort.MaxValue || state.Owned.Contains(weapon.Id))
                return InventoryResult.Reject(3) with { RejectReason = "weapon_skin_unavailable_or_owned" };
            state = state with { Owned = state.Owned.Add(weapon.Id) };
        }
        else
        {
            if (!TryAddTimed(state, definition, now, out state)) return InventoryResult.Reject(3) with { RejectReason = "skin_batch_unavailable" };
            if (definition.Expiration.AbsoluteTime > 0)
                deadline = Math.Min(deadline ?? long.MaxValue, definition.Expiration.Resolve(now));
        }
        var removed = InventoryDeleteService.Plan(before, position, token.Core.ItemId, 1);
        if (!removed.Accepted) return InventoryResult.Reject(3);
        return new(true, 0, new(before, removed.Plan!.After with { Skins = state }, "skin_storage_add",
            new(character, deadline, Clock)));
    }

    internal static bool TryAddTimed(SkinStorageState before, SkinStorageToken definition, DateTimeOffset now, out SkinStorageState after)
    {
        after = before;
        if (definition.Type is not (CutsceneType or DamageFontType) || definition.SkinIds.IsDefaultOrEmpty ||
            definition.SkinIds.Length > byte.MaxValue || definition.SkinIds.Distinct().Count() != definition.SkinIds.Length) return false;
        int defaultId = definition.Type == CutsceneType ? 30000 : 1;
        var owned = (definition.Type == CutsceneType ? before.Cutscenes : before.DamageFonts).ToDictionary(row => row.SkinId);
        foreach (int skinId in definition.SkinIds)
        {
            if (skinId <= 0 || skinId == defaultId) return false;
            var previous = owned.GetValueOrDefault(skinId);
            if (previous is { ExpiresAt: 0 } || previous is null && owned.Count == ushort.MaxValue - 1) return false;
            int expiry = definition.Expiration.Resolve(now);
            if (expiry > 0 && expiry <= now.ToUnixTimeSeconds()) return false;
            if (definition.Expiration.Days > 0 && previous is not null)
                expiry = (int)Math.Min(ItemExpiration.MaximumUnixTime,
                    Math.Max(previous.ExpiresAt, now.ToUnixTimeSeconds()) + (long)definition.Expiration.Days * 86400);
            else if (expiry > 0 && previous is not null) expiry = Math.Max(expiry, previous.ExpiresAt);
            owned[skinId] = new(skinId, expiry);
        }
        var updated = owned.Values.OrderBy(row => row.SkinId).ToImmutableArray();
        after = definition.Type == CutsceneType ? before with { Cutscenes = updated } : before with { DamageFonts = updated };
        return true;
    }

    public InventoryResult Select(Character character, SelectSkinRequest request)
    {
        var state = character.Inventory.Skins;
        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        if (request.AdditionalIds.IsDefault || request.AdditionalIds.Length > 9) return InventoryResult.Reject(19);
        if (request.Type == CutsceneType)
        {
            var ids = request.Ids.Where(id => id != 0).ToImmutableArray();
            if (!HasCutscenes || request.AdditionalIds.IsDefault || request.Ids.Any(id => id < 0) ||
                ids.Length is 0 or > 10 || ids.Distinct().Count() != ids.Length ||
                ids.Any(id => !skins.Cutscenes.Contains(id) || id != 30000 && !state.Cutscenes.Any(row => row.SkinId == id && row.Active(now))))
                return InventoryResult.Reject(19);
            long? deadline = state.Cutscenes.Where(row => ids.Contains(row.SkinId) && row.ExpiresAt > 0)
                .Select(row => (long?)row.ExpiresAt).Min();
            return new(true, 0, new(character.Inventory, character.Inventory with
                { Skins = state with { SelectedCutscenes = ids } }, "skill_cutscene_select", new(character, deadline, Clock)));
        }
        if (!request.AdditionalIds.IsDefaultOrEmpty && request.AdditionalIds.Any(id => id != 0)) return InventoryResult.Reject(19);
        if (request.Type == DamageFontType)
        {
            var font = state.DamageFonts.SingleOrDefault(row => row.SkinId == request.SkinId);
            if (!HasDamageFonts || !skins.DamageFonts.Contains(request.SkinId) ||
                request.SkinId != 1 && (font is null || !font.Active(now))) return InventoryResult.Reject(19);
            return new(true, 0, new(character.Inventory, character.Inventory with
                { Skins = state with { SelectedDamageFont = request.SkinId } }, "damage_font_select",
                new(character, font is { ExpiresAt: > 0 } ? font.ExpiresAt : null, Clock)));
        }
        if (request.Type != Type || request.SkinId != 0 && (!state.Owned.Contains(request.SkinId) ||
            Weapon(request.SkinId) is not { } weapon || !SupportsCharacter(character, weapon)))
            return InventoryResult.Reject(19);
        return new(true, 0, new(character.Inventory, character.Inventory with
            { Skins = state with { Selected = request.SkinId } }, "weapon_skin_select", new(character, null, Clock)));
    }

    public int EffectiveSelection(Character character) => character.Inventory.Skins.Selected is var id && id != 0 &&
        Weapon(id) is { } weapon && SupportsCharacter(character, weapon) ? id : 0;

    public SkinStorageState View(Character character)
    {
        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        return character.Inventory.Skins with
        {
            SelectedDamageFont = EffectiveDamageFont(character),
            SelectedCutscenes = EffectiveCutscenes(character),
            Cutscenes = character.Inventory.Skins.Cutscenes.Where(row => row.Active(now) && skins.Cutscenes.Contains(row.SkinId)).ToImmutableArray(),
            DamageFonts = character.Inventory.Skins.DamageFonts.Where(row => row.Active(now) &&
                skins.DamageFonts.Contains(row.SkinId)).ToImmutableArray()
        };
    }

    public int EffectiveDamageFont(Character character) => skins.DamageFonts.Contains(character.Inventory.Skins.SelectedDamageFont)
        ? character.Inventory.Skins.DamageFontSelection(Clock.GetUtcNow().ToUnixTimeSeconds()) : 1;

    public bool NeedsFontReset(Character character) => HasDamageFonts && character.Inventory.Skins.SelectedDamageFont > 1 &&
        EffectiveDamageFont(character) == 1;

    public ImmutableArray<int> EffectiveCutscenes(Character character)
    {
        var selected = character.Inventory.Skins.CutsceneSelection(Clock.GetUtcNow().ToUnixTimeSeconds())
            .Where(skins.Cutscenes.Contains).ToImmutableArray();
        return selected.IsEmpty ? [30000] : selected;
    }

    public bool NeedsCutsceneReset(Character character) => HasCutscenes && !character.Inventory.Skins.SelectedCutscenes.IsEmpty &&
        !character.Inventory.Skins.SelectedCutscenes.SequenceEqual(EffectiveCutscenes(character));

    public InventoryResult ExpireSelections(Character character)
    {
        var before = character.Inventory;
        var state = before.Skins;
        if (NeedsFontReset(character)) state = state with { SelectedDamageFont = 1 };
        if (NeedsCutsceneReset(character)) state = state with { SelectedCutscenes = EffectiveCutscenes(character) };
        return new(true, 0, new(before, before with { Skins = state }, "skin_storage_expire", new(character, null, Clock)));
    }

    ItemTemplate? Weapon(int id)
    {
        if (!items.Contains(id)) return null;
        try { return items.Get(id) is { Kind: ItemKind.Equipment, EquipmentSlot: 12 } weapon ? weapon : null; }
        catch (InvalidDataException) { return null; }
    }

    // 外观按职业匹配，不继承原武器的佩戴等级和技能门槛。
    internal static bool SupportsCharacter(Character character, ItemTemplate weapon) => weapon is { Kind: ItemKind.Equipment, EquipmentSlot: 12 } &&
        character.Job < 16 && (weapon.Jobs & (1 << character.Job)) != 0;

    bool CanWear(Character character, ItemTemplate weapon, long now) => SupportsCharacter(character, weapon) &&
        InventoryMoveService.WearLevelSatisfied(character, weapon, now, contracts) &&
        InventoryMoveService.RequiredSkillSatisfied(character, weapon);
}
