using System.Collections.Immutable;
using DfoServer.Game.CollectBox;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Characters;

internal sealed record CharacterInvestmentSnapshot(InvestmentSnapshot Equipment, byte Level, byte Job, byte GrowType,
    ImmutableArray<InventoryItem> Worn, CollectBoxState CollectBox)
{
    // 官方风格战斗力（cp-official/v1）的永久属性来源；建设分不读取。
    public PermanentStatBonuses Permanent { get; init; } = PermanentStatBonuses.Empty;
}
internal sealed record CharacterInvestmentPart(string Module, string Source, int Score, bool Partial, bool Capped, string Note)
{
    public EquipmentQualitySeedTier? Quality { get; init; }
    public string RuleVersion { get; init; } = Module == "equipment" ? EquipmentInvestmentScore.Profile : CharacterInvestmentScore.Profile;
    public string Status => Partial ? "Partial" : Note == "appearance_only_no_double_count" ? "AppearanceCovered" :
        Score == 0 ? "KnownZero" : "Complete";
}
internal sealed record CharacterInvestmentResult(string Profile, ushort CharacterId, long CharacterRevision, long ObservedAt,
    string Status, int? Score, bool Partial, bool ScoreCapped, InvestmentScoreResult Equipment,
    ImmutableArray<CharacterInvestmentPart> Parts);

// 自有角色建设规则；按安装状态计建设投入，不推算伤害，不读取客户端面板。
internal static class CharacterInvestmentScore
{
    public const string Profile = "character-investment/v2";
    public const string ChronicleProfile = "character-investment/v2.1";
    public const string DimensionEnergyProfile = "character-investment/v2.2";
    public const string QualityProfile = "character-investment/v2.3";
    public const string CompleteWornProfile = "character-investment/v2.4";
    static readonly string[] Modules = ["equipment", "title", "avatar", "pet", "artifact", "medal", "gem",
        "enchant", "random-title", "emblem", "talisman", "rune", "collectbox", "set", "auxiliary", "cosmetic", "random-option", "chronicle", "emancipation", "quality"];

    public static CharacterInvestmentSnapshot Capture(Character character, long now)
    {
        var basis = EquipmentInvestmentScore.Capture(character, now);
        // Core/特殊详情均是仅init的记录；逐条复制，内部字段只有值类型或不可变字符串。
        var worn = basis.Error is not null ? [] : character.Inventory.Items.Where(item =>
            item.Position.List == InventoryListType.Equipment ||
            item.Position.List == InventoryListType.Talisman && item.Position.Index is >= 210 and <= 215)
            .Select(item => item with { Core = item.Core with { }, Avatar = item.Avatar is { } a ? a with { } : null,
                Creature = item.Creature is { } c ? c with { } : null }).ToImmutableArray();
        var box = character.CollectBox;
        return new(basis, character.Level, character.Job, character.GrowType, worn,
            new(box.SelectedBox, box.Items.IsDefault ? default : [.. box.Items],
                box.Expirations.IsDefault ? default : [.. box.Expirations])) { Permanent = character.PermanentStats };
    }

