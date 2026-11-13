using System.Collections.Frozen;
using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer.GameWorld.MonsterMockBattle;

internal readonly record struct MockBattleStats(int Hp, int Attack, int Hit, int Critical);
internal sealed record MockBattleAttribute(int Code, ImmutableArray<int> TextKeys, ImmutableArray<float> Rates);
internal sealed record MockBattleCardDefinition(int Id, int MonsterId, ImmutableArray<int> TextKeys,
    ImmutableArray<MockBattleStats> NormalStats, ImmutableArray<MockBattleAttribute> NormalAttributes,
    ImmutableArray<MockBattleStats> SpecialStats, ImmutableArray<MockBattleAttribute> SpecialAttributes,
    ImmutableArray<int> RawClassification);
internal readonly record struct MockBattleDraw(int CardId, int Weight, int Stars);
internal sealed record MockBattleBooster(int ItemId, ImmutableArray<MockBattleDraw> Draws);
internal readonly record struct MockBattlePreview(int Stars, string Animation);
internal readonly record struct MockBattleItemReward(int ItemId, int Count);
internal sealed record MockBattleRewards(ImmutableArray<MockBattleItemReward> Items, int Coins);
internal sealed record MockBattleWorldDefinition(int Id, int DungeonId, int Mode,
    ImmutableArray<int> DisplayParameters, MockBattleRewards FirstReward, MockBattleRewards RepeatReward,
    int FirstHeartCost, int RepeatHeartCost, ImmutableArray<MockBattlePreview> Preview);
internal readonly record struct MockBattlePosition(int Team, int Slot, int X, int Y);
internal readonly record struct MockBattleAchievement(int Id, int Type, int Parameter, int Required,
    int ItemId, int Count, int TitleKey, int DescriptionKey);

// 数值直接读取token，保留float精度；资源摘要不参与目录加载或进度资格。
internal sealed class MonsterMockBattleCatalog
{
    public const string ResourcePath = "event/gcontents/200114_monstermockbattle/monstermockbattle.evt";
    public FrozenDictionary<int, MockBattleCardDefinition> Cards { get; }
    public ImmutableArray<MockBattleDraw> CoinDraws { get; }
    public FrozenDictionary<int, MockBattleBooster> ItemDraws { get; }
    public ImmutableArray<MockBattleAchievement> Achievements { get; }
    public ImmutableArray<int> BuyCosts { get; }
    public ImmutableArray<int> ComposeCosts { get; }
    public ImmutableArray<MockBattleStats> BaseStats { get; }
    public FrozenDictionary<int, MockBattleWorldDefinition> Worlds { get; }
    public ImmutableArray<int> PvpParameters { get; }
    public ImmutableArray<MockBattlePosition> Positions { get; }
    public ImmutableArray<(DateOnly Date, int World)> SpecialDays { get; }

    public static MonsterMockBattleCatalog Load(PvfArchive archive) =>
        Parse(archive.ReadTokens(ResourcePath), archive.ResolveString);

    public DungeonDefinition Battlefield(DungeonDefinition definition)
    {
        if (!Worlds.Values.Any(world => world.DungeonId == definition.Id) || definition.Mazes is not [var source] ||
            source.ResourceError is not null || source.MapSpecification is not { Count: > 0 } maps ||
            source.Starts is not [var start] || !maps.Any(map => map.Position == start) ||
            maps.Any(map => map.Id != maps[0].Id || map.Path != maps[0].Path) ||
            !maps[0].Path.StartsWith("map/gcontents/200114_monstercolosseum/", StringComparison.Ordinal) ||
            source.ClearConditions is not [{ Type: "clear map", TargetId: var target, Count: 1 }] || target != maps[0].Id ||
            source.LayeredMaps.Count != 0 || source.ConditionalMaps.Count != 0 || source.StoryStages.Count != 0 ||
            source.DummyStages.Count != 0 || source.WarpMaps.Count != 0 || source.QuestConnectionTokens.Count != 0 ||
            source.HellParty is not null || source.BossEntrance is not null)
            throw new InvalidDataException("怪物模拟战战场与活动单地图清图声明不符");
        // 原DGN重复同一MAP，PvP的Boss坐标甚至不含地图；活动只在原清图目标对应的战场作战。
        var battlefield = maps[0] with { Kind = "boss", Position = new(0, 0) };
        var maze = source with { Width = 1, Height = 1, Greed = "BB", Starts = [battlefield.Position],
            Bosses = [battlefield.Position], MapSpecification = [battlefield], MockBattleRoom = battlefield };
        return definition with { Mazes = [maze] };
    }

