using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonPassiveDropOrigin(int ParentOrdinal, byte AssociationSlot, int ActionOrdinal, int GroupIndex);
internal sealed record DungeonPassiveDropPreparation(DungeonPlannedSpecialObject Parent, DungeonPassiveDropOrigin Origin,
    DungeonMap Map, ImmutableArray<DungeonGroundContent> Contents, string ResourceSource);

// 规则与物品创建在入场owner锁外完成；一个MAP父对象的多个[item]共享关联槽，而不是使用掉落组号。
internal sealed class DungeonPassiveDropPolicy(Func<int, DungeonDefinition> resolveDungeon, Func<int, ItemTemplate> resolveItem,
    DungeonDropRuleCatalog? objectRules = null, DungeonItemDropCatalog? candidates = null)
{
    public const string Rule = "pvf-special-passive-item-v1";
    public IReadOnlyList<DungeonPassiveDropPreparation> Prepare(DungeonEntryPlan entry, DungeonMonsterPlan room,
        Func<int, int> draw, DateTimeOffset now)
    {
        if (entry.IsTutorial || entry.IsClassExperience || entry.IsTrainingRoom)
            return [];
        var definition = resolveDungeon(checked((int)entry.DungeonId));
        if (definition.RawSections.Any(section => section.Tag == "disable drop item" && section.Tokens.Count == 0))
            return [];
        var groups = DungeonPassiveItemGroup.Parse(definition.RawSections);
        var result = new List<DungeonPassiveDropPreparation>();
        int association = 0, maximum = 0;
        foreach (var parent in room.SpecialObjects)
        {
            var actions = parent.Source.Children.Where(child => child.Kind == "[item]").ToArray();
            if (actions.Length == 0)
                continue;
            if (association > byte.MaxValue)
                throw new InvalidDataException("特殊物品父对象关联槽超过单字节范围");
            foreach (var action in actions)
            {
                var values = action.RawValues;
                if (values.Count == 5 && values[0] == -1)
                    continue;
                if (values.Count != 5 || values[0] < 0 || values[0] >= groups.Length ||
                    values[1] is < -1 or > 127 || values[2] is < -1 or > 127)
                    throw new InvalidDataException("特殊物品子规则的组或有符号次数无效");
                var group = groups[values[0]];
                var pool = new DungeonItemDropPool(group.Items.Where(item => item.Weight > 0)
                    .Select(item => new DungeonWeightedDropItem(resolveItem(item.ItemId), item.Weight)));
                if (pool.Entries.Any(item => !DungeonDropCreation.Supports(item.Template.Kind)))
                    throw new NotSupportedException("特殊对象掉落尚未绑定该物品类别的创建合同");
                var random = values[2] > 0 ? RandomPlan(group.Level == -1 ? entry.BasisLevel ?? 0 : group.Level, entry.Difficulty) : null;
                maximum = checked(maximum + (pool.TotalWeight == 0 ? 0 : Math.Max(0, values[1])) +
                    Math.Max(0, values[2]) * (random?.MaximumObjects ?? 0));
                if (maximum > byte.MaxValue)
                    throw new InvalidDataException("房间完整特殊物品声明超过入房通知容量，不能截断掉落");
                var contents = ImmutableArray.CreateBuilder<DungeonGroundContent>();
                // df_game_r先进行通用物件抽取，再进行指定池抽取；空组仍保留已声明的抽取次数。
                for (int i = 0; i < values[2]; i++)
                    contents.AddRange(random!.Generate(draw, now));
                for (int i = 0; i < values[1]; i++)
                {
                    int roll = draw(10001);
                    if (roll is < 0 or > 10000)
                        throw new InvalidDataException("特殊指定掉落随机数越界");
                    if (roll < pool.TotalWeight)
                        contents.Add(new(DungeonDropCreation.Create(pool.Select(roll), 1, draw, now, referenceQuality: true)));
                }
                result.Add(new(parent, new(parent.Source.SourceOrdinal, checked((byte)association), action.SourceOrdinal, group.Index),
                    room.Room.Map, contents.ToImmutable(), Rule + ":" + definition.Path));
            }
            association++;
        }
        return result;
    }

    DungeonDropGenerationPlan RandomPlan(int level, byte difficulty)
    {
        if (objectRules is null || candidates is null)
            throw new NotSupportedException("通用物件掉落规则未装配");
        var probability = objectRules.ProbabilityRows.SingleOrDefault(row => level >= row.MinimumLevel && level <= row.MaximumLevel)
            ?? throw new InvalidDataException("物件掉落缺少生成等级段");
        var reference = objectRules.ItemReferences.SingleOrDefault(row => row.Level == level);
        var gold = objectRules.GoldReferences.SingleOrDefault(row => row.Level == level);
        if (reference.Level == 0 || gold.Level == 0 || reference.Value0 < 0 || reference.Value1 < 0)
            throw new InvalidDataException("物件掉落缺少物品或金币等级引用");
        if (probability.Values[3] != 0 || probability.Values[4] != 0)
            throw new NotSupportedException("物件配方及神器独立候选类别尚未绑定");
        var trials = new List<DungeonDropTrial>();
        for (int channel = 0; channel < 3; channel++)
        {
            int threshold = checked((int)Math.Min(10000, Math.Floor(probability.Values[channel] *
                objectRules.DifficultyBonus[channel * 5 + difficulty].ReferenceDouble *
                objectRules.MonsterTypeBonus[channel * 4].ReferenceDouble)));
            if (threshold == 0)
                continue;
            if (channel == 0)
                trials.Add(new(threshold, null, 1, 1, gold));
            else
            {
                var pools = Enumerable.Range(0, 7).Select(rarity => new DungeonDropRarityPool(
                    objectRules.RarityDecision[(channel - 1) * 7 + rarity], candidates.ReferencePool(channel == 2,
                        level - reference.Value0, level + reference.Value1 - 1, rarity))).ToImmutableArray();
                trials.Add(new(threshold, null, 1, 1, RarityPools: pools));
            }
        }
        return new(Rule + ":etc/itemdropinfo_object.etc", trials, referenceQuality: true);
    }
}
