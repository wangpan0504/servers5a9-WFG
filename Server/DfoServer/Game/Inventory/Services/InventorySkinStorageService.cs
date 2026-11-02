using System.Collections.Frozen;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventorySkinStorageService(ItemCatalog items, SkinStorageCatalog skins,
    GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, PremiumContractCatalog contracts, TimeProvider? time = null)
{
    public const byte Type = 4;
    public const byte PartyFrameType = 0;
    public const byte CutsceneType = 1;
    public const byte DamageFontType = 2;
    public const byte EmoticonType = 3;
    public const int EmoticonSlots = 4;
    public const uint AddAction = 199;
    public TimeProvider Clock { get; } = time ?? TimeProvider.System;
    public bool Recognizes(int id) => skins.IsToken(id);
    public bool HasDamageFonts => skins.HasDamageFonts;
    public bool HasCutscenes => skins.HasCutscenes;
    public bool HasPartyFrames => skins.HasPartyFrames;

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
        if (source.ExpireTime != 0 || source.Core.DisjointForbidden || original.DisjointForbidden) return InventoryResult.Reject(93);
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

    public InventoryResult Add(Character character, InventorySlot position, bool town, bool seriaRoom = false)
    {
        var now = Clock.GetUtcNow();
        var before = character.Inventory;
        var token = before.Find(position);
        if (position.List != InventoryListType.Main || token is null ||
            token.Core.Kind != ItemKind.Consumable || token.Core.EquipmentLockId != 0 || token.IsExpired(now.ToUnixTimeSeconds()) ||
            skins.Token(token.Core.ItemId) is not { } definition ||
            !items.Get(token.Core.ItemId).AllowsUse(character.Level, character.Job) ||
            !definition.Places.IsEmpty && !definition.Places.Contains("[all]") &&
                !definition.Places.Contains(town ? "[village]" : "[dungeon]") &&
                !(town && seriaRoom && definition.Places.Contains("[seria room]")))
            return InventoryResult.Reject(3) with { RejectReason = "invalid_skin_token_or_context" };
        if (token.Core is { ItemId: 490013318, Strongway2019FrameOrigin: { Version: 1 } } &&
            definition is { Type: PartyFrameType, AccountWide: false, SkinId: 420000004 } &&
            definition.SkinIds is [420000004])
            definition = definition with { Expiration = new() };
        // 账号归属的券写入账号皮肤仓，其余写入角色皮肤仓；选中项始终留在角色身上。
        var state = definition.AccountWide ? before.AccountSkins : before.Skins;
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
        // 同一时钟刻度连续跨归属入库也保留实际顺序；不因时钟回拨把新记录排到后面。
        long latest = before.Skins.Recent.Concat(before.AccountSkins.Recent).Select(row => row.AcquiredAt).DefaultIfEmpty(0).Max();
        state = state.Acquired(Keys(token), Math.Max(now.UtcTicks, latest + 1));
        var after = definition.AccountWide
            ? removed.Plan!.After with { AccountSkins = state }
            : removed.Plan!.After with { Skins = state };
        return new(true, 0, new(before, after, "skin_storage_add", new(character, deadline, Clock)));
    }

    internal static bool TryAddTimed(SkinStorageState before, SkinStorageToken definition, DateTimeOffset now, out SkinStorageState after)
    {
        after = before;
        if (definition.Type is not (CutsceneType or DamageFontType or PartyFrameType or EmoticonType) ||
            definition.SkinIds.IsDefaultOrEmpty || definition.SkinIds.Length > byte.MaxValue ||
            definition.SkinIds.Distinct().Count() != definition.SkinIds.Length) return false;
        int defaultId = DefaultSkinId(definition.Type);
        var owned = OwnedOf(before, definition.Type).ToDictionary(row => row.SkinId);
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
        after = WithOwned(before, definition.Type, updated);
        return true;
    }

    static int DefaultSkinId(byte type) => type switch
    {
        CutsceneType => 30000, DamageFontType => 1, PartyFrameType => 20000, _ => 0
    };

    static ImmutableArray<SkinOwnership> OwnedOf(SkinStorageState state, byte type) => type switch
    {
        CutsceneType => state.Cutscenes, DamageFontType => state.DamageFonts,
        PartyFrameType => state.PartyFrames, EmoticonType => state.Emoticons, _ => []
    };

    static SkinStorageState WithOwned(SkinStorageState state, byte type, ImmutableArray<SkinOwnership> owned) => type switch
    {
        CutsceneType => state with { Cutscenes = owned }, DamageFontType => state with { DamageFonts = owned },
        PartyFrameType => state with { PartyFrames = owned }, EmoticonType => state with { Emoticons = owned }, _ => state
    };

    public InventoryResult Select(Character character, SelectSkinRequest request)
    {
        var state = character.Inventory.Skins;
        var merged = Merged(character);
        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        if (request.AdditionalIds.IsDefault || request.AdditionalIds.Length > 9) return InventoryResult.Reject(19);
        if (request.Type == CutsceneType)
        {
            var ids = request.Ids.Where(id => id != 0).ToImmutableArray();
            if (!HasCutscenes || request.AdditionalIds.IsDefault || request.Ids.Any(id => id < 0) ||
                ids.Length is 0 or > 10 || ids.Distinct().Count() != ids.Length ||
                ids.Any(id => !skins.Cutscenes.Contains(id) || id != 30000 && !merged.Cutscenes.Any(row => row.SkinId == id && row.Active(now))))
                return InventoryResult.Reject(19);
            long? deadline = merged.Cutscenes.Where(row => ids.Contains(row.SkinId) && row.ExpiresAt > 0)
                .Select(row => (long?)row.ExpiresAt).Min();
            return new(true, 0, new(character.Inventory, character.Inventory with
                { Skins = state with { SelectedCutscenes = ids } }, "skill_cutscene_select", new(character, deadline, Clock)));
        }
        // 即时表情在下方按四个栏位校验，不能提前按单编号皮肤拒绝第二至第四栏。
        if (request.Type != EmoticonType && request.AdditionalIds.Any(id => id != 0)) return InventoryResult.Reject(19);
        if (request.Type == PartyFrameType)
        {
            var frame = merged.PartyFrames.SingleOrDefault(row => row.SkinId == request.SkinId);
            if (!HasPartyFrames || !skins.PartyFrames.Contains(request.SkinId) ||
                request.SkinId != 20000 && (frame is null || !frame.Active(now))) return InventoryResult.Reject(19);
            return new(true, 0, new(character.Inventory, character.Inventory with
                { Skins = state with { SelectedPartyFrame = request.SkinId } }, "party_frame_select",
                new(character, frame is { ExpiresAt: > 0 } ? frame.ExpiresAt : null, Clock)));
        }
        if (request.Type == DamageFontType)
        {
            var font = merged.DamageFonts.SingleOrDefault(row => row.SkinId == request.SkinId);
            if (!HasDamageFonts || !skins.DamageFonts.Contains(request.SkinId) ||
                request.SkinId != 1 && (font is null || !font.Active(now))) return InventoryResult.Reject(19);
            return new(true, 0, new(character.Inventory, character.Inventory with
                { Skins = state with { SelectedDamageFont = request.SkinId } }, "damage_font_select",
                new(character, font is { ExpiresAt: > 0 } ? font.ExpiresAt : null, Clock)));
        }
        if (request.Type == EmoticonType)
        {
            // 00862C80/01500030 固定发送4个栏位，空栏位补0；客户端按位置应用。
            // 超出的栏位直接拒绝，避免回执原样复述未被保存的编号。
            if (request.Ids.Skip(EmoticonSlots).Any(id => id != 0)) return InventoryResult.Reject(19);
            var slots = request.Ids.Take(EmoticonSlots).Concat(Enumerable.Repeat(0, EmoticonSlots))
                .Take(EmoticonSlots).ToImmutableArray();
            var chosen = slots.Where(id => id != 0).ToImmutableArray();
            if (chosen.Distinct().Count() != chosen.Length || chosen.Any(id => !skins.Emoticons.Contains(id) ||
                !merged.Emoticons.Any(row => row.SkinId == id && row.Active(now)))) return InventoryResult.Reject(19);
            long? deadline = merged.Emoticons.Where(row => chosen.Contains(row.SkinId) && row.ExpiresAt > 0)
                .Select(row => (long?)row.ExpiresAt).Min();
            return new(true, 0, new(character.Inventory, character.Inventory with
                { Skins = state with { SelectedEmoticons = slots } }, "instant_emoticon_select",
                new(character, deadline, Clock)));
        }
        if (request.Type != Type || request.SkinId != 0 && (!state.Owned.Contains(request.SkinId) ||
            Weapon(request.SkinId) is not { } weapon || !SupportsCharacter(character, weapon)))
            return InventoryResult.Reject(19);
        return new(true, 0, new(character.Inventory, character.Inventory with
            { Skins = state with { Selected = request.SkinId } }, "weapon_skin_select", new(character, null, Clock)));
    }

    public int EffectiveSelection(Character character) => character.Inventory.Skins.Selected is var id && id != 0 &&
        Weapon(id) is { } weapon && SupportsCharacter(character, weapon) ? id : 0;

    // 角色皮肤仓与账号皮肤仓的并集：同一编号取期限较晚的一方，选中项始终由角色持有。
    internal static SkinStorageState Merged(InventorySnapshot inventory) => inventory.Skins with
    {
        Owned = inventory.Skins.Owned.Concat(inventory.AccountSkins.Owned.Where(id => !inventory.Skins.Owned.Contains(id))).ToImmutableArray(),
        DamageFonts = UnionOwned(inventory.Skins.DamageFonts, inventory.AccountSkins.DamageFonts),
        Cutscenes = UnionOwned(inventory.Skins.Cutscenes, inventory.AccountSkins.Cutscenes),
        PartyFrames = UnionOwned(inventory.Skins.PartyFrames, inventory.AccountSkins.PartyFrames),
        Emoticons = UnionOwned(inventory.Skins.Emoticons, inventory.AccountSkins.Emoticons)
    };

    static SkinStorageState Merged(Character character) => Merged(character.Inventory);

    static ImmutableArray<SkinOwnership> UnionOwned(ImmutableArray<SkinOwnership> character, ImmutableArray<SkinOwnership> account)
    {
        var rows = character.ToDictionary(row => row.SkinId);
        foreach (var row in account)
            if (!rows.TryGetValue(row.SkinId, out var local) || Rank(row) > Rank(local)) rows[row.SkinId] = row;
        return rows.Values.OrderBy(row => row.SkinId).ToImmutableArray();
    }

    static long Rank(SkinOwnership row) => row.ExpiresAt == 0 ? long.MaxValue : row.ExpiresAt;

    public SkinStorageState View(Character character)
    {
        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        var state = character.Inventory.Skins;
        return state with
        {
            SelectedDamageFont = EffectiveDamageFont(character),
            SelectedCutscenes = EffectiveCutscenes(character),
            SelectedPartyFrame = EffectivePartyFrame(character),
            SelectedEmoticons = EffectiveEmoticons(character),
            Cutscenes = Active(state.Cutscenes, skins.Cutscenes, now),
            DamageFonts = Active(state.DamageFonts, skins.DamageFonts, now),
            PartyFrames = Active(state.PartyFrames, skins.PartyFrames, now),
            Emoticons = Active(state.Emoticons, skins.Emoticons, now)
        };
    }

    // 账号皮肤仓的条目在1557里单独成表，客户端合并显示但分属两份列表。
    public SkinStorageState AccountView(Character character)
    {
        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        var state = character.Inventory.AccountSkins;
        return state with
        {
            Cutscenes = Active(state.Cutscenes, skins.Cutscenes, now),
            DamageFonts = Active(state.DamageFonts, skins.DamageFonts, now),
            PartyFrames = Active(state.PartyFrames, skins.PartyFrames, now),
            Emoticons = Active(state.Emoticons, skins.Emoticons, now)
        };
    }

    public ImmutableArray<SkinStorageKey> Recent(Character character)
    {
        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        var state = character.Inventory.Skins;
        var account = character.Inventory.AccountSkins;
        var owned = new HashSet<SkinStorageKey>();
        // 券可保存制作候选表以外的真实武器，不能用该候选表收窄既有所有权。
        foreach (int id in state.Owned)
            if (items.Contains(id)) owned.Add(new(Type, id));
        Add(CutsceneType, skins.Cutscenes, state.Cutscenes, account.Cutscenes);
        Add(DamageFontType, skins.DamageFonts, state.DamageFonts, account.DamageFonts);
        Add(PartyFrameType, skins.PartyFrames, state.PartyFrames, account.PartyFrames);
        Add(EmoticonType, skins.Emoticons, state.Emoticons, account.Emoticons);
        // 有真实时间的记录优先；旧存档只用有效所有权补齐，不虚构获得时间或写库。
        return state.Recent.Concat(account.Recent).Where(row => owned.Contains(row.Key))
            .OrderByDescending(row => row.AcquiredAt).Select(row => row.Key)
            .Concat(owned.OrderBy(key => key.Type).ThenBy(key => key.SkinId))
            .Distinct().Take(SkinStorageState.RecentLimit).ToImmutableArray();

        void Add(byte type, FrozenSet<int> catalog, ImmutableArray<SkinOwnership> local, ImmutableArray<SkinOwnership> shared)
        {
            foreach (var row in local.Concat(shared))
                if (row.Active(now) && row.SkinId != DefaultSkinId(type) && catalog.Contains(row.SkinId))
                    owned.Add(new(type, row.SkinId));
        }
    }

    static ImmutableArray<SkinOwnership> Active(ImmutableArray<SkinOwnership> owned, FrozenSet<int> catalog, long now) =>
        owned.Where(row => row.Active(now) && catalog.Contains(row.SkinId)).ToImmutableArray();

    public int EffectiveDamageFont(Character character) => skins.DamageFonts.Contains(character.Inventory.Skins.SelectedDamageFont)
        ? Merged(character).DamageFontSelection(Clock.GetUtcNow().ToUnixTimeSeconds()) : 1;

    public bool NeedsFontReset(Character character) => HasDamageFonts && character.Inventory.Skins.SelectedDamageFont > 1 &&
        EffectiveDamageFont(character) == 1;

    public ImmutableArray<int> EffectiveCutscenes(Character character)
    {
        var selected = Merged(character).CutsceneSelection(Clock.GetUtcNow().ToUnixTimeSeconds())
            .Where(skins.Cutscenes.Contains).ToImmutableArray();
        return selected.IsEmpty ? [30000] : selected;
    }

    public bool NeedsCutsceneReset(Character character) => HasCutscenes && !character.Inventory.Skins.SelectedCutscenes.IsEmpty &&
        !character.Inventory.Skins.SelectedCutscenes.SequenceEqual(EffectiveCutscenes(character));

    // 组队头像框原生默认20000（PartyFrame/default.skn），未选择或过期时回落到它。
    public int EffectivePartyFrame(Character character) => skins.PartyFrames.Contains(character.Inventory.Skins.SelectedPartyFrame)
        ? Merged(character).PartyFrameSelection(Clock.GetUtcNow().ToUnixTimeSeconds()) : 20000;

    public bool NeedsPartyFrameReset(Character character) => HasPartyFrames && character.Inventory.Skins.SelectedPartyFrame != 0 &&
        character.Inventory.Skins.SelectedPartyFrame != EffectivePartyFrame(character);

    public ImmutableArray<int> EffectiveEmoticons(Character character) =>
        Merged(character).EmoticonSelection(Clock.GetUtcNow().ToUnixTimeSeconds())
            .Where(id => id == 0 || skins.Emoticons.Contains(id)).ToImmutableArray();

    public bool CanSendEmoticon(Character character, int skinId) => skinId > 0 && skins.Emoticons.Contains(skinId) &&
        character.Inventory.Skins.Emoticons.Concat(character.Inventory.AccountSkins.Emoticons)
            .Any(row => row.SkinId == skinId && row.Active(Clock.GetUtcNow().ToUnixTimeSeconds()));

    public bool NeedsEmoticonReset(Character character) => !character.Inventory.Skins.SelectedEmoticons.IsEmpty &&
        !character.Inventory.Skins.SelectedEmoticons.SequenceEqual(EffectiveEmoticons(character));

    // 即时表情没有原生默认皮肤：角色和账号都没有条目时不发空列表，避免覆盖客户端本地保存。
    public bool NeedsEmoticonInitialize(Character character) => !character.Inventory.Skins.Emoticons.IsEmpty ||
        !character.Inventory.Skins.SelectedEmoticons.IsEmpty || !character.Inventory.AccountSkins.Emoticons.IsEmpty;

    public InventoryResult ExpireSelections(Character character)
    {
        var before = character.Inventory;
        var state = before.Skins;
        if (NeedsFontReset(character)) state = state with { SelectedDamageFont = 1 };
        if (NeedsCutsceneReset(character)) state = state with { SelectedCutscenes = EffectiveCutscenes(character) };
        if (NeedsPartyFrameReset(character)) state = state with { SelectedPartyFrame = EffectivePartyFrame(character) };
        if (NeedsEmoticonReset(character)) state = state with { SelectedEmoticons = EffectiveEmoticons(character) };
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
