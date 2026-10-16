using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 入口锁外解析当前任务与剧情子任务需要的物品；死亡路径只消费已装载模板。
internal sealed record QuestDropResources(IReadOnlyDictionary<int,StoryQuest> Quests,
    IReadOnlyDictionary<int,ItemTemplate> Items, EpicPieceCatalog Pieces, GrowthContractBenefits? Growth = null)
{
    public static QuestDropResources Create(Character character, DungeonEntryPlan entry, QuestCatalog catalog, ItemCatalog items, EpicPieceCatalog pieces,
        GrowthContractBenefits? growth = null)
    {
        var ids = character.ActiveMainQuests.Select(row => (int)row.QuestId).ToHashSet();
        foreach (int id in ids.ToArray())
            if (catalog.TryGet(id,out var parent) && QuestRelationProgress.SequentialParent(parent,catalog)) ids.UnionWith(parent.IntData);
        var definitions = ids.Where(id => catalog.TryGet(id,out _)).Select(catalog.Get)
            .Where(quest => QuestCoverage.HasOrdinaryDeclarations(quest)).ToDictionary(quest => quest.Id);
        var templates = definitions.Values.SelectMany(quest => quest.Definition!.Drops)
            .Where(row => row.MatchesEntry(entry.DungeonId,entry.Difficulty)).Select(row => row.ItemId).Distinct().ToDictionary(id => id,items.Get);
        foreach (var template in templates.Values)
            if (template.Kind is not (ItemKind.Quest or ItemKind.Material or ItemKind.Consumable) ||
                CurrencyWallet.RewardKind(template.Id) is not null || pieces.ByPiece.ContainsKey(template.Id))
                throw new NotSupportedException("任务掉落需要尚未接入的持有域: " + template.Id);
        return new(definitions,templates,pieces,growth);
    }
}

internal sealed record QuestDropOutcome(ushort QuestId, Guid AcceptanceId, QuestDropDefinition Definition, int Rolled, int Granted, ushort Error);

internal sealed class QuestDropPlan(Character expected, Guid eventId, DungeonRunIdentity identity, int mapId, int templateId,
    int enemyType, bool clear, DateTimeOffset frozenAt, ImmutableArray<QuestDropOutcome> outcomes, InventoryPlan inventory)
{
    public Character Expected { get; } = expected;
    public Guid EventId { get; } = eventId;
    public DungeonRunIdentity Identity { get; } = identity;
    public int MapId { get; } = mapId;
    public InventoryPlan Inventory { get; } = inventory;
    public ImmutableArray<QuestDropOutcome> Outcomes { get; } = outcomes;
    public string Snapshot { get; } = JsonSerializer.Serialize(new
    {
        expected.Id,expected.Revision,EventId = eventId,Identity = identity,MapId = mapId,TemplateId = templateId,
        EnemyType = enemyType,Clear = clear,FrozenAt = frozenAt,Outcomes = outcomes,
        Items = inventory.After.Items.Where(item => inventory.ChangedSlots.Contains(item.Position)).ToArray()
    });

    public static QuestDropPlan Create(Character character, DungeonEntryPlan entry, QuestDropResources resources,
        Guid eventId, DungeonRunIdentity identity, int mapId, int templateId, int enemyType, bool cleared,
        Func<int,int> draw, DateTimeOffset now)
    {
        if (eventId == Guid.Empty || identity.CharacterId != character.Id || identity.QuestId != entry.QuestId ||
            mapId <= 0 || templateId <= 0 || enemyType is not (1 or 2 or 3)) throw new InvalidDataException("任务掉落需要真实场景事件");
        var after = character.Inventory;
        var outcomes = ImmutableArray.CreateBuilder<QuestDropOutcome>();
        foreach (var active in character.ActiveMainQuests.OrderBy(row => row.Slot))
        {
            if (!QuestHuntProgress.Bound(active,entry) || !resources.Quests.TryGetValue(active.QuestId,out var quest)) continue;
            foreach (var row in quest.Definition!.Drops.Where(row => row.Matches(entry.DungeonId,entry.Difficulty,templateId,enemyType,cleared)))
            {
                long held = after.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == row.ItemId &&
                    !item.IsExpired(now.ToUnixTimeSeconds())).Sum(item => (long)item.Core.Count);
                long limit = row.MaximumHeld;
                if (quest.Condition is "[seeking]" or "[seek n meet npc]" && QuestLifecyclePlan.ValidSeeking(quest))
                {
                    long required = Enumerable.Range(0,(quest.IntData.Count - (quest.Condition == "[seek n meet npc]" ? 1 : 0)) / 2)
                        .Where(at => quest.IntData[at * 2] == row.ItemId).Sum(at => (long)quest.IntData[at * 2 + 1]);
                    if (required > 0) limit = limit < 0 ? required : Math.Min(limit,required);
                }
                if (limit >= 0 && held >= limit) continue;
                int rolled = 0;
                int rate = GrowthContractPolicy.QuestDropRate(character,row.Rate,resources.Growth,now.ToUnixTimeSeconds());
                if (rate == 100) rolled = row.Count;
                else if (rate != 0)
                    for (int at = 0; at < row.Count; at++)
                    {
                        int value = draw(100);
                        if (value is < 0 or >= 100) throw new InvalidDataException("任务掉落随机值越界");
                        if (value < rate) rolled++;
                    }
                int count = checked((int)Math.Min(rolled,limit < 0 ? rolled : Math.Max(0,limit - held)));
                ushort error = 0;
                if (count > 0)
                {
                    var template = resources.Items[row.ItemId];
                    if (count > template.StackLimit) error = 17;
                    else
                    {
                        var grant = InventoryRewardGrantService.TryGrantCore(character with { Inventory = after },
                            InventoryRewardGrantService.Create(template,count,now),template,resources.Pieces,now);
                        if (grant.Accepted) after = grant.Plan!.After;
                        else error = grant.Error;
                    }
                }
                outcomes.Add(new(active.QuestId,active.AcceptanceId,row,rolled,error == 0 ? count : 0,error));
            }
        }
        return new(character,eventId,identity,mapId,templateId,enemyType,cleared,now,outcomes.ToImmutable(),
            new(character.Inventory,after,"quest_drop"));
    }
}
