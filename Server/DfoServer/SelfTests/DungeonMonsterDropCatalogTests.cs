using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class DungeonMonsterDropCatalogTests
{
    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = DungeonMonsterDropCatalog.Load(pvf);
        DungeonMonsterDropEntry[] actual = [new(1000, 50), new(1047, 200), new(1004, 100)];
        var goblin = catalog.Get(1);
        var thrower = catalog.Get(2);
        That(goblin.Path == "monster/goblin/goblin.mob"
            && thrower.Path == "monster/goblin/goblinthrower.mob", "掉落声明绑定匹配PVF与真实怪物目录身份");
        That(goblin.Items is not null && thrower.Items is not null && goblin.Items.SequenceEqual(actual)
            && thrower.Items.SequenceEqual(actual), "普通哥布林两模板保留三个真实物品与原整数参数");
        That(goblin.CommonChampionItems is { Count: 0 } && catalog.Get(107000903).Items is null
            && catalog.Get(107000906).Items is null, "空冠军块与Boss或同房普通怪缺失item块保持区别");
        That(ReferenceEquals(goblin, catalog.Get(1)) && goblin.Items is System.Collections.IList frozen && frozen.IsReadOnly,
            "同目录共享完整只读声明，不为每次击杀重新解析");
        Reject(() => catalog.Get(-1), "未知怪物不变成零掉落成功");
        var names = new Dictionary<int, string>
        {
            [1] = "[item]", [2] = "[/item]", [3] = "[common champion drop item]", [4] = "[/common champion drop item]",
            [5] = "[name]", [6] = "[未支持条件]"
        };
        string Resolve(int id) => names[id];
        DungeonMonsterDropDeclaration Parse(params PvfToken[] tokens) => DungeonMonsterDropCatalog.Parse(1, goblin.Path, tokens, Resolve);
        var duplicateRows = Parse(new(3, 1), new(0, 1000), new(0, 50), new(0, 1000), new(0, 0), new(3, 2));
        That(duplicateRows.Items!.SequenceEqual(new DungeonMonsterDropEntry[] { new(1000, 50), new(1000, 0) }),
            "重复物品行和零参数保持资源顺序，不归一化或过滤未知随机语义");
        var negativeParameter = Parse(new(3, 1), new(0, 1000), new(0, -1), new(3, 2));
        That(negativeParameter.Items![0].Parameter == -1, "负参数保持原声明，不擅自夹为概率或物品数量");
        That(Parse(new(3, 5), new(6, 1)).Items is null, "字符串中的item文字不冒充真实tag");
        That(Parse(new(3, 1), new(3, 2)).Items is { Count: 0 } && Parse().Items is null,
            "显式空块与缺失块不混同");
        PvfToken[] source = [new(3, 1), new(0, 1000), new(0, 50), new(3, 2)];
        var own = Parse(source);
        source[2] = new(0, 200);
        That(own.Items![0].Parameter == 50, "源token变化不能修改冻结掉落声明");
        Reject(() => Parse(new(3, 1), new(0, 1000), new(3, 2)), "残缺参数拒绝");
        Reject(() => Parse(new(3, 1), new(0, 1000), new(2, 50), new(3, 2)), "非整数参数拒绝");
        Reject(() => Parse(new(3, 1), new(0, 0), new(0, 50), new(3, 2)), "非法物品ID拒绝");
        Reject(() => Parse(new(3, 1), new(0, 1000), new(0, 50)), "缺失结束tag拒绝");
        Reject(() => Parse(new PvfToken(3, 2)), "孤立结束tag不伪装成缺失声明");
        Reject(() => Parse(new(3, 1), new(3, 6)), "未支持嵌套条件拒绝而不抽掉条件");
        var repeated = Parse(new(3, 1), new(0, 1000), new(0, 50), new(3, 2),
            new(3, 1), new(0, 1047), new(0, 200), new(3, 2));
        That(repeated.Items!.SequenceEqual(new[] { new DungeonMonsterDropEntry(1000, 50) }) &&
            repeated.ItemBlocks.Count == 2 && repeated.ItemBlocks[1][0].ItemId == 1047,
            "参考首块参与抽样，后续不同声明保留原值，不重复加权或令整个资源拒绝");
        That(Parse(new(3, 3), new(3, 4), new(3, 3), new(3, 4)).CommonChampionBlocks.Count == 2,
            "重复冠军块同样保留，每个块仍要求完整格式");
        int tauId = DungeonCatalog.ParseDirectory(pvf.ReadTokens("monster/monster.lst"), pvf.ResolveString, "monster")
            .Single(row => row.Value == "monster/tau/shauta.mob").Key;
        var tau = catalog.Get(tauId);
        That(tau.ItemBlocks.Count == 2 && tau.Items!.SequenceEqual(tau.ItemBlocks[1]),
            "萨乌塔真实资源含两个相同item块，首块不会被重复声明放大权重");
        Console.WriteLine($"PASS {Count - checks} 怪物掉落声明与匹配资源断言");

        void Reject(Action action, string name)
        {
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
                That(true, name);
                return;
            }
            throw new InvalidOperationException("FAIL expected InvalidDataException: " + name);
        }
    }
}
