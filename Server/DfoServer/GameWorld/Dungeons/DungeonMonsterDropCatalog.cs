using System.Collections.Concurrent;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// Parameter保持PVF原整数；这里不规定随机分母、数量或是否独立抽取。
internal sealed record DungeonMonsterDropEntry(int ItemId, int Parameter);
internal sealed record DungeonMonsterDropDeclaration(int MonsterId, string Path,
    IReadOnlyList<DungeonMonsterDropEntry>? Items, IReadOnlyList<DungeonMonsterDropEntry>? CommonChampionItems)
{
    public IReadOnlyList<IReadOnlyList<DungeonMonsterDropEntry>> ItemBlocks { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<DungeonMonsterDropEntry>> CommonChampionBlocks { get; init; } = [];
}

internal sealed class DungeonMonsterDropCatalog
{
    readonly PvfArchive archive;
    readonly IReadOnlyDictionary<int, string> monsters;
    readonly ConcurrentDictionary<int, Lazy<DungeonMonsterDropDeclaration>> declarations = new();

    private DungeonMonsterDropCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> monsters)
        => (this.archive, this.monsters) = (archive, monsters);

    public static DungeonMonsterDropCatalog Load(PvfArchive archive) => new(archive,
        DungeonCatalog.ParseDirectory(archive.ReadTokens("monster/monster.lst"), archive.ResolveString, "monster"));

    public DungeonMonsterDropDeclaration Get(int monsterId)
    {
        if (!monsters.TryGetValue(monsterId, out string? path) || !archive.Contains(path))
            throw new InvalidDataException("怪物掉落资源不存在: " + monsterId);
        // 完整解析后才发布；重复击杀共享不可变声明，不在随机规划中重新打开PVF。
        return declarations.GetOrAdd(monsterId, _ => new(() => Parse(monsterId, path, archive.ReadTokens(path), archive.ResolveString),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    internal static DungeonMonsterDropDeclaration Parse(int monsterId, string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        IReadOnlyList<DungeonMonsterDropEntry>? items = null;
        IReadOnlyList<DungeonMonsterDropEntry>? champion = null;
        var itemBlocks = new List<IReadOnlyList<DungeonMonsterDropEntry>>();
        var championBlocks = new List<IReadOnlyList<DungeonMonsterDropEntry>>();
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3)
                continue;
            string tag = resolve(tokens[at].Value).ToLowerInvariant();
            if (tag is "[/item]" or "[/common champion drop item]")
                throw new InvalidDataException("怪物掉落结束块缺少起始块: " + path);
            if (tag is not ("[item]" or "[common champion drop item]"))
                continue;
            string closing = "[/" + tag[1..];
            var rows = new List<DungeonMonsterDropEntry>();
            at++;
            while (at < tokens.Count && tokens[at].Type != 3)
            {
                if (at + 1 >= tokens.Count || tokens[at].Type != 0 || tokens[at + 1].Type != 0 || tokens[at].Value <= 0)
                    throw new InvalidDataException("怪物掉落不是完整的正物品ID与整数参数对: " + path);
                rows.Add(new(tokens[at].Value, tokens[at + 1].Value));
                at += 2;
            }
            if (at >= tokens.Count || resolve(tokens[at].Value).ToLowerInvariant() != closing)
                throw new InvalidDataException("怪物掉落块未闭合或包含未支持条件: " + path);
            var frozen = Array.AsReadOnly(rows.ToArray());
            if (tag == "[item]")
            {
                itemBlocks.Add(frozen);
                items ??= frozen;
            }
            else
            {
                championBlocks.Add(frozen);
                champion ??= frozen;
            }
        }
        // 缺失块是null，显式空块是空集合；二者不能被解释成同一种默认掉落规则。
        // 参考服务端Section取首块；后续完整保留，不合并成重复权重，也不令正常副本断开。
        return new(monsterId, path, items, champion)
        { ItemBlocks = itemBlocks.AsReadOnly(), CommonChampionBlocks = championBlocks.AsReadOnly() };
    }
}
