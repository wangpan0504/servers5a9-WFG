using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestModuleSchemaTests
{
    public static void Run()
    {
        int before = Count;
        That(QuestTargetTypes.All.Count(row => row.NativeType is not null) == 69 &&
            QuestTargetTypes.All.Where(row => row.NativeType is not null).Select(row => row.NativeType).Distinct().Count() == 69,
            "69个已赋值类型保持唯一映射，不按50/51/61空位补值");
        foreach (byte value in Enumerable.Range(0, 72).Select(value => (byte)value))
            That(QuestTargetTypes.IsNative(value) == (value is not (50 or 51 or 61)), "原生类型边界：" + value);
        That(QuestTargetTypes.Find("[self disjoint item]")?.NativeType == 66 &&
            QuestTargetTypes.Find("[equipped item]")?.NativeType == 17 &&
            QuestTargetTypes.Find("[mission roulette]") is { NativeType: null, Evidence: QuestTargetEvidence.Unassigned },
            "本人分解与穿戴不混用，轮盘不伪造独立编号");
        That(QuestPackets.Finish(10021, 66, 0, [], 0, []).SequenceEqual(new byte[] { 1, 37, 39, 66, 0, 0, 0, 0, 0, 0 }),
            "ACK34独立字节预期验证类型66");
        ReadDeclarations();
        var basis = QuestEligibilityTests.Quest(100);
        foreach (var (condition, subtype, data, expected) in new (string, int, int[], QuestParameterState)[]
        {
            ("[use item]", 0, [2, 2058, 1, 2059, 3], QuestParameterState.Parsed),
            ("[use item]", 1, [2, 2058, 1], QuestParameterState.Invalid),
            ("[hunt monster]", -1, [-4, 49, -1, -1, 100], QuestParameterState.Parsed),
            ("[hunt monster]", -1, [-4, 49, -1, -1, 100, 3, -1, 1, 1], QuestParameterState.Invalid),
            ("[hunt monster]", -1, [3, -1, 1, 1, -4, 12, -1, 1], QuestParameterState.Invalid),
            ("[hunt enemy]", 0, [3, -1, 1, 10, 2], QuestParameterState.Parsed),
            ("[condition under clear]", 0, [3, 2, 120], QuestParameterState.Parsed),
            ("[condition under clear]", 0, [3, 2], QuestParameterState.Invalid),
            ("[condition under clear]", 11, [3, -1, -1], QuestParameterState.Parsed),
            ("[condition under clear]", 19, [3, -1, 1], QuestParameterState.Unconfirmed),
            ("[raid phase clear]", 0, [0, 2, 5, 12], QuestParameterState.Parsed),
            ("[raid phase clear]", 0, [0, 5, -1], QuestParameterState.Unconfirmed),
            ("[use skill]", -1, [1, 10], QuestParameterState.Parsed),
            ("[use skill]", -1, [2, 10], QuestParameterState.Unconfirmed),
            ("[clear treasure dungeon]", 1, [2, 100, 101], QuestParameterState.Parsed),
            ("[clear treasure dungeon]", 1, [3, 100, 101], QuestParameterState.Unconfirmed),
            ("[user level]", 0, [10, 20], QuestParameterState.Parsed),
            ("[user level]", 0, [20, 10], QuestParameterState.Invalid)
        })
            That(QuestTargetSchema.Read(basis with { Condition = condition, SubType = subtype, IntData = data }).State == expected,
                "目标参数解释及待证状态：" + condition + "/" + subtype);
        ItemFacts(basis);
        Events(basis);
        Relations(basis);
        Nested(basis);
        DatabaseSchemaTests.RunQuestTargetsCore();
        Console.WriteLine($"PASS {Count - before} 全任务类型声明、参数、持有范围及真实事件纯规则断言");
    }

    static void ReadDeclarations()
    {
        var strings = new Dictionary<int, string> { [1] = "[level]", [2] = "[type]", [3] = "[meet npc]",
            [4] = "[sub type]", [5] = "[int data]", [6] = "[/int data]", [7] = "[npc index]" };
        PvfToken[] common = [new(3, 1), new(0, 1), new(0, 99), new(3, 2), new(6, 3), new(3, 7), new(0, 1)];
        StoryQuest Read(params PvfToken[] tail) => QuestDefinitionReader.Read(100, "fixture/native-declaration.qst", [.. common, .. tail], id => strings[id]);
        var missing = Read(new(3, 5), new(0, 1), new(3, 6));
        var negative = Read(new(3, 4), new(0, -1), new(3, 5), new(0, 1), new(3, 6));
        var zero = Read(new(3, 4), new(0, 0), new(3, 5), new(0, 1), new(3, 6));
        That(missing is { SubType: 0, DeclaredSubType: null, HasDeclaredSubType: false } &&
            negative is { SubType: -1, DeclaredSubType: -1 } && zero is { SubType: 0, DeclaredSubType: 0 },
            "省略、显式-1与显式0的来源没有被折叠");
        var repeated = Read(new(3, 4), new(0, -1), new(3, 5), new(0, 999), new(3, 6),
            new(3, 4), new(0, 0), new(3, 5), new(0, 1), new(3, 6));
        That(repeated.SubType == 0 && repeated.IntData.SequenceEqual([1]) && repeated.Definition!.Diagnostics.IsEmpty &&
            repeated.Definition.Fields.Count(field => field.Name == "int data") == 2,
            "重复目标标量和向量后值生效，原段仍完整保留");
    }

    static void ItemFacts(StoryQuest basis)
    {
        var items = new ItemCatalog(null!, null!, null!, 0);
        items.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [1000] = new(1000, ItemKind.Equipment, 12, 60, 10, 1, 0, 4, 60, "", 0, 0, ""),
            [1001] = new(1001, ItemKind.Equipment, 12, 60, 10, 1, 0, 5, 60, "", 0, 0, ""),
            [1002] = new(1002, ItemKind.Avatar, 3, 1, 0, 1, 0, 3, 1, "", 0, 0, "") { CloneAvatar = true }
        });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var inventory = new InventorySnapshot([new(1, new(InventoryListType.Main, 9), InventoryRewardGrantService.Create(items.Get(1000))),
            new(2, new(InventoryListType.Main, 10), InventoryRewardGrantService.Create(items.Get(1001)))]);
        var selector = basis with { Condition = "[check item]", SubType = -2, IntData = [-5, 1] };
        That(QuestItemProgress.Count(inventory, selector, -5, items, now) == 1 &&
            QuestItemProgress.Count(inventory, selector, -6, items, now) == 1, "负品级选择符精确匹配，不与阈值排除规则混合");
        That(!QuestItemProgress.AtLeastRarity(5, 3) && !QuestItemProgress.AtMostRarity(5, 6) &&
            QuestItemProgress.AtLeastRarity(5, -1), "显式品级阈值排除勇者，未声明品级不排除");
        var equipped = basis with { Condition = "[equipped item]", SubType = 0, IntData = [3, 50],
            Definition = new([new("string data", [new(6, 0, "weapon")], [])]) };
        That(!QuestItemProgress.Equipped(inventory, equipped, items, now) && QuestItemProgress.Equipped(
            inventory with { Items = [inventory.Items[0] with { Position = new(InventoryListType.Equipment, 12) }] }, equipped, items, now),
            "穿戴目标只读真正穿戴槽，不把背包中的装备当成已穿戴");
        var catalog = new QuestCatalog(new Dictionary<int, StoryQuest> { [100] = selector }, items: items);
        That(QuestTargetRules.Supports(selector, catalog) && QuestTargetSchema.Read(selector with { SubType = 20 }).State == QuestParameterState.Parsed,
            "check item的sub是持有范围，不借用seeking/sub20的通关取物尾项");
        var active = new ActiveMainQuest(0, 100, 1) { AcceptanceId = Guid.NewGuid() };
        var character = new Character(1, 0, "Holding", 1, new(38, 2, 450, 240), null)
            { Inventory = inventory, ActiveMainQuests = [active] };
        That(QuestLifecyclePlan.SetCondition(character, selector, 0, catalog).ActiveAfter.Single().ConditionState == 0,
            "check item的33实际重算持有，不要求先移动物品来触发进度");
        Reject<InvalidDataException>(() => QuestLifecyclePlan.SetCondition(character with { Inventory = InventorySnapshot.Empty }, selector, 0, catalog),
            "持有33不能自证未持有的物品或品级");
    }

    static void Events(StoryQuest basis)
    {
        var use = basis with { Condition = "[use item]", SubType = 0, IntData = [1, 2058, 1] };
        var get = basis with { Id = 101, Condition = "[get item check index]", IntData = [2058] };
        var catalog = new QuestCatalog(new[] { use, get }.ToDictionary(row => row.Id));
        ActiveMainQuest[] active = [new(0, 100, 1) { AcceptanceId = Guid.NewGuid() }, new(1, 101, 1) { AcceptanceId = Guid.NewGuid() }];
        var inventory = new InventorySnapshot([new(1, new(InventoryListType.Main, 65), new(2058, ItemKind.Consumable, 1))]);
        var move = new InventoryPlan(inventory, inventory with { Items = [inventory.Items[0] with { Position = new(InventoryListType.Main, 66) }] }, "move");
        That(QuestEventProgress.Inventory(active, catalog, move).SequenceEqual(active), "移动槽位不伪造获取历史");
        var consume = new InventoryPlan(inventory, InventorySnapshot.Empty, "use") { UsedItems = [new(2058, 1)] };
        That(QuestEventProgress.Inventory(active, catalog, consume)[0].ConditionState == 0 &&
            QuestEventProgress.Inventory(active, catalog, new(inventory, inventory, "free_use") { UsedItems = [new(2058, 1)], PreserveMaterials = true }).SequenceEqual(active),
            "实际使用意图推进目标，免耗练习不生成任务事件");
        That(QuestEventProgress.Inventory(active, catalog, new(InventorySnapshot.Empty, inventory, "grant"))[1].ConditionState == 0,
            "真实新增物品而非当前持有历史推进获取目标");
        var cube = get with { IntData = [3037] };
        var cubeCatalog = new QuestCatalog(new() { [101] = cube });
        var wallet = DfoServer.Game.Currency.CurrencyWallet.Set(InventorySnapshot.Empty, DfoServer.Game.Currency.CurrencyKind.ClearCube, 1);
        That(QuestEventProgress.Inventory([active[1]], cubeCatalog, new(InventorySnapshot.Empty, wallet, "grant_cube"))[0].ConditionState == 0 &&
            QuestEventProgress.Inventory([active[1]], cubeCatalog, new(wallet, wallet, "query_cube"))[0].ConditionState == 1,
            "小晶块等已有物品编号的钱包净增加可推进，不把历史余额当作新获取");
        var uiQuest = basis with { Id = 102, Condition = "[ui click]", IntData = [42] };
        var coinQuest = basis with { Id = 103, Condition = "[use fortune coin]", IntData = [2058, 1] };
        var treasureQuest = basis with { Id = 104, Condition = "[clear treasure dungeon]", SubType = 1, IntData = [2, 100, 101] };
        var powerWinQuest = basis with { Id = 105, Condition = "[powerwar win]", IntData = [] };
        var assaultQuest = basis with { Id = 106, Condition = "[assault count]", IntData = [] };
        var extendedCatalog = new QuestCatalog(new[] { uiQuest, coinQuest, treasureQuest, powerWinQuest, assaultQuest }.ToDictionary(row => row.Id));
        var extendedActive = extendedCatalog.All.Select((q, idx) => new ActiveMainQuest((byte)idx, (ushort)q.Id, 1) { AcceptanceId = Guid.NewGuid() }).ToArray();
        That(extendedCatalog.All.All(q => QuestTargetRules.Supports(q, extendedCatalog)),
            "UI点击、幸运币、宝藏地下城、势力战胜利与突袭次数目标均已接入");
        That(QuestEventProgress.UiClicked(extendedActive, extendedCatalog, 42)[0].ConditionState == 0 &&
            QuestEventProgress.FortuneCoinUsed(extendedActive, extendedCatalog, 2058)[1].ConditionState == 0 &&
            QuestEventProgress.TreasureDungeonCleared(extendedActive, extendedCatalog, 101)[2].ConditionState == 0 &&
            QuestEventProgress.PowerWarRecorded(extendedActive, extendedCatalog, true, 0)[3].ConditionState == 0 &&
            QuestEventProgress.AssaultCleared(extendedActive, extendedCatalog)[4].ConditionState == 0,
            "扩展事件目标按各自领域触发推进条件");
    }

    static void Relations(StoryQuest basis)
    {
        var first = basis with { Id = 101, MainQuest = 100 };
        var second = basis with { Id = 102, MainQuest = 100 };
        foreach (int subtype in new[] { -1, 1, 3 })
        {
            var parent = basis with { Condition = "[quest clear]", SubType = subtype, IntData = [101, 102] };
            var catalog = new QuestCatalog(new[] { parent, first, second }.ToDictionary(row => row.Id));
            That(QuestTargetRules.Supports(parent, catalog) && QuestRelationProgress.DialogueChild(second, catalog),
                "原生列表父任务不再套用sub2的顺序门禁");
            That(QuestRelationProgress.ListScalar(parent, []) == 1 &&
                QuestRelationProgress.ListScalar(parent, [101]) == (subtype == -1 ? 1u : 0u) &&
                QuestRelationProgress.ListScalar(parent, [101, 102]) == 0, "列表全部与任一条件按真实完成位区分");
            That(!QuestRelationProgress.ListParent(parent with { IntData = [101, 29999] }, catalog),
                "服务端不伪造缺失子任务的完成事实");
        }
    }

    static void Nested(StoryQuest basis)
    {
        var check = new QuestField("count", [new(0, 5, null), new(6, 0, "excess")], []);
        var field = new QuestField("enemy action check", [], [new("target type", [new(6, 0, "monster")], []), check]);
        var quest = basis with { Condition = "[enemy action check]", Definition = new([field]), IntData = [] };
        That(QuestTargetSchema.Read(quest).Nested.Single().Checks.Single().Comparison == QuestComparison.Greater,
            "嵌套excess严格大于，不因资源可解析就改成大于等于");
        var under = field with { Children = [field.Children[0], check with { Values = [new(0, 5, null), new(6, 0, "under")] }] };
        That(QuestTargetSchema.Read(quest with { Definition = new([under]) }).Nested.Single().Checks.Single().Comparison == QuestComparison.Less,
            "嵌套under严格小于");
        That(QuestTargetSchema.Read(quest with { Definition = new([field with { Children = [.. field.Children, new("future condition", [], [])] }]) })
            .State == QuestParameterState.Unconfirmed, "未知嵌套条件保留待证，不丢掉条件后标记已解析");
        var question = basis with { Condition = "[question]", Npc = 7, IntData = [99],
            Definition = new([new("question info", [new(6, 0, "question"), new(6, 0, "description"), new(6, 0, "answer")], [])]) };
        That(QuestQuestionProgress.TryCount(question, out int answers) && answers == 1 &&
            !QuestQuestionProgress.TryCount(question with { Npc = -1 }, out _),
            "问题独立NPC字段决定上下文，不要求与int data样本相同，也不能由int data代替缺失NPC");
    }
}
