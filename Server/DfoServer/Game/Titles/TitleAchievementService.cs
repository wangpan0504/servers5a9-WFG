using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Currency;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Titles;

internal sealed class TitleAchievementService(TitleCatalog titles, QuestCatalog quests, ItemCatalog items,
    GoldLimitCatalog goldLimits, EpicPieceCatalog pieces)
{
    public TitleCatalog Catalog => titles;

    public ImmutableArray<int> UnlockedEmotions(Character character) =>
        character.TitleSections.Where(titles.Sections.ContainsKey)
            .Select(key => titles.Sections[key].Emotion)
            .Where(emotion => emotion > 0)
            .Distinct()
            .Order()
            .ToImmutableArray();

    // 成就券单独授权general全组；不复用GM的逐项降级，任一奖励无效即整批拒绝。
    public TitleAchievementResult CompleteGeneral(Character character, IReadOnlyCollection<int> questIds, DateTimeOffset now)
    {
        var general = titles.Slots.Where(row => row.Key.Category == 0 && row.Value.Kind != -1 && row.Value.QuestId > 0)
            .Select(row => row.Value.QuestId).ToHashSet();
        if (questIds.Any(id => !general.Contains(id) || !titles.Quests.ContainsKey(id))) return TitleAchievementResult.Reject(0);
        var updates = questIds.Distinct().Where(id => character.TitleProgress.GetValueOrDefault(id)?.Remaining.Complete != true)
            .Select(id => (id, default(TitleCounters))).ToArray();
        return PrepareRewards(character, updates, 0, now, category: 0);
    }

    // 绝望称号只由原实例真实通关事实驱动；不接受客户端累计数或GM完成入口代替楼层证明。
    public TitleAchievementResult DespairClear(Character character, Dungeons.DungeonClearFact fact,
        GameWorld.Dungeons.TowerOfDespairFloor floor, DateTimeOffset now)
    {
        if (fact.Identity.CharacterId != character.Id || fact.DungeonId != floor.DungeonId ||
            floor.Layer is < 1 or > 100 || floor.DungeonId != 11007 + floor.Layer || fact.ConfirmedDeathEvents.Count == 0)
            return TitleAchievementResult.Reject(floor.TitleQuestId);
        if (floor.TitleQuestId == -1) return new(true, 0, 0, default);
        if (!titles.Quests.TryGetValue(floor.TitleQuestId, out var definition) ||
            definition.Quest.Grade != "[title]" || definition.Quest.Condition != "[condition under clear]" ||
            definition.Quest.SubType != 6 || !definition.Quest.IntData.SequenceEqual(new[] { floor.DungeonId, -1 }) ||
            !titles.QuestCategories.GetValueOrDefault(floor.TitleQuestId, []).Contains(3))
            return TitleAchievementResult.Reject(floor.TitleQuestId);
        // 已领取的称号（含旧完成任务存档）不再受接取资格检查，也不重复发奖。
        if (character.TitleProgress.GetValueOrDefault(floor.TitleQuestId)?.Remaining.Complete == true ||
            QuestEligibility.Completed(character).Contains(floor.TitleQuestId))
            return new(true, 0, floor.TitleQuestId, default);
        if (!QuestEligibility.Evaluate(character, definition.Quest, quests, allowAboveMaximum: true).Eligible)
            return TitleAchievementResult.Reject(floor.TitleQuestId);
        return Prepare(character, [(floor.TitleQuestId, default(TitleCounters))], floor.TitleQuestId, now);
    }

    public TitleAchievementResult Trigger(Character character, TitleTriggerRequest request, DateTimeOffset now)
    {
        if (FiendwarTitleFacts.IsQuest(request.QuestId)) return TitleAchievementResult.Reject(request.QuestId);
        if (!titles.Quests.TryGetValue(request.QuestId, out var definition) ||
            !QuestEligibility.Evaluate(character, definition.Quest, quests, allowAboveMaximum: true).Eligible)
            return TitleAchievementResult.Reject(request.QuestId, 1);
        var previous = character.TitleProgress.GetValueOrDefault(request.QuestId);
        if (previous?.Remaining.Complete == true) return TitleAchievementResult.Reject(request.QuestId);
        var quest = definition.Quest;
        // 客户端没有本服尚未实现玩法的有效上下文，不能仅凭计数声称完成。
        if (quest.Condition is not ("[hunt monster]" or "[hunt enemy]" or "[condition under clear]" or "[seeking]" or
            "[use item]" or "[get score]" or "[get item]" or "[custom quest]" or "[send chatting]" or "[use fortune coin]" or
            "[clear quest]" or "[level up]" or "[disjoint item]" or "[amplify item]" or "[check time]" or
            "[equipped item]" or "[ui click]" or "[awakening]" or "[clear a dungeon]" or "[first grow]") ||
            titles.QuestCategories.GetValueOrDefault(quest.Id, []).Any(category => category is 2 or 3) ||
            !CheckStateFact(character, quest, now)) return TitleAchievementResult.Reject(request.QuestId);
        var remaining = (previous?.Remaining ?? definition.Initial).Subtract(request.Delta);
        return Prepare(character, [(request.QuestId, remaining)], request.QuestId, now);
    }

    // GM：把指定成就直接置为完成，复用同一套奖励/称号簿/邮件规划（不检查玩法条件）。
    // 整批被拒（如背包无空位、奖励类型不受支持）时逐个试探，只提交可成功的，其余作为 skipped 回报。
    public (TitleAchievementResult Result, ImmutableArray<int> Skipped) Complete(Character character, IReadOnlyCollection<int> questIds, DateTimeOffset now)
    {
        var wanted = questIds.Distinct().Where(id => titles.Quests.ContainsKey(id) &&
            character.TitleProgress.GetValueOrDefault(id)?.Remaining.Complete != true).Order().ToArray();
        (int, TitleCounters)[] Rows(IEnumerable<int> ids) => [.. ids.Select(id => (id, default(TitleCounters)))];
        if (wanted.Length == 0) return (TitleAchievementResult.Reject(0), []);
        var all = Prepare(character, Rows(wanted), 0, now);
        if (all.Accepted) return (all, []);
        var accepted = wanted.Where(id => Prepare(character, Rows([id]), 0, now).Accepted).ToArray();
        if (accepted.Length == 0) return (all, [.. wanted]);
        return (Prepare(character, Rows(accepted), 0, now), [.. wanted.Except(accepted)]);
    }

    public TitleAchievementResult Reconcile(Character character, DateTimeOffset now,
        ImmutableArray<FiendwarTitleCounter> fiendwarFacts = default, int requested = 0, bool includeStateFacts = true)
    {
        var completed = QuestEligibility.Completed(character);
        var legacy = titles.Adjustments.Where(row => completed.Contains(row.QuestId)).Select(row => row.AchievementId).ToHashSet();
        var candidates = titles.Quests.Values.Where(definition => (legacy.Contains(definition.Quest.Id) ||
            definition.Quest.Condition is "[clear quest]" or "[level up]" or "[awakening]" or "[first grow]") &&
            character.TitleProgress.GetValueOrDefault(definition.Quest.Id)?.Remaining.Complete != true &&
            QuestEligibility.Evaluate(character, definition.Quest, quests, allowAboveMaximum: true).Eligible &&
            (legacy.Contains(definition.Quest.Id) || CheckStateFact(character, definition.Quest, now, completed))).Select(definition => (definition.Quest.Id, default(TitleCounters))).ToArray();
        var updates = includeStateFacts ? candidates.ToDictionary(row => row.Id, row => row.Item2) : new Dictionary<int, TitleCounters>();
        var consumed = ImmutableArray.CreateBuilder<FiendwarTitleCounter>();
        foreach (var fact in fiendwarFacts.IsDefault ? [] : fiendwarFacts)
        {
            if (!fact.Valid || !titles.Quests.TryGetValue(fact.Quest, out var definition) ||
                !titles.QuestCategories.GetValueOrDefault(fact.Quest, []).Contains(1) ||
                !QuestEligibility.Evaluate(character, definition.Quest, quests, allowAboveMaximum: true).Eligible) continue;
            var remaining = character.TitleProgress.GetValueOrDefault(fact.Quest)?.Remaining ?? definition.Initial;
            updates[fact.Quest] = remaining.Subtract(new(checked((ushort)fact.Pending), 0, 0));
            consumed.Add(fact);
        }
        var result = Prepare(character, updates.Select(row => (row.Key, row.Value)).ToArray(), requested, now);
        return result.Plan is { } plan ? result with { Plan = plan with { FiendwarFacts = consumed.ToImmutable() } } : result;
    }

    bool CheckStateFact(Character character, StoryQuest quest, DateTimeOffset now, HashSet<int>? completed = null)
    {
        return quest.Condition switch
        {
            "[clear quest]" => quest.IntData.Count > 0 && quest.IntData.All(id => (completed ?? QuestEligibility.Completed(character)).Contains(id)),
            "[level up]" => quest.IntData is [> 0] && character.Level >= quest.IntData[0],
            "[awakening]" => quest.IntData is [> 0] && (character.GrowType >> 4) >= quest.IntData[0],
            "[first grow]" => (character.GrowType & 15) != 0,
            "[seeking]" => Holds(character, quest, now.ToUnixTimeSeconds()),
            _ => true
        };
    }

    internal static bool IsHatchedCreature(InventoryItem item, ItemTemplate template, long now) =>
        item.Core.Kind == ItemKind.Creature && item.Core.SealFlag == 0 && template.Id == item.Core.ItemId &&
        template.Kind == ItemKind.Creature && template.CreatureSubType == 0 && !item.IsExpired(now);

    bool Holds(Character character, StoryQuest quest, long now)
    {
        if (quest.IntData.Count == 0 || quest.IntData.Count % 2 != 0) return false;
        if (quest.IntData.Where((_, at) => at % 2 == 0).Any(id => id < 0))
            QuestItemProgress.Prepare(character.Inventory, items);
        for (int at = 0; at < quest.IntData.Count; at += 2)
        {
            int itemId = quest.IntData[at], count = quest.IntData[at + 1];
            long held;
            if (CurrencyWallet.RewardKind(itemId) is { } currency) held = CurrencyWallet.Get(character.Inventory, currency);
            else if (itemId > 0) held = character.Inventory.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == itemId && !item.IsExpired(now)).Sum(item => (long)item.Core.Count);
            // 资源要求已孵化宠物；实例SealFlag不证明模板是成宠，未孵化蛋也可能为0。
            else if (itemId is >= -7 and < 0)
                held = QuestItemProgress.Count(character.Inventory, quest, itemId, items, now);
            else return false;
            if (count <= 0 || held < count) return false;
        }
        return true;
    }

    TitleAchievementResult Prepare(Character character, IReadOnlyList<(int QuestId, TitleCounters Remaining)> updates, int requested, DateTimeOffset now)
    {
        try { return PrepareRewards(character, updates, requested, now); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException)
        { return TitleAchievementResult.Reject(requested); }
    }

    TitleAchievementResult PrepareRewards(Character character, IReadOnlyList<(int QuestId, TitleCounters Remaining)> updates, int requested, DateTimeOffset now,
        int? category = null)
    {
        var progress = character.TitleProgress.ToBuilder();
        var inventory = character.Inventory;
        var owned = category is null ? null : inventory.Items.Where(item => !item.IsExpired(now.ToUnixTimeSeconds()))
            .Select(item => item.Core.ItemId).ToHashSet();
        var completed = ImmutableArray.CreateBuilder<int>();
        var postals = ImmutableArray.CreateBuilder<TitlePostal>();
        var postalItems = ImmutableArray.CreateBuilder<InventoryItem>();
        foreach (var (id, remaining) in updates)
        {
            if (progress.GetValueOrDefault(id)?.Remaining == remaining) continue;
            progress[id] = new(remaining, remaining.Complete ? character.Revision + 1 : 0);
            if (!remaining.Complete) continue;
            var quest = titles.Quests[id].Quest;
            if (quest.RewardType is not ("[item]" or "[title]")) return TitleAchievementResult.Reject(requested);
            var rewards = QuestFixedRewardDefinition.Parse(quest).ForJob(character.Job, character.GrowType);
            foreach (var reward in rewards)
            {
                if (titles.RewardSlot(id, reward.ItemId, category) is { } slot)
                {
                    if (reward.Count != 1 || items.Get(reward.ItemId).EquipmentSlot != 13) return TitleAchievementResult.Reject(requested);
                    var position = TitleBookService.Slot(slot.Category, slot.Index);
                    if (inventory.Find(position) is { } stored)
                    {
                        // 存量实体保持全部属性，不重造或覆盖已有称号。
                        if (stored.Core.ItemId != reward.ItemId) return TitleAchievementResult.Reject(requested);
                    }
                    // 全组领取保留已穿戴或仍在其他容器中的实体，不因称号簿空格再造同一称号。
                    else if (owned is null || owned.Add(reward.ItemId))
                        inventory = inventory with { Items = inventory.Items.Add(new(0, position, InventoryRewardGrantService.Create(items.Get(reward.ItemId), utcNow: now))) };
                }
                else
                {
                    if (category is not null) return TitleAchievementResult.Reject(requested);
                    var granted = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = inventory }, [new(reward.ItemId, reward.Count)], items, goldLimits, pieces, now);
                    if (!granted.Accepted) return TitleAchievementResult.Reject(requested);
                    inventory = granted.Plan!.After;
                }
            }
            completed.Add(id);
            if (quest.Definition?.Fields.FirstOrDefault(field => field.Name == "send postal") is { } postal)
            {
                int Integer(string name) => postal.Children.FirstOrDefault(field => field.Name == name)?.Values is [{ Type: 0 } value] ? value.Value : 0;
                string Text(string name) => postal.Children.FirstOrDefault(field => field.Name == name)?.Values is [{ Type: 6, Text: not null } value] ? value.Text : "";
                if (!Postal(new(Integer("item index"), Integer("item count"), Text("sender"), Text("message"), "title-quest:" + id)))
                    return TitleAchievementResult.Reject(requested);
            }
        }
        var sections = character.TitleSections.ToBuilder();
        var unlockedEmotions = ImmutableArray.CreateBuilder<int>();
        IEnumerable<KeyValuePair<int, TitleSectionDefinition>> candidates = completed.Count != 0 || requested == 0 ? titles.Sections : [];
        foreach (var (key, section) in candidates)
        {
            if (category is { } selected && section.Category != TitleCatalog.Categories[selected]) continue;
            if (sections.Contains(key)) continue;
            var required = titles.SectionQuests.GetValueOrDefault(key, []);
            if (required.Length == 0 || required.Any(id => progress.GetValueOrDefault(id)?.Remaining.Complete != true)) continue;
            if (section.PostalItem > 0 && !Postal(new(section.PostalItem, section.PostalCount, section.Sender, section.Message, "title-section:" + key)))
                return TitleAchievementResult.Reject(requested);
            sections.Add(key);
            if (section.Emotion > 0) unlockedEmotions.Add(section.Emotion);
        }
        var plan = new TitleAchievementPlan(character, new(character.Inventory, inventory, "title_achievement", new(character)),
            progress.ToImmutable(), sections.ToImmutable(), completed.ToImmutable(), postals.ToImmutable(), postalItems.ToImmutable())
        {
            UnlockedEmotions = unlockedEmotions.ToImmutable()
        };
        return new(true, 0, requested, requested == 0 ? default : progress.GetValueOrDefault(requested)?.Remaining ?? titles.Quests[requested].Initial, plan);

        bool Postal(TitlePostal postal)
        {
            if (postal.ItemId <= 0 || postal.Count <= 0 || postal.Sender.Length == 0 || postal.Message.Length == 0 ||
                CharacterName.Encoding.GetByteCount(postal.Sender) > 29 || CharacterName.Encoding.GetByteCount(postal.Message) > 511) return false;
            var template = items.Get(postal.ItemId);
            if (postal.Count > template.StackLimit) return false;
            var core = InventoryRewardGrantService.Create(template, postal.Count, now);
            if (core.IsExpired(now.ToUnixTimeSeconds())) return false;
            postals.Add(postal);
            postalItems.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 9),
                core));
            return true;
        }
    }
}
