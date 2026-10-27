using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using PvfLib;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record TagEvildomRewardItem(int ItemId, int Count);
internal sealed record TagEvildomRewardDefinition(int DefinitionId, int Weight, ImmutableArray<TagEvildomRewardItem> Items);

// 结算卡牌编号是奖励定义，不是发给玩家的箱子；展开其已支持的确定性booster子池后才规划真实附件。
internal sealed class TagEvildomRewardCatalog
{
    public const string ResourcePath = "etc/tagtournament/tagevildomgathering.etc";
    public int WeeklyLimit { get; }
    public ImmutableArray<TagEvildomRewardDefinition> Hard { get; }
    public ImmutableArray<TagEvildomRewardDefinition> Easy { get; }
    public string ResourceKey { get; }

    public TagEvildomRewardCatalog(PvfArchive archive, BoosterCatalog boosters)
        : this(archive.ReadTokens(ResourcePath), archive.ResolveString, boosters.Get) { }

    internal TagEvildomRewardCatalog(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve,
        Func<int, BoosterDefinition> definition)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("再战奖励表存在结构异常");
        var weekly = Required("weekly reward", false);
        if (weekly.Values is not [{ Type: 0, Value: 3 }])
            throw new InvalidDataException("再战周额度与当前三队保护合同不一致");
        WeeklyLimit = 3;
        var returning = Required("tag tournament return village time", false);
        if (returning.Values is not [{ Type: 0, Value: 5 }, { Type: 0, Value: 30 }, { Type: 0, Value: 10 }])
            throw new InvalidDataException("再战关闭入场与强制回城时刻不符合本版日界合同");
        Hard = Read("clear reward");
        Easy = Read("clear reward easy mode");
        // 只标识这张已解释的小奖励表，不读取或校验整份PVF哈希。
        ResourceKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { WeeklyLimit, Hard, Easy }))));

        PvfTokenSection Required(string name, bool block)
        {
            var found = document.Sections.Where(row => row.Name == name).ToArray();
            if (found.Length != 1 || found[0].IsBlock != block || !found[0].Children.IsEmpty)
                throw new InvalidDataException("再战奖励字段缺失、重复或嵌套: " + name);
            return found[0];
        }
        ImmutableArray<TagEvildomRewardDefinition> Read(string name)
        {
            var values = Required(name, true).Values;
            if (values.IsEmpty || values.Length % 2 != 0 || values.Any(value => value.Type != 0 || value.Value <= 0))
                throw new InvalidDataException("再战奖励表要求正编号、权重完整配对");
            var result = ImmutableArray.CreateBuilder<TagEvildomRewardDefinition>();
            var ids = new HashSet<int>();
            long total = 0;
            for (int index = 0; index < values.Length; index += 2)
            {
                int id = values[index].Value, weight = values[index + 1].Value;
                if (!ids.Add(id)) throw new InvalidDataException("再战奖励定义重复");
                total += weight;
                result.Add(new(id, weight, Expand(definition(id))));
            }
            if (total != 10_000) throw new InvalidDataException("再战奖励权重总和不符合当前万分表");
            return result.ToImmutable();
        }
    }

    internal static ImmutableArray<TagEvildomRewardItem> Expand(BoosterDefinition source)
    {
        if (!source.Available || source.Selection is not null || !source.PackageRewards.IsDefaultOrEmpty ||
            source.Lottery || source.NpcPocketPot || !source.FirstGrowTypes.IsEmpty || source.UnsealsRandomOptions ||
            source.GrantsEpicExchangeOrigin || source.SetsDisjointInstanceType || source.DailyRetainedLimit is not null ||
            source.RuneBox is not null || source.Pools.IsDefaultOrEmpty || source.Pools.Length > 10)
            throw new InvalidDataException("再战结算定义包含未支持的开箱或附加资格");
        var result = ImmutableArray.CreateBuilder<TagEvildomRewardItem>(source.Pools.Length);
        var ids = new HashSet<int>();
        foreach (var pool in source.Pools)
        {
            if (pool.Draws != 1 || pool.JobTag is not null || pool.MinimumLevel != 0 || pool.MaximumLevel != int.MaxValue ||
                pool.GrowType != -1 || pool.RequiredKind is not null || pool.Rewards is not [{ Weight: > 0 } item] ||
                item.ItemId <= 0 || item.Count <= 0 || !ids.Add(item.ItemId))
                throw new InvalidDataException("再战结算子池必须为无职业条件的一次确定性产物");
            result.Add(new(item.ItemId, item.Count));
        }
        return result.MoveToImmutable();
    }

    public ImmutableArray<TagEvildomRewardDefinition> ForDungeon(int dungeon) => dungeon switch
    {
        9608 => Hard,
        400000095 => Easy,
        _ => throw new InvalidDataException("再战奖励不能用于其它副本")
    };
}
