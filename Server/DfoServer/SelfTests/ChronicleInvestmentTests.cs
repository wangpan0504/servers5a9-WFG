using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ChronicleInvestmentTests
{
    public static void RunCore()
    {
        int before = Count;
        var strings = new List<string>();
        var documents = new Dictionary<string, PvfTokenDocument>();
        string Resolve(int id) => strings[id];
        PvfToken Text(string value, byte type = 6) { strings.Add(value); return new(type, strings.Count - 1); }
        PvfToken Tag(string value) => Text("[" + value + "]", 3);
        PvfToken N(int value) => new(0, value);
        PvfTokenDocument Doc(params PvfToken[] tokens) => new(tokens, Resolve);
        PvfToken[] Skill(int number, string job = "[swordman]", string field = "[level]", int amount = -12) =>
            [Tag("skill"), N(number), Text(job), N(46), Text("[dungeon type]"), Text(field), N(0), Text("%"), N(amount), Tag("/skill")];
        PvfToken[] Group(int job, int grow, string slot, params PvfToken[] body) =>
            [Tag("check"), N(job), N(grow), Text(slot), .. body, Tag("/check")];
        PvfTokenDocument Table(params PvfToken[] groups) => Doc([Tag("3choro enchant"), .. groups, Tag("/3choro enchant")]);
        PvfTokenDocument Child(int type, string import = "parent.stk", string action = "[3rd chronicle ability enchant]") =>
            Doc(Tag("import script"), Text(import), Tag("action type"), Text(action), Tag("/action type"),
                Tag("stackable type"), Text("[etc]"), N(1), Tag("type"), N(type));
        CharacterInvestmentMetadataCatalog Catalog() => new(path => documents.TryGetValue(path, out var doc) ? doc :
            throw new FileNotFoundException(path), Resolve, new Dictionary<int, string>(),
            new Dictionary<int, string> { [1254] = "effect.stk", [1255] = "effect.stk", [1256] = "effect.stk", [1257] = "effect.stk",
                [1299] = "red.stk", [1300] = "blue.stk", [1301] = "green.stk", [1400] = "red.stk" });
        documents["stackable/red.stk"] = Child(0);
        documents["stackable/blue.stk"] = Child(1);
        documents["stackable/green.stk"] = Child(2);
        var validTable = Table([.. Group(0, 1, "weapon", [.. Skill(0), .. Skill(1)]),
            Tag("Monitor"), N(13), N(1), N(2), Text("coat"), Text("pants"), .. Skill(0), Tag("/Monitor")]);
        documents["stackable/parent.stk"] = validTable;
        var catalog = Catalog();
        foreach (int id in new[] { 1299, 1300, 1301 })
            That(catalog.ReadChronicle(id, 0, 1, 12, 0) is { Valid: true } r && r.Type == id - 1299,
                "气息三type、selector0与负效果量合法：" + id);
        That(catalog.ReadChronicle(1299, 13, 1, 14, 0).Valid && catalog.ReadChronicle(1299, 13, 1, 16, 0).Valid,
            "Monitor展开多个规范部位");
        That(!catalog.ReadChronicle(1299, 0, 1, 14, 0).Valid && !catalog.ReadChronicle(1299, 0, 1, 12, 2).Valid,
            "部位与selector精确匹配");
        PvfTokenDocument Effect(int type) => Doc(Tag("import script"), Text("parent.stk"),
            Tag("stackable type"), Text("[etc]"), N(1), Tag("type"), N(type));
        for (byte type = 0; type < 3; type++)
        {
            documents["stackable/effect.stk"] = Effect(type);
            var versioned = Catalog();
            int id = 1254 + type;
            Reject<InvalidDataException>(() => versioned.ReadChronicle(id, 0, 1, 12, 0));
            That(versioned.ReadInstalledChronicle(id, 0, 1, 12, 0) is { Valid: true } effect && effect.Type == type,
                "v2.3原效果模板无需消耗动作，旧缓存失败不污染新入口：" + type);
            Reject<InvalidDataException>(() => versioned.ReadChronicle(id, 0, 1, 12, 0));
            That(!versioned.ReadInstalledChronicle(id, 0, 1, 25, 0).Valid &&
                !versioned.ReadInstalledChronicle(id, 0, 1, 12, 2).Valid, "新入口仍要求实际部位与选项键");
            var newFirst = Catalog();
            That(newFirst.ReadInstalledChronicle(id, 0, 1, 12, 0).Valid, "先读取新入口仍为合法效果");
            Reject<InvalidDataException>(() => newFirst.ReadChronicle(id, 0, 1, 12, 0));
        }
        foreach (var invalid in new[] { Effect(1), Effect(3), Child(0, action: "[other]"),
            Doc(Tag("import script"), Text("parent.stk"), Tag("type"), N(0)),
            Doc(Tag("import script"), Text("parent.stk"), Tag("stackable type"), Text("[etc]"), N(1),
                Tag("type"), N(0), Tag("type"), N(0)),
            Doc(Tag("import script"), Text("parent.stk"), Tag("stackable type"), Text("[etc]"), N(1), Tag("type"), N(0),
                Tag("action type"), Text("[3rd chronicle ability enchant]"), Tag("/action type"),
                Tag("action type"), Text("[3rd chronicle ability enchant]"), Tag("/action type")) })
        {
            documents["stackable/effect.stk"] = invalid;
            Reject<InvalidDataException>(() => Catalog().ReadInstalledChronicle(1254, 0, 1, 12, 0));
        }
        documents["stackable/effect.stk"] = Effect(0);
        Reject<InvalidDataException>(() => Catalog().ReadInstalledChronicle(1257, 0, 1, 12, 0));
        That(catalog.ReadInstalledChronicle(1299, 0, 1, 12, 0).Valid &&
            catalog.ReadInstalledChronicle(1300, 0, 1, 12, 0).Valid &&
            catalog.ReadInstalledChronicle(1301, 0, 1, 12, 0).Valid, "v2.3保留旧合法消耗模板解释");
        documents["stackable/parent.stk"] = Table([.. Group(0, 1, "weapon", Skill(0)),
            .. Group(3, 2, "coat", [.. Skill(6), .. Skill(6, amount: 20)]),
            .. Group(4, 1, "weapon", Skill(0, "[at preist]"))]);
        documents["stackable/effect.stk"] = Effect(2);
        var local = Catalog();
        That(local.ReadChronicle(1301, 0, 1, 12, 0).Valid &&
            local.ReadChronicle(1301, 3, 2, 14, 6) is { Valid: false, Reason: "ambiguous_key" } &&
            local.ReadChronicle(1301, 4, 1, 12, 0) is { Valid: false, Reason: "unsupported_effect" },
            "无关绿重键和错误职业拼写不污染唯一目标，不first/last或修别名");
        That(local.ReadInstalledChronicle(1256, 0, 1, 12, 0).Valid &&
            local.ReadInstalledChronicle(1256, 3, 2, 14, 6) is { Valid: false, Reason: "ambiguous_key" } &&
            local.ReadInstalledChronicle(1256, 4, 1, 12, 0) is { Valid: false, Reason: "unsupported_effect" },
            "v2.3仅放宽原效果动作存在性，仍按选中键隔离歧义与未知效果");
        foreach (var invalid in new[]
        {
            Table(Group(256, 1, "weapon", Skill(0))), Table(Group(0, 256, "weapon", Skill(0))),
            Table(Group(0, 1, "weapon", Skill(256))), Table(Group(0, 1, "unknown", Skill(0))),
            Table([Tag("Monitor"), N(0), N(1), N(2), Text("weapon"), .. Skill(0), Tag("/Monitor")]),
            Doc([Tag("3choro enchant"), Tag("check"), N(0), N(1), Text("weapon"), .. Skill(0)])
        })
        {
            documents["stackable/parent.stk"] = invalid;
            Reject<InvalidDataException>(() => Catalog().ReadChronicle(1299, 0, 1, 12, 0));
        }
        documents["stackable/parent.stk"] = validTable;
        foreach (var invalid in new[] { Child(3), Child(0, "red.stk"), Child(0, "../outside.stk"), Child(0, "missing.stk"),
            Child(0, action: "[other]"), Doc(Tag("import script"), Text("parent.stk"), Tag("type"), N(0)) })
        {
            documents["stackable/red.stk"] = invalid;
            Reject<InvalidDataException>(() => Catalog().ReadChronicle(1299, 0, 1, 12, 0));
        }
        documents["stackable/red.stk"] = Child(0);
        catalog = Catalog();
        var weapon = new InventoryItem(1, new(InventoryListType.Equipment, 12), new(100, ItemKind.Equipment, 1) { Durability = 30 });
        var template = new ItemTemplate(100, ItemKind.Equipment, 12, 95, 30, 1, 0, 4, 1, "[free]", ushort.MaxValue, 0, "fixture");
        ItemTemplate Get(int id) => id == 100 ? template : throw new KeyNotFoundException();
        Character Character(InventoryItem item) => new(1, 0, "气息测试", 7, default!, null)
            { Level = 95, Job = 15, GrowType = 4, Inventory = new([item]) };
        ChronicleInvestmentMetadata Read(ChronicleOption option) => catalog.ReadChronicle(option.Id, option.Job, option.GrowType, option.EquipmentType, option.Number);
        CharacterInvestmentResult Calculate(CharacterInvestmentSnapshot snapshot, bool current = true) =>
            CharacterInvestmentScore.Calculate(snapshot, Get, (_, _, _, _) => false, _ => false, _ => null,
                _ => null, _ => null, _ => throw new KeyNotFoundException(), (_, _) => false, current ? Read : null);
        CharacterInvestmentResult Score(ChronicleOption a, ChronicleOption b = default, bool current = true) =>
            Calculate(CharacterInvestmentScore.Capture(Character(weapon with { Core = weapon.Core with { Chronicle0 = a, Chronicle1 = b } }), 1000), current);
        var red0 = new ChronicleOption(1299, 0, 1, 12, 0);
        That(Score(red0) is { Score: 3740, Partial: false, Profile: CharacterInvestmentScore.ChronicleProfile },
            "v2.1真实冻结来源增加40，持久职业不强制匹配当前角色");
        That(Score(red0, current: false) is { Score: 3700, Partial: true, Profile: CharacterInvestmentScore.Profile },
            "旧v2保留未解释状态与旧分数");
        That(Score(default) is { Score: 3700, Partial: false } && Score(default).Parts.All(p =>
            p.RuleVersion == (p.Module == "equipment" ? EquipmentInvestmentScore.Profile : CharacterInvestmentScore.ChronicleProfile)),
            "新版本所有模块明细显式版本，合法空不Partial");
        That(Score(red0, red0 with { Number = 1 }).Score == 3780, "同type不同selector可分别计入两个wire槽");
        That(Score(red0, red0 with { Id = 1300 }).Score == 3780, "不同type同selector独立来源");
        That(Score(red0, red0 with { Id = 1400 }) is { Score: 3740, Partial: true }, "不同Id不能绕过type/Number去重");
        That(Score(red0 with { EquipmentType = 14 }) is { Score: 3700, Partial: true }, "错误宿主只拒绝该选项");
        That(Score(red0 with { Id = 0 }) is { Score: 3700, Partial: true }, "空Id残留键不是合法空选项");
        That(Score(red0 with { Id = -1 }) is { Score: 3700, Partial: true }, "负Id不得成为已安装来源");
        var expired = Character(weapon with { Core = weapon.Core with { Chronicle0 = red0, ExpireTime = 1000 } });
        That(Calculate(CharacterInvestmentScore.Capture(expired, 1000)).Score == 0, "宿主到期同时排除气息");
        var unknown = Character(weapon with { Core = weapon.Core with { Chronicle0 = red0, EmancipateLevel = 1 } });
        That(Calculate(CharacterInvestmentScore.Capture(unknown, 1000)) is { Score: 3740, Partial: true }, "已解释气息不隐藏未知Emancipate");
        using (var fixture = new MailboxPersistenceTests.Fixture())
        {
            var created = fixture.Repository.Create(fixture.SenderOwner, new("ChrScoreHero", fixture.Sender.Position, null)
            { Level = 95, Inventory = new([weapon with { Uid = 0, Core = weapon.Core with { Chronicle0 = red0, Chronicle1 = red0 with { Id = 1300 } } }]) });
            fixture.SenderOwner.Dispose();
            string beforeQuery = fixture.Snapshot();
            string response = DevelopmentGm.QueryCharacterScore(fixture.Repository, fixture.Owners, null,
                DevelopmentRequest.Parse("character-score ChrScoreHero", "sender"), snapshot => Calculate(snapshot));
            using var json = JsonDocument.Parse(response[3..]);
            var root = json.RootElement;
            That(root.GetProperty("Score").GetInt32() == 3780 && root.GetProperty("Profile").GetString() == CharacterInvestmentScore.ChronicleProfile &&
                root.GetProperty("Status").GetString() == "Complete" && root.GetProperty("CharacterId").GetUInt16() == created.Id &&
                root.GetProperty("CharacterRevision").GetInt64() == created.Revision, "真实SQLite气息字段经GM只读查询，新版本与明细完整");
            That(fixture.Snapshot() == beforeQuery, "气息评分不持久化写入资产");
        }
        Console.WriteLine($"ChronicleInvestmentTests: {Count - before} passed");
    }
    public static void RunResource(ItemResources resources)
    {
        int before = Count;
        const int gearId = 100300119;
        var metadata = resources.CharacterInvestmentMetadata;
        var scorer = new CharacterInvestmentService(resources.Items, metadata, resources.CollectBoxes, resources.RandomOptions);
        var enchant = new InventoryChronicleEnchantService(resources.Items, resources.ChronicleEnchants, resources.Disjoint,
            resources.AmplifyUpgrades, resources.GoldLimits, resources.EpicPieces);
        for (byte type = 0; type < 3; type++)
        {
            using var f = new MailboxPersistenceTests.Fixture();
            var target = InventoryRewardGrantService.Create(resources.Items.Get(gearId));
            var role = f.Repository.Create(f.SenderOwner, new("ChrScore", f.Sender.Position, null)
            {
                Job = 12, GrowType = 0x11, Level = 80,
                Inventory = new([new(0, new(InventoryListType.Main, 9), target),
                    new(0, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(resources.Items.Get(1299 + type)))])
            });
            var request = new ChronicleEnchantRequest(65, 1299 + type, new(InventoryListType.Main, 9), gearId, 0);
            var planned = enchant.Plan(role, request, out var receipt);
            That(planned.Accepted && receipt is { Succeeded: true }, "真实三色变体赋予成功：" + type);
            role = InventoryService.Commit(f.Repository, f.SenderOwner, role, planned);
            var installed = role.Inventory.Find(request.Target)!;
            That(installed.Core.Chronicle0 == new ChronicleOption(1254 + type, 12, 1, 19, 0) &&
                installed.Core.Value == target.Value && metadata.ReadInstalledChronicle(1254 + type, 12, 1, 19, 0) is { Valid: true },
                "实际效果Id、Grow低4位和原seed保存，读取原色目录而非消耗变体：" + type);
            Reject<InvalidDataException>(() => metadata.ReadChronicle(1254 + type, 12, 1, 19, 0),
                "旧v2.1/v2.2继续拒绝无动作原效果模板，不随v2.3静默改变复算");
            That(scorer.Calculate(CharacterInvestmentScore.Capture(role, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                .Parts.Where(part => part.Module is "chronicle" or "quality").Sum(part => part.Score) == 0,
                "背包内成功赋予尚不计穿戴建设分");
            var move = InventoryMoveService.Plan(role, new(request.Target, gearId, 1,
                new(InventoryListType.Equipment, 19), 0, 0, 0, 0, 0), resources.Items, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            That(move.Accepted, "真实宿主可通过既有移动资格穿戴");
            role = InventoryService.Commit(f.Repository, f.SenderOwner, role, move);
            f.SenderOwner.Dispose();
            string original = f.Snapshot();
            string json = DevelopmentGm.QueryCharacterScore(f.Repository, f.Owners, null,
                DevelopmentRequest.Parse("character-score ChrScore", "sender"), scorer.Calculate);
            using var result = JsonDocument.Parse(json[3..]);
            var root = result.RootElement;
            var parts = root.GetProperty("Parts").EnumerateArray().ToArray();
            That(root.GetProperty("Profile").GetString() == CharacterInvestmentScore.CompleteWornProfile &&
                parts.Where(part => part.GetProperty("Module").GetString() == "chronicle").Sum(part => part.GetProperty("Score").GetInt32()) == 40 &&
                parts.Where(part => part.GetProperty("Module").GetString() == "quality").Sum(part => part.GetProperty("Score").GetInt32()) == 100,
                "实际穿戴后的默认v2.4保留每气息40与顶档seed100，不重复部位权");
            That(f.Snapshot() == original, "实际原色目录评分不写SQLite或重roll种子");
        }
        That(resources.ChronicleEnchants.Get(1257) is null, "当前1257无动作、类型或导入表，不伪称可安装黄色气息");
        Reject<InvalidDataException>(() => metadata.ReadInstalledChronicle(1257, 12, 1, 19, 0), "无动作黄色模板维持显式不支持");
        That(metadata.ReadInstalledChronicle(1254, 12, 1, 25, 0) is { Valid: false, Reason: "missing_key" } &&
            metadata.ReadInstalledChronicle(1256, 3, 2, 14, 6) is { Valid: false, Reason: "ambiguous_key" },
            "当前目录无耳环键且绿色同编号多段仍歧义，不静默扩旧v2.1解释域");
        Console.WriteLine($"PASS {Count - before} 真实三色赋予、穿戴、v2.3只读评分与目录边界断言");
    }

}
