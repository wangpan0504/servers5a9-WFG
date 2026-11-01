using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal readonly record struct TournamentCardWeights(int Gold, int Item, int Empty)
{
    public int Total => checked(Gold + Item + Empty);
}

internal readonly record struct TournamentRewardItem(int ItemId, int Weight, int Count);

internal sealed record DungeonTournamentRewards(ImmutableArray<uint> Experience,
    ImmutableDictionary<int, TournamentCardWeights> Cards, ImmutableArray<TournamentRewardItem> Items, decimal GoldRate)
{
    public const string Rule = "pvf-tournament-rewards-v1";

    public static DungeonTournamentRewards Parse(DungeonDefinition dungeon)
    {
        var sections = dungeon.RawSections;
        var experience = new uint[5];
        var seenExperience = new HashSet<int>();
        foreach (var row in Field("tournament clear reward exp").Chunk(2))
        {
            if (row.Length != 2 || row[0] is < 0 or > 4 || row[1] < 0 || !seenExperience.Add(row[0]))
                throw new InvalidDataException("大会经验表需要唯一的轮数和非负经验");
            experience[row[0]] = checked((uint)row[1]);
        }
        var cards = ImmutableDictionary.CreateBuilder<int, TournamentCardWeights>();
        var items = ImmutableArray.CreateBuilder<TournamentRewardItem>();
        bool inside = false, found = false;
        foreach (var section in sections)
        {
            if (section.Tag == "result card")
            {
                if (inside || found) throw new InvalidDataException("大会奖励表重复");
                inside = found = true;
            }
            else if (section.Tag == "/result card") { inside = false; continue; }
            if (!inside) continue;
            if (section.Tag is not ("result card" or "reward item rate" or "/reward item rate")) continue;
            int[] values = Integers(section.Tokens);
            if (section.Tag == "reward item rate")
            {
                foreach (var row in values.Chunk(3))
                {
                    if (row.Length != 3 || row[0] <= 0 || row[1] < 0 || row[2] <= 0)
                        throw new InvalidDataException("大会物品奖励需要编号、权重和数量");
                    items.Add(new(row[0], row[1], row[2]));
                }
            }
            else if (section.Tag is "result card" or "/reward item rate")
            {
                foreach (var row in values.Chunk(4))
                {
                    if (row.Length != 4 || row[0] is < 0 or > 5 || row.Skip(1).Any(value => value < 0))
                        throw new InvalidDataException("大会牌型需要轮数及金币、物品、空牌三项权重");
                    var weights = new TournamentCardWeights(row[1], row[2], row[3]);
                    if (weights.Total <= 0 || !cards.TryAdd(row[0], weights))
                        throw new InvalidDataException("大会牌型权重为空或轮数重复");
                }
            }
        }
        if (inside) throw new InvalidDataException("大会奖励表未闭合");
        if (cards.Values.Any(row => row.Item > 0) && items.Sum(row => row.Weight) <= 0)
            throw new InvalidDataException("大会物品牌缺少有效物品池");
        var goldFields = sections.Where(row => row.Tag == "tournament clear reward gold rate").ToArray();
        decimal gold = 1;
        if (goldFields.Length > 0)
        {
            if (goldFields.Length != 1 || goldFields[0].Tokens is not [var token] || token.Type is not (0 or 2))
                throw new InvalidDataException("大会金币倍率不是唯一数值");
            gold = token.Type == 0 ? token.Value : (decimal)BitConverter.Int32BitsToSingle(token.Value);
            if (gold < 0) gold = 1;
        }
        return new([.. experience], cards.ToImmutable(), items.ToImmutable(), gold);

        int[] Field(string tag)
        {
            var rows = sections.Where(row => row.Tag == tag).ToArray();
            if (rows.Length > 1) throw new InvalidDataException("大会字段重复: " + tag);
            return rows.Length == 0 ? [] : Integers(rows[0].Tokens);
        }
        static int[] Integers(IReadOnlyList<PvfToken> values) => values.All(token => token.Type == 0)
            ? values.Select(token => token.Value).ToArray() : throw new InvalidDataException("大会奖励行需要整数");
    }
}
