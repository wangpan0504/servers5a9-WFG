using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer;

internal static class DungeonExplicitDropPolicyTests
{
    const string GoblinPath = "monster/goblin/goblin.mob";
    const string ThrowerPath = "monster/goblin/goblinthrower.mob";

    public static void RunCore()
    {
        int checks = 0;
        DungeonMonsterDropEntry[] source = Rows();
        ItemTemplate[] templates = [Template(1000), Template(1047), Template(1004)];
        ItemTemplate Resolve(int id) => templates.Single(template => template.Id == id);
        var declaration = new DungeonMonsterDropDeclaration(1, GoblinPath, source, []);
        var pool = Create(declaration, Resolve);
        var thrower = Create(declaration with { MonsterId = 2, Path = ThrowerPath }, Resolve);
        Check(DungeonExplicitDropPolicy.RuleId == "local-3145-explicit-consumables-10pct-v1" &&
            DungeonExplicitDropPolicy.TriggerDenominator == 10000 && DungeonExplicitDropPolicy.TriggerThreshold == 1000 &&
            DungeonExplicitDropPolicy.SelectionDenominator == 350, "只使用已批准本服规则的完整常量");
        Check(pool.MonsterId == 1 && pool.Path == GoblinPath && thrower.MonsterId == 2 && thrower.Path == ThrowerPath,
            "仅两个模板保持各自规范路径");
        Check(pool.Entries.Select(entry => (entry.SourceOrdinal, entry.Template.Id, entry.Weight))
            .SequenceEqual(new[] { (0, 1000, 50), (1, 1047, 200), (2, 1004, 100) }), "完整原序和零基资源行索引");
        Check(ReferenceEquals(pool.Entries[0].Template, templates[0]), "保留原不可变模板，不重建简化模板");

        int[] selectedCounts = new int[3];
        for (int roll = 0; roll < 350; roll++)
        {
            var choice = DungeonExplicitDropPolicy.Select(pool, 0, roll)!;
            int expectedOrdinal = roll < 50 ? 0 : roll < 250 ? 1 : 2;
            Check(choice.SourceOrdinal == expectedOrdinal && choice.Count == 1 &&
                ReferenceEquals(choice.Template, pool.Entries[expectedOrdinal].Template), "命中互斥区间及模板数量: " + roll);
            selectedCounts[choice.SourceOrdinal]++;
        }
        Check(selectedCounts.SequenceEqual(new[] { 50, 200, 100 }), "350个均匀选择值形成精确50/200/100权重区间");
        int triggered = 0;
        for (int roll = 0; roll < 10000; roll++)
        {
            if (DungeonExplicitDropPolicy.Select(pool, roll, 349) is not null)
                triggered++;
        }
        Check(triggered == 1000, "10000个均匀触发值仅前1000命中，不保证每怪掉落");
        Check(DungeonExplicitDropPolicy.Select(pool, 999, 49) is { SourceOrdinal: 0, Count: 1 } &&
            DungeonExplicitDropPolicy.Select(pool, 999, 50) is { SourceOrdinal: 1, Count: 1 } &&
            DungeonExplicitDropPolicy.Select(pool, 999, 249) is { SourceOrdinal: 1, Count: 1 } &&
            DungeonExplicitDropPolicy.Select(pool, 999, 250) is { SourceOrdinal: 2, Count: 1 } &&
            DungeonExplicitDropPolicy.Select(pool, 1000, 0) is null &&
            DungeonExplicitDropPolicy.Select(pool, 9999, 349) is null, "触发阈值和每个累计权重边界不越界");

        // 源数组可以被夹具改动；冻结池与已选Choice不得随数组替换而变化。
        var frozenChoice = DungeonExplicitDropPolicy.Select(pool, 0, 0)!;
        source[0] = new(1047, 999);
        templates[0] = templates[0] with { Name = "已改源数组", StackLimit = 1 };
        Check(pool.Entries[0].Weight == 50 && pool.Entries[0].Template.Id == 1000 &&
            pool.Entries[0].Template.StackLimit == 1000 && frozenChoice.Template == pool.Entries[0].Template &&
            frozenChoice.Count == 1 && DungeonExplicitDropPolicy.Select(pool, 0, 49) == frozenChoice,
            "原缓冲替换不污染冻结池、模板或Choice");

        var valid = new DungeonMonsterDropDeclaration(1, GoblinPath, Rows(), []);
        RejectData(() => DungeonExplicitDropPolicy.CreatePool("", valid, Template), "资源来源为空拒绝");
        RejectData(() => DungeonExplicitDropPolicy.CreatePool(" ", valid, Template), "资源来源空白拒绝");
        RejectData(() => Create(valid with { MonsterId = 107000903, Items = null }, Template), "Boss缺失声明不套用哥布林池");
        RejectData(() => Create(valid with { MonsterId = 107000906 }, Template), "其他普通模板即使复制声明也拒绝");
        RejectData(() => Create(valid with { MonsterId = 2 }, Template), "怪物ID与路径不匹配拒绝");
        RejectData(() => Create(valid with { Path = GoblinPath.ToUpperInvariant() }, Template), "非规范路径拒绝");
        RejectData(() => Create(valid with { Path = "monster/goblin/../goblin/goblin.mob" }, Template), "路径别名拒绝");
        RejectData(() => Create(valid with { Items = null }, Template), "缺失声明拒绝而非默认池");
        RejectData(() => Create(valid with { Items = [] }, Template), "显式空池拒绝而非默认池");
        RejectData(() => Create(valid with { Items = Rows()[..2] }, Template), "残缺池拒绝");
        RejectData(() => Create(valid with { Items = [.. Rows(), new(9999, 1)] }, Template), "后续未知行不能静默过滤");
        RejectData(() => Create(valid with { Items = [new(1000, 50), new(1000, 200), new(1004, 100)] }, Template), "重复物品替代行拒绝");
        RejectData(() => Create(valid with { Items = Rows().Reverse().ToArray() }, Template), "重排行不能以同总权重通过");
        foreach (int parameter in new[] { -1, 0, 49, int.MaxValue })
            RejectData(() => Create(valid with { Items = [new(1000, parameter), new(1047, 200), new(1004, 100)] }, Template),
                "任何变化的原始参数拒绝: " + parameter);

        RejectTemplate(template => template with { Id = 9999 }, "解析返回另一物品拒绝");
        RejectTemplate(template => template with { Kind = ItemKind.Equipment }, "装备不能进入显式消耗品池");
        RejectTemplate(template => template with { Kind = ItemKind.Material }, "材料不能进入显式消耗品池");
        RejectTemplate(template => template with { EquipmentSlot = 12 }, "装备槽不匹配拒绝");
        RejectTemplate(template => template with { Durability = 1 }, "耐久不匹配拒绝");
        RejectTemplate(template => template with { StackLimit = 1 }, "堆叠上限不匹配拒绝");
        RejectTemplate(template => template with { Expiration = new(1, 0) }, "绝对期限拒绝");
        RejectTemplate(template => template with { Expiration = new(0, 1) }, "相对期限拒绝");
        RejectTemplate(template => template with { Expiration = new(-1, -1) }, "异常期限不当作永久");
        RejectTemplate(template => template with { AttachType = "[sealing]" }, "封装模板拒绝");
        RejectTemplate(template => template with { StackableType = "[waste]" }, "类型参数缺失拒绝");
        RejectTemplate(template => template with { StackableType = "[waste] 5 1" }, "额外类型参数拒绝");
        RejectTemplate(template => template with { StackableType = "[waste] 9" }, "错误类型参数拒绝");
        RejectTemplate(template => template with { StackableType = "[recipe] 1" }, "特殊堆叠类型拒绝");
        RejectData(() => Create(valid, id => id == 1004 ? null! : Template(id)), "最后模板缺失不能返回前半池");

        // 此纯规则没有角色/地图状态；可用职业与等级不是掉落资格，正式范围由调用方核对。
        var callerScoped = Create(valid with { CommonChampionItems = [new(9999, 1)] },
            id => Template(id) with { Jobs = 1 << 15, MinimumLevel = 99 });
        Check(DungeonExplicitDropPolicy.Select(callerScoped, 0, 0) is { Template.Jobs: 32768, Template.MinimumLevel: 99 },
            "不借模板使用条件筛掉奖励，也不读取冠军池冒充普通生成资格");
        RejectRange(() => DungeonExplicitDropPolicy.Select(pool, -1, 0), "负触发值拒绝");
        RejectRange(() => DungeonExplicitDropPolicy.Select(pool, 10000, 0), "触发上界拒绝");
        RejectRange(() => DungeonExplicitDropPolicy.Select(pool, int.MaxValue, 0), "过大触发值不取模");
        RejectRange(() => DungeonExplicitDropPolicy.Select(pool, 0, -1), "负选择值拒绝");
        RejectRange(() => DungeonExplicitDropPolicy.Select(pool, 0, 350), "选择上界拒绝");
        RejectRange(() => DungeonExplicitDropPolicy.Select(pool, 9999, 350), "未触发也不能掩盖非法选择值");
        RejectRange(() => DungeonExplicitDropPolicy.Select(pool, 0, int.MaxValue), "过大选择值不取模");
        Console.WriteLine($"PASS {checks} 本服显式掉落纯规则断言");

        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL 本服显式掉落: " + name);
        }
        void RejectData(Action action, string name) => Reject<InvalidDataException>(action, name);
        void RejectRange(Action action, string name) => Reject<ArgumentOutOfRangeException>(action, name);
        void Reject<TException>(Action action, string name) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                Check(true, name);
                return;
            }
            throw new InvalidOperationException("FAIL 本服显式掉落未拒绝: " + name);
        }
        void RejectTemplate(Func<ItemTemplate, ItemTemplate> change, string name) =>
            RejectData(() => Create(valid, id => id == 1004 ? change(Template(id)) : Template(id)), name);
    }

    // 主runner传入共享Catalog；本用例不重新打开PVF或建立另一份资源目录。
    public static void RunResources(DungeonMonsterDropCatalog drops, ItemCatalog items)
    {
        foreach (int monsterId in new[] { 1, 2 })
        {
            var declaration = drops.Get(monsterId);
            var pool = DungeonExplicitDropPolicy.CreatePool("monster/monster.lst", declaration, items.Get);
            if (pool.Entries.Length != 3 || !pool.Entries.Select(entry => entry.Template.Id).SequenceEqual(new[] { 1000, 1047, 1004 }) ||
                !pool.Entries.Select(entry => entry.Weight).SequenceEqual(new[] { 50, 200, 100 }))
                throw new InvalidOperationException("FAIL 本服显式掉落匹配资源池: " + monsterId);
            foreach (var entry in pool.Entries)
            {
                if (!ReferenceEquals(entry.Template, items.Get(entry.Template.Id)))
                    throw new InvalidOperationException("FAIL 本服显式掉落未共享真实模板: " + entry.Template.Id);
            }
        }
        if (drops.Get(107000903).Items is not null || drops.Get(107000906).Items is not null)
            throw new InvalidOperationException("FAIL 本服显式掉落Boss房资源边界变化");
        Console.WriteLine("PASS 本服显式掉落两怪物三模板匹配资源断言");
    }

    static DungeonExplicitDropPool Create(DungeonMonsterDropDeclaration declaration, Func<int, ItemTemplate> resolve) =>
        DungeonExplicitDropPolicy.CreatePool("monster/monster.lst", declaration, resolve);

    static DungeonMonsterDropEntry[] Rows() => [new(1000, 50), new(1047, 200), new(1004, 100)];

    static ItemTemplate Template(int id) => new(id, ItemKind.Consumable, 0, 1, 0, 1000, 20, 1, 2,
        "`[free]`", ushort.MaxValue, 0, "合成永久消耗品", id == 1047 ? "[waste] 1" : "[waste] 5");
}