    // 目录查询均在owner外；仅解析已装宿主及其依赖，不扫描玩家仓库或全PVF。
    public static CharacterInvestmentResult Calculate(CharacterInvestmentSnapshot snapshot, Func<int, ItemTemplate> template,
        Func<int, byte, ItemKind, byte, bool> enchant, Func<uint, bool> randomTitle,
        Func<int, EquipmentSetInvestmentMetadata?> set, Func<int, TalismanDefinition?> talisman,
        Func<int, RuneDefinition?> rune, Func<byte, CollectBoxEntry> collectBox, Func<ItemTemplate, byte, bool> randomOption,
        Func<ChronicleOption, ChronicleInvestmentMetadata>? chronicle = null,
        Func<ItemTemplate, byte, DimensionEnergyInvestmentMetadata>? dimensionEnergy = null, bool includeQualitySeedTier = false,
        bool includeAllWorn = false)
    {
        if (dimensionEnergy is not null && chronicle is null)
            throw new ArgumentException("v2.2须同时提供已支持的气息目录", nameof(chronicle));
        if (includeQualitySeedTier && dimensionEnergy is null)
            throw new ArgumentException("v2.3须同时提供已支持的次元目录", nameof(dimensionEnergy));
        if (includeAllWorn && !includeQualitySeedTier)
            throw new ArgumentException("v2.4须同时开启品质档位", nameof(includeAllWorn));
        string profile = includeAllWorn ? CompleteWornProfile : includeQualitySeedTier ? QualityProfile : dimensionEnergy is not null ? DimensionEnergyProfile : chronicle is null ? Profile : ChronicleProfile;
        var basis = EquipmentInvestmentScore.Calculate(snapshot.Equipment, template, includeAllWorn);
        var parts = ImmutableArray.CreateBuilder<CharacterInvestmentPart>();
        CharacterInvestmentResult Result(bool invalid = false) => new(profile, basis.CharacterId, basis.CharacterRevision,
            basis.ObservedAt, invalid ? "Invalid" : basis.Partial || parts.Any(p => p.Partial) ? "Partial" : "Complete",
            invalid ? null : checked((basis.Score ?? 0) + parts.Where(p => p.Module != "equipment").Sum(p => p.Score)),
            !invalid && (basis.Partial || parts.Any(p => p.Partial)), basis.ScoreCapped || parts.Any(p => p.Capped), basis, parts.ToImmutable());
        if (basis.Status == "Invalid" || snapshot.Worn.IsDefault || snapshot.Worn.Length > 42 ||
            snapshot.Worn.Any(item => item is null || item.Core is null || item.Uid <= 0 || item.Core.ItemId <= 0 || item.ExpireTime < 0 ||
                !(item.Position.List == InventoryListType.Equipment && item.Position.Index is >= 0 and <= 35 ||
                  item.Position.List == InventoryListType.Talisman && item.Position.Index is >= 210 and <= 215)) ||
            snapshot.Worn.Select(i => i.Position).Distinct().Count() != snapshot.Worn.Length ||
            snapshot.Worn.Select(i => i.Uid).Distinct().Count() != snapshot.Worn.Length)
            return Result(true);
        var projected = snapshot.Worn.Where(i => i.Position.List == InventoryListType.Equipment && EquipmentInvestmentScore.Slots.Contains(i.Position.Index))
            .Select(i => new InvestmentItem(i.Uid, i.Position.Index, i.Core.ItemId, i.Core.Upgrade, i.Core.AmplifyType,
                i.Core.AmplifyValue, i.Core.GenuineUpgrade, i.Core.ExpireTime, i.Core.Durability)).OrderBy(i => i.Slot);
        if (!projected.SequenceEqual(snapshot.Equipment.Items.OrderBy(i => i.Slot))) return Result(true);
        bool boxValid = true;
        try { snapshot.CollectBox.Validate(); }
        catch (InvalidDataException) { boxValid = false; }
        void Add(string module, string source, int score, bool partial = false, bool cap = false, string note = "covered") =>
            parts.Add(new(module, source, score, partial, cap, note)
            { RuleVersion = module == "equipment" ? EquipmentInvestmentScore.Profile : profile });
        void Try(string module, string source, Action action)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FileNotFoundException or FormatException or OverflowException)
            { Add(module, source, 0, true, note: "resource_or_state_unavailable"); }
        }
        ItemTemplate Get(int id, ItemKind? kind = null)
        {
            var value = template(id);
            if (value.Id != id || kind is { } k && value.Kind != k) throw new InvalidDataException("评分依赖种类不符");
            return value;
        }
        void Tier(string module, string source, int rarity, int[] scores, int extra = 0, bool cap = false)
        {
            bool known = rarity >= 0 && rarity < scores.Length;
            Add(module, source + $"/tier:{rarity}", (known ? scores[rarity] : scores[0]) + extra, !known, cap,
                known ? "normalized_template_tier" : "unknown_tier_minimum_only");
        }
        Add("equipment", "equipment-investment-95/v1", basis.Score ?? 0, basis.Partial, basis.ScoreCapped);
        if (!boxValid) Add("collectbox", "snapshot", 0, true, note: "invalid_collectbox_state");
        var talismanSkills = new HashSet<int>();
        var valid = new Dictionary<short, (InventoryItem Item, ItemTemplate Template)>();
        foreach (var item in snapshot.Worn.OrderBy(i => i.Position.List).ThenBy(i => i.Position.Index))
        {
            short slot = item.Position.Index;
            if (item.Position.List != InventoryListType.Equipment) continue;
            string source = $"equipment:{slot}/uid:{item.Uid}";
            if (!includeAllWorn && slot is 11 or 33) { Add("cosmetic", source, 0, note: "appearance_only_no_double_count"); continue; }
            string module = slot switch { <= 11 and >= 0 => "avatar", 13 => "title", 26 or 33 => "pet", >= 27 and <= 29 => "artifact",
                32 => "medal", 34 or 35 => "talisman", 24 or 30 => "auxiliary", _ => "equipment" };
            if (item.IsExpired(basis.ObservedAt)) { Add(module, source, 0, note: "expired_excluded"); continue; }
            if (includeAllWorn && slot == 31)
            { Add(module, source, 0, true, note: "slot_without_wear_contract"); continue; }
            Try(module, source, () =>
            {
                item.ValidateDetails(allocating: false);
                var t = Get(item.Core.ItemId);
                ItemKind expected = module switch { "avatar" => ItemKind.Avatar, "pet" => ItemKind.Creature,
                    "artifact" => ItemKind.CreatureEquipment, "medal" => ItemKind.GuildMedal, "talisman" => ItemKind.Talisman, _ => ItemKind.Equipment };
                bool matchesSlot = module == "talisman" ? t.EquipmentSlot == 34 : t.EquipmentSlot == slot;
                if (includeAllWorn)
                    matchesSlot |= slot == 11 && t.EquipmentSlot is 9 or 11 || slot == 33 && t.EquipmentSlot == 26 || slot == 24 && t.EquipmentSlot == 12;
                if (t.Kind != expected || item.Core.Kind != expected || !matchesSlot ||
                    module == "equipment" && !EquipmentInvestmentScore.Slots.Contains(slot))
                    throw new InvalidDataException("穿戴宿主槽位不符");
                switch (module)
                {
                    case "title":
                    case "auxiliary":
                        if (includeAllWorn)
                        {
                            var row = new InvestmentItem(item.Uid, slot, t.Id, item.Core.Upgrade, item.Core.AmplifyType,
                                item.Core.AmplifyValue, item.Core.GenuineUpgrade, item.ExpireTime, item.Core.Durability);
                            var score = EquipmentInvestmentScore.Calculate(new(basis.CharacterId, basis.CharacterRevision, basis.ObservedAt, [row]), template, true);
                            Add(module, source, score.Score ?? 0, score.Partial, score.ScoreCapped, "extended_equipment_base_and_crafting");
                        }
                        else Tier(module, source, t.Rarity, [300, 350, 425, 525, 700, 475, 625]);
                        break;
                    case "avatar":
                        if (item.Avatar is null) throw new InvalidDataException("时装详情缺失");
                        Tier(module, source, t.Grade, [50, 100, 200, 350]); break;
                    case "pet":
                        if (item.Creature is null || item.Creature.Level == 0) throw new InvalidDataException("宠物成长缺失");
                        Tier(module, source + $"/level:{item.Creature.Level}", t.Rarity, [500, 600, 750, 900, 1300, 1000, 1150],
                            10 * Math.Min((int)item.Creature.Level, 50), item.Creature.Level > 50); break;
                    case "artifact": Tier(module, source, t.Rarity, [100, 125, 162, 212, 300, 187, 262]); break;
                    case "medal": Tier(module, source + $"/upgrade:{item.Core.Upgrade}", t.Rarity, [200, 250, 325, 425, 600, 375, 525],
                        10 * Math.Min((int)item.Core.Upgrade, 15), item.Core.Upgrade > 15); break;
                    case "talisman":
                        var td = talisman(t.Id);
                        if (td?.AllowsUse(snapshot.Level, snapshot.Job, snapshot.GrowType) != true ||
                            td.SkillIndex is not { } skill || !talismanSkills.Add(skill))
                            throw new InvalidDataException("护石职业、技能重复或资源不符");
                        Add(module, source, 200); break;
                }
                valid.Add(slot, (item, t));
            });
        }
        if (includeAllWorn)
        {
            // 基础层也须服从完整宿主校验，详情非法不能留下独立的基础打造分。
            var rows = basis.Slots.Select(row => row.Uid is not null && !valid.ContainsKey(row.Slot) &&
                !row.Notes.Contains("expired_excluded") ? row with
                { Base = 0, Enhancement = 0, Amplification = 0, Score = 0, Partial = true,
                    Notes = [.. row.Notes, "invalid_worn_host"] } : row).ToImmutableArray();
            basis = basis with { Slots = rows, Score = rows.Sum(row => row.Score), Partial = rows.Any(row => row.Partial),
                Status = rows.Any(row => row.Partial) ? "Partial" : "Complete" };
            parts.RemoveAt(0);
            parts.Insert(0, new("equipment", EquipmentInvestmentScore.Profile, basis.Score ?? 0, basis.Partial, basis.ScoreCapped, "covered"));
        }
        foreach (var (slot, pair) in valid.OrderBy(p => p.Key))
        {
            var item = pair.Item;
            var core = item.Core;
            string source = $"equipment:{slot}/uid:{item.Uid}";
            // 非普通装备的强化也是已保存投入；勋章原有每级10分不重复奖励。
            if (includeAllWorn && core.Kind != ItemKind.Equipment && core.Kind != ItemKind.GuildMedal && core.Upgrade != 0)
            {
                int upgrade = Math.Min((int)core.Upgrade, 15);
                Add("enhancement", source, 20 * upgrade + 5 * Math.Max(upgrade - 10, 0) * Math.Max(upgrade - 10, 0),
                    cap: core.Upgrade > 15, note: "installed_non_equipment_upgrade");
            }
            if (includeAllWorn && core.Kind != ItemKind.Equipment && (core.AmplifyType != 0 || core.AmplifyValue != 0))
            {
                if (core.AmplifyType is >= 1 and <= 4 && core.AmplifyValue > 0)
                    Add("enhancement", source + "/amplification", 10 * Math.Min((int)core.Upgrade, 15), cap: core.Upgrade > 15);
                else if (core.AmplifyType is not (>= 0x81 and <= 0x84) || core.AmplifyValue == 0)
                    Add("enhancement", source, 0, true, note: "invalid_amplification_excluded");
            }
            if (includeQualitySeedTier && core.Kind == ItemKind.Equipment && (includeAllWorn || EquipmentInvestmentScore.Slots.Contains(slot)))
            {
                var quality = EquipmentQualitySeedResolver.Resolve(core.Value);
                // 不重复乘武器权重；零属性模板也按本服seed规则计，不推断原生UI适用性。
                parts.Add(new("quality", source, 25 * quality.Tier, false, false, includeAllWorn ? "worn_equipment_seed_tier_investment" : "standard_equipment_seed_tier_investment")
                { RuleVersion = profile, Quality = quality });
            }
            else if (core.Kind == ItemKind.Equipment)
                Add("quality", source + $"/seed-or-code:{core.Value}", 0, note: "covered_by_equipment_base_seed_not_percentage");
            if (chronicle is null)
            {
                // 保留v2旧合同；只有显式提供气息目录的v2.1才解释这些字段。
                if (core.Chronicle0 != default || core.Chronicle1 != default)
                    Add("chronicle", source, 0, true, note: "unmapped_persisted_chronicle_state");
            }
            else
            {
                var identities = new HashSet<(byte Type, byte Number)>();
                ChronicleOption[] installed = [core.Chronicle0, core.Chronicle1];
                for (int index = 0; index < installed.Length; index++)
                {
                    var option = installed[index];
                    if (option == default) continue;
                    string location = source + $"/chronicle:{index}/id:{option.Id}/job:{option.Job}/grow:{option.GrowType}/slot:{option.EquipmentType}/number:{option.Number}";
                    Try("chronicle", location, () =>
                    {
                        if (core.Kind != ItemKind.Equipment || option.Id <= 0 ||
                            option.EquipmentType != (includeAllWorn ? pair.Template.EquipmentSlot : slot))
                            throw new InvalidDataException("气息已提交宿主不符");
                        var definition = chronicle(option);
                        if (!definition.Valid)
                        {
                            Add("chronicle", location, 0, true, note: definition.Reason);
                            return;
                        }
                        if (definition.Type > 2) throw new InvalidDataException("气息目录类型不符");
                        if (!identities.Add((definition.Type, option.Number)))
                        {
                            Add("chronicle", location, 0, true, note: "duplicate_type_number_excluded");
                            return;
                        }
                        Add("chronicle", location + $"/type:{definition.Type}", 40, note: "validated_installed_option");
                    });
                }
            }
            if (core.EmancipateLevel != 0)
            {
                if (dimensionEnergy is null)
                    Add("emancipation", source, 0, true, note: "unmapped_persisted_emancipate_state");
                else Try("emancipation", source, () =>
                {
                    if (core.Kind != ItemKind.Equipment) throw new InvalidDataException("次元档宿主不是普通装备");
                    var support = dimensionEnergy(pair.Template, core.EmancipateLevel);
                    if (!support.Supported)
                    {
                        Add("emancipation", source, 0, true, note: support.Reason);
                        return;
                    }
                    Add("emancipation", source + $"/stages:{core.EmancipateLevel}/effective-level:{support.EffectiveLevel}/profiles:{string.Join(',', support.SourceIds)}",
                        20 * core.EmancipateLevel, note: "current_resource_support_envelope_not_consumption_history");
                });
            }
            RandomOption[] options = [core.Random0, core.Random1, core.Random2];
            bool gap = false;
            var randomTypes = new HashSet<byte>();
            for (int index = 0; index < options.Length; index++)
            {
                var option = options[index];
                if (option.Type == 0)
                {
                    gap = true;
                    if (option.Value1 != 0 || option.Value2 != 0) Add("random-option", source + $"/option:{index}", 0, true, note: "invalid_empty_option");
                    continue;
                }
                string location = source + $"/option:{index}/type:{option.Type}";
                Try("random-option", location, () =>
                {
                    if (gap || !randomTypes.Add(option.Type) || !randomOption(pair.Template, option.Type)) throw new InvalidDataException("魔封属性来源不符");
                    Add("random-option", location, 40);
                });
            }
            if (core.EnchantCardId != 0 || core.EnchantUpgrade != 0)
                Try("enchant", source, () =>
                {
                    if (core.EnchantCardId <= 0 || !Get(core.EnchantCardId).IsMonsterCard || !enchant(core.EnchantCardId, core.EnchantUpgrade, pair.Template.Kind, pair.Template.EquipmentSlot))
                        throw new InvalidDataException("附魔卡片索引不符");
                    Add("enchant", source + $"/card:{core.EnchantCardId}/index:{core.EnchantUpgrade}", 100);
                });
            if (core.Extension97 != 0)
                Try("random-title", source, () =>
                {
                    if (slot != 13 || !randomTitle(core.Extension97)) throw new InvalidDataException("随机称号附魔不符");
                    Add("random-title", source + $"/pool:{core.Extension97 & 65535}/entry:{core.Extension97 >> 16}", 50);
                });
            void Emblem(int id, ushort type, int socket)
            {
                string location = source + $"/socket:{socket}";
                if (type is not (0 or 1 or 2 or 4 or 8 or 16 or 239))
                { Add("emblem", location, 0, true, note: "unknown_socket_type"); return; }
                if (id <= 0)
                {
                    if (id < -1 || type == 0 && id != 0 || type != 0 && id == 0)
                        Add("emblem", location, 0, true, note: "invalid_socket_state");
                    return;
                }
                Try("emblem", location, () =>
                {
                    var e = Get(id, ItemKind.Emblem);
                    if (type == 0 || (e.EmblemTargetSockets & type) == 0) throw new InvalidDataException("徽章孔色不符");
                    Add("emblem", location + $"/item:{id}", e.Rarity == 6 ? 100 : 40,
                        e.Rarity is < 0 or > 6, note: e.Rarity == 6 ? "platinum" : "installed_emblem");
                });
            }
            if (core.Kind != ItemKind.Equipment && (core.EmblemSocketCount != 0 || core.Emblem1 != 0 || core.Emblem2 != 0))
                Add("emblem", source, 0, true, note: "unexpected_equipment_socket_state");
            if (item.Avatar is { } avatar)
            {
                AvatarSocket[] sockets = [avatar.Socket0, avatar.Socket1, avatar.Socket2, avatar.Socket3, avatar.Socket4];
                for (int i = 0; i < sockets.Length; i++) Emblem(sockets[i].EmblemId, sockets[i].Type, i);
            }
            else if (core.Kind == ItemKind.Equipment)
            {
                int limit = slot is 22 or 23 ? 1 : 2;
                ushort type = slot switch { 14 or 16 => 4, 15 or 19 => 2, 18 or 21 => 1, 17 or 20 => 8, _ => 16 };
                if (core.EmblemSocketCount > limit || core.EmblemSocketCount == 0 && (core.Emblem1 != 0 || core.Emblem2 != 0) ||
                    core.EmblemSocketCount == 1 && core.Emblem2 != 0)
                    Add("emblem", source, 0, true, note: "invalid_socket_count");
                else
                {
                    if (core.EmblemSocketCount > 0) Emblem(core.Emblem1, type, 0);
                    if (core.EmblemSocketCount > 1) Emblem(core.Emblem2, type, 1);
                }
            }
            if (core.GuardianGemKeys != 0)
            {
                var gems = new List<(int Socket, int Id, ItemTemplate Template, string Source)>();
                for (int i = 0; i < 4; i++)
                {
                    int id = InventoryGuardianGemService.ItemId(core.GuardianGemKeys, i);
                    if (id == 0) continue;
                    string location = source + $"/gem:{i}/item:{id}";
                    int socket = i;
                    Try("gem", location, () =>
                    {
                        if (slot != 32) throw new InvalidDataException("守护珠宿主不符");
                        var gem = Get(id, ItemKind.GuardianGem);
                        if (gem.GuardianGemEffects.Count == 0) throw new InvalidDataException("守护珠属性缺失");
                        gems.Add((socket, id, gem, location));
                    });
                }
                int[] gemScores = [50, 60, 75, 95, 130, 85, 115];
                var effects = new HashSet<string>(StringComparer.Ordinal);
                // 非法重复属性按建设奖励由高到低取一份；相同分按物品ID稳定排序，不随孔序变化。
                foreach (var gem in gems.OrderByDescending(g => g.Template.Rarity is >= 0 and < 7 ? gemScores[g.Template.Rarity] : gemScores[0])
                    .ThenBy(g => g.Id).ThenBy(g => g.Socket))
                {
                    if (gem.Template.GuardianGemEffects.Overlaps(effects))
                    { Add("gem", gem.Source, 0, true, note: "conflicting_effect_excluded"); continue; }
                    effects.UnionWith(gem.Template.GuardianGemEffects);
                    Tier("gem", gem.Source, gem.Template.Rarity, gemScores);
                }
            }
        }
        foreach (var item in snapshot.Worn.Where(i => i.Position.List == InventoryListType.Talisman).OrderBy(i => i.Position.Index))
        {
            string source = $"rune:{item.Position.Index}/uid:{item.Uid}";
            if (item.IsExpired(basis.ObservedAt)) { Add("rune", source, 0, note: "expired_excluded"); continue; }
            var parentSlot = (short)(34 + (item.Position.Index - 210) / 3);
            var rawParent = snapshot.Worn.SingleOrDefault(i => i.Position == new InventorySlot(InventoryListType.Equipment, parentSlot));
            if (rawParent?.IsExpired(basis.ObservedAt) == true)
            { Add("rune", source, 0, note: "expired_parent_excluded"); continue; }
            Try("rune", source, () =>
            {
                int index = item.Position.Index - 210;
                if (index is < 0 or > 5 || !valid.TryGetValue((short)(34 + index / 3), out var parent) ||
                    item.Core.Kind != ItemKind.Rune || item.Core.Count != 1)
                    throw new InvalidDataException("符文父槽或实例不符");
                var r = rune(item.Core.ItemId);
                if (r?.AllowsUse(snapshot.Level, snapshot.Job, snapshot.GrowType) != true ||
                    talisman(parent.Template.Id)?.AllowsUse(snapshot.Level, snapshot.Job, snapshot.GrowType) != true ||
                    TalismanSocketColors.Read(parent.Item.Core)[index % 3] != r.SocketColor)
                    throw new InvalidDataException("符文资格或孔色不符");
                Add("rune", source, 50);
            });
        }
        var groups = new Dictionary<int, (HashSet<short> Slots, ImmutableArray<int> Thresholds)>();
        foreach (var (slot, pair) in valid.OrderBy(p => p.Key))
        {
            Try("set", $"equipment:{slot}", () =>
            {
                var s = set(pair.Template.Id);
                if (s is null) return;
                if (s.SetId <= 0 || s.Thresholds.IsDefaultOrEmpty || s.Thresholds.Any(k => k < 2) ||
                    s.Thresholds.Distinct().Count() != s.Thresholds.Length) throw new InvalidDataException("套装阈值无效");
                if (!groups.TryGetValue(s.SetId, out var g)) groups.Add(s.SetId, g = ([], s.Thresholds));
                if (!g.Thresholds.SequenceEqual(s.Thresholds)) throw new InvalidDataException("套装阈值不一致");
                g.Slots.Add(slot);
            });
        }
        foreach (var (id, g) in groups.OrderBy(p => p.Key))
        {
            int k = g.Thresholds.Where(k => k >= 2 && k <= g.Slots.Count).DefaultIfEmpty(0).Max();
            Add("set", $"set:{id}/pieces:{g.Slots.Count}/threshold:{k}", checked(50 * k * (k - 1)));
        }
        if (boxValid && snapshot.CollectBox.SelectedBox != 0)
            Try("collectbox", $"box:{snapshot.CollectBox.SelectedBox}", () =>
            {
                byte index = snapshot.CollectBox.SelectedBox;
                var b = collectBox(index);
                long? expiry = b.ResolveExpiration(snapshot.CollectBox.Expiration(index));
                if (expiry <= basis.ObservedAt) { Add("collectbox", $"box:{index}", 0, note: "expired_excluded"); return; }
                foreach (var entry in snapshot.CollectBox.Items.Where(i => i.Box == index).OrderBy(i => i.PhysicalSlot))
                {
                    bool known = b.Index == index && b.TrySlot(entry.ItemId, out var expected) && expected.PhysicalSlot == entry.PhysicalSlot;
                    Add("collectbox", $"box:{index}/slot:{entry.PhysicalSlot}/item:{entry.ItemId}", known ? 50 : 0, !known,
                        note: known ? "valid_selected_box_slot" : "invalid_box_slot");
                }
            });
        foreach (string module in Modules)
            if (!parts.Any(p => p.Module == module)) Add(module, "empty", 0, note: "known_empty");
        return Result();
    }
}