    internal static MonsterMockBattleCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("怪物模拟战资源声明不完整");
        return new(document.Sections, resolve);
    }

    MonsterMockBattleCatalog(ImmutableArray<PvfTokenSection> sections, Func<int, string> resolve)
    {
        PvfTokenSection Required(string name)
        {
            var matches = sections.Where(row => row.Name == name).ToArray();
            if (matches.Length != 1 || !matches[0].Children.IsEmpty)
                throw new InvalidDataException("怪物模拟战唯一资源段缺失、重复或包含未知嵌套: " + name);
            return matches[0];
        }
        ImmutableArray<int> Integers(string name, int count)
        {
            var values = Required(name).Values;
            if (values.Length != count) throw new InvalidDataException("怪物模拟战标量数量错误: " + name);
            return values.Select(Integer).ToImmutableArray();
        }
        var cards = new Dictionary<int, MockBattleCardDefinition>();
        foreach (var section in sections.Where(row => row.Name == "user card info"))
        {
            var values = section.Values;
            if (values.Length != 208 || !section.Children.IsEmpty)
                throw new InvalidDataException("怪物模拟战卡片定义必须包含208个字段");
            int position = 0;
            int Next() => Integer(values[position++]);
            ImmutableArray<MockBattleStats> Stats()
            {
                var result = ImmutableArray.CreateBuilder<MockBattleStats>(5);
                for (int index = 0; index < 5; index++)
                {
                    var stats = new MockBattleStats(Next(), Next(), Next(), Next());
                    if (stats.Hp <= 0 || stats.Attack < 0 || stats.Hit < 0 || stats.Critical < 0)
                        throw new InvalidDataException("怪物模拟战卡片基础属性无效");
                    result.Add(stats);
                }
                return result.MoveToImmutable();
            }
            ImmutableArray<MockBattleAttribute> Attributes()
            {
                var result = ImmutableArray.CreateBuilder<MockBattleAttribute>(10);
                for (int index = 0; index < 10; index++)
                {
                    int code = Next();
                    var keys = ImmutableArray.Create(Next(), Next(), Next());
                    var rates = ImmutableArray.CreateBuilder<float>(4);
                    for (int rate = 0; rate < 4; rate++)
                    {
                        PvfToken token = values[position++];
                        float value = token.Type == 2 ? BitConverter.Int32BitsToSingle(token.Value) :
                            throw new InvalidDataException("怪物模拟战属性倍率必须为float");
                        if (!float.IsFinite(value)) throw new InvalidDataException("怪物模拟战属性倍率不是有限数值");
                        rates.Add(value);
                    }
                    result.Add(new(code, keys, rates.MoveToImmutable()));
                }
                return result.MoveToImmutable();
            }
            int id = Next(), monster = Next();
            var text = ImmutableArray.Create(Next(), Next(), Next());
            var normal = Stats(); var normalAttributes = Attributes();
            var special = Stats(); var specialAttributes = Attributes();
            var classification = ImmutableArray.Create(Next(), Next(), Next());
            if (id is < 0 or > 127 || monster <= 0 || !cards.TryAdd(id,
                new(id, monster, text, normal, normalAttributes, special, specialAttributes, classification)))
                throw new InvalidDataException("怪物模拟战卡片编号无效或重复");
        }
        if (cards.Count == 0) throw new InvalidDataException("怪物模拟战卡片目录为空");
        Cards = cards.ToFrozenDictionary();
        BuyCosts = Integers("monster card buy cost", 2);
        ComposeCosts = Integers("monster card compose cost", 4);
        if (BuyCosts.Any(value => value < 0) || BuyCosts[1] > 100 || ComposeCosts.Any(value => value < 0))
            throw new InvalidDataException("怪物模拟战费用不能为负数");
        var baseline = Integers("monster status base parameter", 4);
        BaseStats = [new(baseline[0], baseline[1], baseline[2], baseline[3])];
        CoinDraws = Draws(Required("random monster card booster by coin").Values, selectable: false);
        var boosters = new Dictionary<int, MockBattleBooster>();
        foreach (var section in sections.Where(row => row.Name == "random monster card booster by item"))
        {
            if (section.Values.Length < 5 || !section.Children.IsEmpty)
                throw new InvalidDataException("怪物模拟战物品抽卡定义不完整");
            int item = Integer(section.Values[0]), count = Integer(section.Values[1]);
            if (count <= 0 || section.Values.Length != 2 + (long)count * 3)
                throw new InvalidDataException("怪物模拟战物品抽卡池项数不符");
            // 原生指定召唤券的全部权重为0；选择按该池内的序号进行。
            var draws = Draws(section.Values[2..], selectable: true);
            if (item <= 0 || !boosters.TryAdd(item, new(item, draws)))
                throw new InvalidDataException("怪物模拟战抽卡物品编号无效或重复");
        }
        ItemDraws = boosters.ToFrozenDictionary();
        var achievementValues = Required("achievement info").Values;
        if (achievementValues.IsEmpty || achievementValues.Length % 8 != 0)
            throw new InvalidDataException("怪物模拟战成就字段不完整");
        var achievements = ImmutableArray.CreateBuilder<MockBattleAchievement>();
        var achievementIds = new HashSet<int>();
        for (int offset = 0; offset < achievementValues.Length; offset += 8)
        {
            var row = achievementValues[offset..(offset + 8)].Select(Integer).ToArray();
            if (row[0] is < 0 or >= 21 || row[3] is <= 0 or > 255 || row[4] <= 0 || row[5] <= 0 || !achievementIds.Add(row[0]))
                throw new InvalidDataException("怪物模拟战成就编号、目标或奖励无效");
            achievements.Add(new(row[0], row[1], row[2], row[3], row[4], row[5], row[6], row[7]));
        }
        if (achievements.Count != 21) throw new InvalidDataException("本版怪物模拟战必须包含完整21项成就");
        Achievements = achievements.ToImmutable();
        var worlds = new Dictionary<int, MockBattleWorldDefinition>();
        foreach (var section in sections.Where(row => row.Name == "world info"))
        {
            var values = section.Values;
            if (values.Length < 24 || !section.Children.IsEmpty)
                throw new InvalidDataException("怪物模拟战关卡定义不完整");
            int cursor = 0;
            int Next() => Integer(values[cursor++]);
            var parameters = Enumerable.Range(0, 6).Select(_ => Next()).ToImmutableArray();
            // 03E6C160：数量、是否包含金币、数量组(item,count)、可选金币。
            MockBattleRewards Rewards()
            {
                int count = Next(), coinFlag = Next();
                if (count is < 0 or > 3 || coinFlag is < 0 or > 1)
                    throw new InvalidDataException("怪物模拟战关卡奖励数量或金币标志无效");
                var rewards = ImmutableArray.CreateBuilder<MockBattleItemReward>(count);
                for (int index = 0; index < count; index++)
                {
                    int item = Next(), quantity = Next();
                    if (item <= 0 || quantity <= 0) throw new InvalidDataException("怪物模拟战关卡奖励无效");
                    rewards.Add(new(item, quantity));
                }
                int coins = coinFlag == 1 ? Next() : 0;
                if (coins < 0) throw new InvalidDataException("怪物模拟战关卡金币不能为负数");
                return new(rewards.MoveToImmutable(), coins);
            }
            var firstReward = Rewards();
            var repeatReward = Rewards();
            int firstCost = Next(), repeatCost = Next();
            int id = parameters[0], dungeon = Next(), mode = Next();
            if (cursor != values.Length - 10 || firstCost < 0 || repeatCost < 0)
                throw new InvalidDataException("怪物模拟战关卡奖励、费用或预览边界无效");
            var previews = ImmutableArray.CreateBuilder<MockBattlePreview>(5);
            for (int index = values.Length - 10; index < values.Length; index += 2)
            {
                int stars = Integer(values[index]);
                string animation = values[index + 1].Type is 6 or 7 ? resolve(values[index + 1].Value) :
                    throw new InvalidDataException("怪物模拟战关卡预览不是字符串");
                if (stars is < 0 or > 5) throw new InvalidDataException("怪物模拟战关卡预览星级无效");
                previews.Add(new(stars, animation));
            }
            if (id < 0 || dungeon <= 0 || mode is < 0 or > 3 ||
                !worlds.TryAdd(id, new(id, dungeon, mode, parameters, firstReward, repeatReward,
                    firstCost, repeatCost, previews.MoveToImmutable())))
                throw new InvalidDataException("怪物模拟战关卡编号、地下城或模式无效");
        }
        if (worlds.Count == 0) throw new InvalidDataException("怪物模拟战关卡目录为空");
        Worlds = worlds.ToFrozenDictionary();
        PvpParameters = Integers("pvp info", 3);
        var placements = Required("monster disposition list").Values;
        if (placements.Length != 18 * 11) throw new InvalidDataException("怪物模拟战阵位表必须包含双方各9个阵位");
        var positions = ImmutableArray.CreateBuilder<MockBattlePosition>(18);
        for (int index = 0; index < 18; index++)
        {
            int offset = index * 11, team = Integer(placements[offset + 10]);
            if (team != index / 9) throw new InvalidDataException("怪物模拟战阵位阵营顺序无效");
            positions.Add(new(team, index % 9, Integer(placements[offset + 3]), Integer(placements[offset + 4])));
        }
        Positions = positions.MoveToImmutable();
        var days = Required("special dungeon info").Values;
        if (days.Length % 2 != 0) throw new InvalidDataException("怪物模拟战特殊关卡日程不完整");
        var specialDays = ImmutableArray.CreateBuilder<(DateOnly, int)>(days.Length / 2);
        for (int index = 0; index < days.Length; index += 2)
        {
            if (days[index].Type is not (6 or 7) || !DateOnly.TryParseExact(resolve(days[index].Value), "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ||
                !worlds.TryGetValue(Integer(days[index + 1]), out var world) || world.Mode != 1)
                throw new InvalidDataException("怪物模拟战特殊关卡日程无效");
            specialDays.Add((date, world.Id));
        }
        SpecialDays = specialDays.MoveToImmutable();

        ImmutableArray<MockBattleDraw> Draws(ImmutableArray<PvfToken> values, bool selectable)
        {
            // 每项为card/weight/star；同卡不同星保留为独立权重。
            var result = ImmutableArray.CreateBuilder<MockBattleDraw>();
            int offset = 0;
            if (values.Length % 3 != 0) throw new InvalidDataException("怪物模拟战抽卡池字段不完整");
            while (offset < values.Length)
            {
                int card = Integer(values[offset++]), weight = Integer(values[offset++]), stars = Integer(values[offset++]);
                if (!cards.ContainsKey(card) || weight < 0 || stars is < 1 or > 5)
                    throw new InvalidDataException("怪物模拟战抽卡引用、权重或星级无效");
                result.Add(new(card, weight, stars));
            }
            if (result.Count == 0 || !selectable && result.All(row => row.Weight == 0))
                throw new InvalidDataException("怪物模拟战抽卡池没有有效权重");
            return result.ToImmutable();
        }
    }

    static int Integer(PvfToken token) => token.Type == 0 ? token.Value :
        throw new InvalidDataException("怪物模拟战字段必须为整数");
}
