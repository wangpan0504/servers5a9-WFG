using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using System.Text.Json;
using DfoServer.Persistence;
using DfoServer.Game.Currency;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BeastDungeonEntryTests
{
    public static void RunCore()
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "ServerS5A9-beast-entry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var auth = new DfoServer.Game.Auth.AuthenticationService(new AuthenticationRepository(Path.Combine(root, "game.db")));
            auth.Create("beast-entry", "fixture-password");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("beast-entry", () => { });
            var character = repository.Create(owner, new NewCharacter("Beast", new(38, 1, 450, 234), null)
                { DungeonState = CharacterDungeonState.NewOrdinary, Inventory = InventorySnapshot.Empty });
            int day = BeastDungeonRotation.ServiceDay(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
            int dungeon = BeastDungeonRotation.Pair(day).First;
            That(repository.LoadBeastDungeonUsed(owner, character.Id, day, dungeon) == 0, "新角色当日魔兽次数为零");
            That(repository.CommitBeastDungeonEntry(owner, character.Id, day, dungeon) == 1 &&
                repository.CommitBeastDungeonEntry(owner, character.Id, day, dungeon) == 2 &&
                repository.CommitBeastDungeonEntry(owner, character.Id, day, dungeon) == -1 &&
                repository.LoadBeastDungeonUsed(owner, character.Id, day, dungeon) == 2,
                "同一服务日同一图只接受两次入场");
            That(repository.LoadBeastDungeonUsed(owner, character.Id, day + 1, dungeon) == 0 &&
                repository.CommitBeastDungeonEntry(owner, character.Id, day, 309) == -1,
                "换日清零，非当日图拒绝");
            int gateway = dungeon - BeastDungeonRotation.FirstDungeonId + BeastDungeonRotation.FirstGatewayDungeonId;
            That(repository.LoadBeastDungeonUsed(owner, character.Id, day, gateway) == 2 &&
                repository.CommitBeastDungeonEntry(owner, character.Id, day, gateway) == -1,
                "原入口已用两次时专属入口共享额度，不能换图号绕过限额");
            That(repository.ResetBeastDungeonDay(owner, character.Id, day) >= 1 &&
                repository.LoadBeastDungeonUsed(owner, character.Id, day, dungeon) == 0 &&
                repository.CommitBeastDungeonEntry(owner, character.Id, day, dungeon) == 1,
                "重置当日魔兽次数后可以再次进入");
            That(Enumerable.Range(0, 6).Select(BeastDungeonRotation.StageRewardItem).SequenceEqual(
                    new int?[] { 10303240, 10303242, 10303243, 10303244, 10303245, 10303246 }) &&
                BeastDungeonRotation.StageRewardItem(-1) is null && BeastDungeonRotation.StageRewardItem(6) is null &&
                !BeastDungeonRotation.StageRewardItems.Contains(10303241),
                "六个阶段罐按整合脚本编号发放，10303241是首饰随机罐");
            That(BeastBalance.Normal("[MAX_HP]\n[NORMAL]\n5.00 5.00 5.00\n[/MAX_HP]\n[ATTACK]\n[NORMAL]\n2.30 0.00 0.00\n[/ATTACK]\n") == (5, 2.3),
                "魔兽平衡表普通档第一列是生命5倍、攻击2.3倍");
            That(BeastDungeonRotation.TimedStage == 3 && BeastDungeonRotation.LimitSeconds == 180,
                "第三阶段起计时180秒");
            var atomic = repository.Create(owner, new NewCharacter("AtomicBeast", new(38, 1, 450, 234), null)
            {
                DungeonState = CharacterDungeonState.NewOrdinary, CompletedQuests = [5927],
                Inventory = InventorySnapshot.Empty with { Gold = 1000 }
            });
            var charge = new DungeonEntryCharge(CurrencyService.Spend(atomic.Inventory, CurrencyKind.Gold, 100).Plan,
                0, new(atomic.Id, 0, 1, Guid.NewGuid(), 1), 59453)
                { DungeonId = dungeon, Beast = new(day, 0) };
            void Sql(string sql)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = Path.Combine(root, "game.db"), Pooling = false, ForeignKeys = true }.ToString());
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            Sql("CREATE TRIGGER fail_beast_entry BEFORE INSERT ON character_beast_dungeon_entries BEGIN SELECT RAISE(ABORT,'test rollback'); END");
            Reject<SqliteException>(() => repository.CommitDungeonEntry(owner, atomic, 500, charge), "魔兽扣次失败必须使整笔入场失败");
            var failed = repository.LoadCharacter(owner, atomic.Slot);
            That(failed.Revision == atomic.Revision && failed.Inventory.Gold == 1000 && failed.DungeonState == atomic.DungeonState &&
                repository.LoadBeastDungeonUsed(owner, atomic.Id, day, dungeon) == 0,
                "魔兽次数失败回滚费用、revision、对象键和尝试号");
            Sql("DROP TRIGGER fail_beast_entry");
            var entered = repository.CommitDungeonEntry(owner, atomic, 500, charge).Character;
            That(entered.Inventory.Gold == 900 && entered.DungeonState!.LastAttempt == 1 &&
                repository.LoadBeastDungeonUsed(owner, atomic.Id, day, dungeon) == 1,
                "魔兽费用、尝试号和次数一次原子提交");
            Reject<InvalidOperationException>(() => repository.CommitDungeonEntry(owner, entered, 501, charge with
                { Items = null, Identity = charge.Identity with { TaskAttempt = 2, InstanceId = Guid.NewGuid() } }),
                "过时魔兽次数规划不覆盖已用次数");
            Reject<InvalidOperationException>(() => repository.CommitDungeonEntry(owner, character, 500, charge with
                { Items = null, Identity = charge.Identity with { CharacterId = character.Id }, Beast = new(day, 1) }),
                "没有完成5927的角色不能借用魔兽入场事务");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"PASS {Count - checks} 魔兽每日入场次数断言");
    }

    public static void RunMaps(PvfLib.PvfArchive installed)
    {
        {
            int checks = Count;
            var rooms = DfoServer.GameWorld.Dungeons.DungeonRoomCatalog.Load(installed);
            var catalog = DfoServer.GameWorld.Dungeons.DungeonCatalog.Load(installed);
            var sources = Enumerable.Range(310,5).Select(catalog.Get).ToArray();
            int opened = 0;
            foreach (var source in sources.Where(source => source.BeastLayouts.All(layout => layout.ResourceError is null)))
                foreach (var map in source.BeastLayouts[0].Maps)
                    if (map.Path.StartsWith("map/devildom_beast/", StringComparison.OrdinalIgnoreCase) &&
                        rooms.Get(map, requirePassiveDefinitions: false, allowUnresolvedSpecialMetadata: true).Map.Id == map.Id)
                        opened++;
            int available = sources.Count(source => source.BeastLayouts.All(layout => layout.ResourceError is null));
            That(opened == available * 15,"资源完整的每个魔兽来源均打开十五房，不丢可用来源覆盖");
            if (available == 5)
                That(opened == 75, "正式 map.lst 里的七十五间魔兽房间都能打开");
            var layout = catalog.Get(310).BeastLayouts[0];
            if (sources[0].BeastLayouts.All(row => row.ResourceError is null))
                That(layout.NormalHpScale == 5 && layout.NormalAttackScale == 2.3,
                    "当前客户端包的平衡表普通档是生命5倍、攻击2.3倍");
            var beastCharacter = new DfoServer.Game.Characters.Character(1, 0, "Beast", 1,
                new(1, 0, 1, 1, 0), null) { Level = 90, CompletedQuests = [5927] };
            foreach (var source in sources)
            {
                That(source.BeastLayouts.Count == source.RawSections.Count(section => section.Tag == "beast dungeon" && section.Tokens.Count == 0) &&
                    source.BeastLayouts.Count == 3 && source.Mazes.Count > 0,
                    "逐源保留三个兽区块与普通路线：" + source.Id);
                foreach (var broken in source.BeastLayouts.Where(row => row.ResourceError is not null))
                    That(broken.Maps.Count == 0 &&
                        (broken.ResourceError!.Contains("魔兽地图未登记:",StringComparison.Ordinal) ||
                         broken.ResourceError.Contains("魔兽地图资源缺失:",StringComparison.Ordinal) ||
                         broken.ResourceError.Contains("魔兽平衡表资源缺失",StringComparison.Ordinal)),
                        "当前缺资源分支必须给出具体缺失诊断，不吞结构错误：" + source.Id + " / " + broken.ResourceError);
                if (source.BeastLayouts.Any(row => row.ResourceError is not null))
                    That(!DungeonEntryPlan.TryCreate(catalog,beastCharacter,(uint)source.Id,0,4,out _,out var beastReason,beastMode:true) &&
                        beastReason == "beast-layout-resource-unavailable","逐源缺依赖的魔兽模式精确拒绝：" + source.Id + " / " + beastReason);
                That(source.Mazes.Any(maze => maze.ResourceError is null && maze.QuestConnectionTokens.Count == 0),
                    "逐源必须保留真实普通无任务路线，不静默跳过普通准入断言：" + source.Id);
                That(DungeonEntryPlan.TryCreate(catalog,beastCharacter,(uint)source.Id,0,4,out var ordinary,out var ordinaryReason,draw:_=>0) &&
                    ordinary is { BeastMode: false } && ordinary.Maze.MapSpecification!.All(map => map.Kind != "Beast"),
                    "逐源既有普通路线仍合法：" + source.Id + " / " + ordinaryReason);
            }
            foreach (int gateway in Enumerable.Range(BeastDungeonRotation.FirstGatewayDungeonId, 5))
            {
                var source = catalog.Get(BeastDungeonRotation.SourceDungeonId(gateway));
                if (!catalog.ContainsDungeon(gateway))
                {
                    That(!DungeonEntryPlan.TryCreate(catalog,beastCharacter,(uint)gateway,0,2,out _,out var missingReason,beastMode:true) &&
                        missingReason == "dungeon-resource-unavailable",
                        "未登记网关必须按请求资源缺失精确拒绝：" + gateway + " / " + missingReason);
                    continue;
                }
                var gatewayDefinition = catalog.Get(gateway);
                if (source.BeastLayouts.Any(row => row.ResourceError is not null))
                {
                    bool accepted = DungeonEntryPlan.TryCreate(catalog,beastCharacter,(uint)gateway,0,2,out _,out var reason,beastMode:true);
                    That(!DungeonEntryPlan.GatewayMatches(gatewayDefinition,source) &&
                        !accepted &&
                        reason == "beast-gateway-source-unavailable","逐源缺依赖网关精确拒绝：" + gateway + " / " + reason);
                    continue;
                }
                That(DungeonEntryPlan.GatewayMatches(gatewayDefinition, source),
                    "专属选图声明魔兽标志或逐格对应原魔兽十五房：" + gateway);
                That(DungeonEntryPlan.TryCreate(catalog, beastCharacter, (uint)gateway, 0, 0, out var gatewayPlan, out var gatewayReason, beastMode: true) &&
                    gatewayPlan is { BeastMode: true, MonsterBasisLevel: 90, BeastHpScale: 5, BeastAttackScale: 2.3 } &&
                    gatewayPlan.Maze.MapSpecification!.Count == 15 && gatewayPlan.Maze.MapSpecification.Count(map => map.Kind == "Beast") == 6,
                    "专属普通请求冻结六阶段、完整路线与倍率：" + gateway + " / " + gatewayReason);
                // 2026-10-28T10:54:58 实机：点气球直接下发难度2，此前只认0把每次点击都拒了。
                That(!DungeonEntryPlan.TryCreate(catalog, beastCharacter, (uint)gateway, 0, 0, out _, out _) &&
                    DungeonEntryPlan.TryCreate(catalog, beastCharacter, (uint)gateway, 0, 2, out _, out _, beastMode: true) &&
                    !DungeonEntryPlan.TryCreate(catalog, beastCharacter, (uint)gateway, 0, 6, out _, out _, beastMode: true),
                    "专属图禁止普通经济路径；实机难度2可通过，越界6仍拒绝");
                var maze = gatewayDefinition.Mazes[0];
                var maps = maze.MapSpecification!;
                That(!DungeonEntryPlan.GatewayMatches(gatewayDefinition with { Mazes = [maze with { MapSpecification = [..maps.SkipLast(1), maps[0]] }] }, source),
                    "专属入口拒绝重复坐标补足数量的坏迷宫");
                That(!DungeonEntryPlan.GatewayMatches(gatewayDefinition with { Mazes = [maze with { Bosses = [maze.Starts[0]] }] }, source),
                    "专属入口拒绝客户端Boss坐标与原阶段终点不一致");
            }
            That(DfoServer.Game.Dungeons.DungeonEntryPlan.TryCreate(catalog, beastCharacter, 310, 0, 4, out var plainFour, out _) &&
                plainFour is { BeastMode: false } && plainFour.MonsterBasisLevel == catalog.Get(310).BasisLevel,
                "普通难度4不借用魔兽等级");
            if (sources[0].BeastLayouts.All(row => row.ResourceError is null))
            {
                That(DfoServer.Game.Dungeons.DungeonEntryPlan.TryCreate(catalog, beastCharacter, 310, 0, 4, out var beastPlan, out _, beastMode: true) &&
                    beastPlan is { BeastMode: true, MonsterBasisLevel: 90 } &&
                    beastPlan.Maze.MapSpecification!.Count(map => map.Kind.Equals("beast", StringComparison.OrdinalIgnoreCase)) == 6,
                    "魔兽入场的怪物基准等级是布局下限90，阶段房六间");
                That(beastPlan!.Maze.MapSpecification!.Count == 15 && beastPlan.Maze.Greed == layout.Greed &&
                    beastPlan.Maze.StaticTopology is { Supported: true, Rooms.Count: 15, Links.Count: 22 } &&
                    beastPlan.BaseBossMap == beastPlan.BossMap && beastPlan.BaseBossMap.Kind == "Beast",
                    "魔兽完整十五间房保留自身门墙、六阶段身份及真实终点，不借普通地图终点");
                var definition = catalog.Get(310);
                foreach (var malformed in new[]
                {
                    layout with { Maps = layout.Maps.Skip(1).ToArray() },
                    layout with { Maps = [..layout.Maps.SkipLast(1),layout.Maps[0]] },
                    layout with { Greed = "BB" },
                    layout with { Maps = layout.Maps.Select((map,index) => index == 0 ? map with { Path = "" } : map).ToArray() }
                })
                    That(!DungeonEntryPlan.TryCreate(definition with { BeastLayouts = [malformed] }, beastCharacter, 310, 0, 4,
                        out _, out _, beastMode: true), "魔兽缺房、同格、错误门墙或缺路径均拒绝，不缩减布局放行");
                var partner = beastCharacter with { Id = 2, Name = "Partner" };
                That(DungeonEntryPlan.TryCreate(catalog, partner, 310, 0, 4, out var partnerPlan, out _,
                    partyRoute: beastPlan, beastMode: true) && partnerPlan is { BeastMode: true } &&
                    ReferenceEquals(partnerPlan.PartyRoute, beastPlan), "队员沿用队长魔兽路线并保留模式");
                That(!DungeonEntryPlan.TryCreate(catalog, partner, 310, 0, 4, out _, out _, partyRoute: beastPlan) &&
                    !DungeonEntryPlan.TryCreate(catalog, partner, 310, 0, 4, out _, out _, partyRoute: plainFour, beastMode: true),
                    "普通与魔兽模式不能借用对方的共享路线");
            }
            var shops = new NpcShopCatalog(installed);
            var items = new ItemCatalog(installed,
                PvfLib.PvfScript.List(installed.ReadText("equipment/equipment.lst")),
                PvfLib.PvfScript.List(installed.ReadText("stackable/stackable.lst")), 0);
            var goldLimits = new GoldLimitCatalog(installed);
            var pieces = new EpicPieceCatalog(installed);
            var stock = shops.Stock(598);
            That(stock.Contains(10303752), "598正式原商店仍保留入场券商品声明，模板可用性另核");
            int shopId = shops.Shops.First(row => row.Value.NpcId == 598).Key;
            var buyer = beastCharacter with { Inventory = InventorySnapshot.Empty with { Gold = 1000000 } };
            string beforePurchase = JsonSerializer.Serialize(buyer);
            var shopService = new InventoryShopService(items, shops, goldLimits, pieces);
            if (stock.Contains(10303248))
                That(stock.Contains(10303752) && stock.Contains(10303248), "完整或追加商店保留入场券和设计图");
            else
            {
                That(!shops.Allows(598, shopId, 10303248), "当前未声明的设计图不能由模板存在推定为在售");
                That(shopService.Buy(buyer, new(10303248, 1, shopId, 598), out var receipt) is
                    { Accepted: false, Error: 17, Plan: null } && receipt is null &&
                    JsonSerializer.Serialize(buyer) == beforePurchase, "未在售设计图精确拒绝，不扣金币或发货");
            }
            foreach (int id in new[] { 10303237,10303238,10303240,10303241,10303242,10303243,10303244,10303245,
                10303246,10303247,10303248,10303495,10303496,10303497,10303498,10303752 })
            {
                if (items.Contains(id))
                {
                    var template = items.Get(id); // 已登记但正文损坏或解析失败必须继续失败。
                    That(template.Name.Length > 0 && (id != 10303240 || template.Name.Contains("魔兽地下城奖励袖珍罐")),
                        "已登记商店、配方与阶段奖励完整解析：" + id);
                }
                else
                {
                    That(!items.TryGet(id, out _), "缺登记模板不能伪造可用物品：" + id);
                    RejectExactly(() => InventoryRewardGrantService.TryGrantBatch(buyer, [new(id, 1)], items,
                        goldLimits, pieces), "PVF 物品不存在: " + id,
                        "缺模板奖励沿正式发放路径精确拒绝：" + id);
                    That(JsonSerializer.Serialize(buyer) == beforePurchase, "缺模板发放不改角色或库存：" + id);
                    if (stock.Contains(id))
                        That(shopService.Buy(buyer, new(id, 1, shopId, 598), out var receipt) is
                            { Accepted: false, Error: 17, Plan: null } && receipt is null &&
                            JsonSerializer.Serialize(buyer) == beforePurchase, "在售但缺模板商品拒绝且无资产变化：" + id);
                }
            }
            var quests = DfoServer.Testing.SelfTestCatalogs.Quests(installed);
            var qualification = quests.Get(5927);
            That(QuestCharacterReward.TryBeastQualification(qualification) && qualification.Definition is { Diagnostics.Length: 0 },
                "5927沿正式任务目录提供已确认魔兽资格，不要求原索引人为补登记");
            if (!PvfLib.PvfScript.List(installed.ReadText("n_quest/quest.lst")).ContainsKey(5927))
                That(installed.Contains("n_quest/new_quest/metrocenter/open_beast.qst") &&
                    qualification.Path == "n_quest/new_quest/metrocenter/open_beast.qst",
                    "5927未登记时只由正式目录既有精确物理路径fallback生效");
            That(new[] { 10307079, 10309050, 10309021, 10307043, 10307044, 10307062 }.All(stock.Contains),
                "魔兽商品追加后保留正式商店原商品、设计图与退还页");
            var beastRoster = DfoServer.Game.Characters.CharacterRosterEntry.FromCharacter(beastCharacter);
            var plainRoster = DfoServer.Game.Characters.CharacterRosterEntry.FromCharacter(beastCharacter with { CompletedQuests = [] });
            That(beastRoster.BeastEligible && !plainRoster.BeastEligible,
                "完成5927才打开名册魔兽位");
            Console.WriteLine($"PASS {Count - checks} 魔兽地图包断言");
        }
    }

    // 10000（new_quest/MetroCenter/beast_acce_01.qst）是魔兽材料任务：seeking / sub type 0，
    // [dungeon info] 只指向五个魔兽网关5335–5339（参数-1）。此前它落到「任务条件初值尚未确认」，
    // 客户端点接受无任何反应。这里核对真实资源形状、接取初值与库存驱动进度。
    public static void RunAccessoryQuest(PvfLib.PvfArchive installed, DfoServer.Infrastructure.ItemResources items)
    {
        int checks = Count;
        var productionCatalog = DfoServer.Testing.SelfTestCatalogs.Quests(installed);
        var catalog = productionCatalog;
        bool registered = productionCatalog.TryGet(10000, out var quest);
        if (!registered)
        {
            const string physicalPath = "n_quest/new_quest/metrocenter/beast_acce_01.qst";
            That(!PvfLib.PvfScript.List(installed.ReadText("n_quest/quest.lst")).ContainsKey(10000) && installed.Contains(physicalPath),
                "当前10000物理正文存在但无任务索引，不能因文件存在向生产目录补登记");
            RejectExactly(() => productionCatalog.Get(10000), "任务目录没有这个任务: 10000", "正式目录精确拒绝未登记10000");
            quest = QuestDefinitionReader.Read(10000, physicalPath, installed.ReadTokens(physicalPath), installed.ResolveString);
            catalog = new QuestCatalog(productionCatalog.All.Append(quest).ToDictionary(row => row.Id));
        }
        var definition = quest!; // 缺索引时仅本测试目录登记正式reader解析的物理脚本。
        That(definition is { Condition: "[seeking]", SubType: 0, RewardType: "[item]" } &&
            definition.IntData.SequenceEqual([10303237, 220, 10303238, 180]) &&
            definition.Definition!.Dungeons.Select(row => row.DungeonId).SequenceEqual(
                Enumerable.Range(BeastDungeonRotation.FirstGatewayDungeonId, 5)) &&
            definition.Definition.Dungeons.All(row => row.Parameter == -1),
            "10000真实资源为seeking子类型0、两件材料、五个魔兽网关");
        That(QuestLifecyclePlan.BeastSeeking(definition) && QuestLifecyclePlan.SeekingSubtype(definition) &&
            QuestLifecyclePlan.ValidSeeking(definition) && QuestCoverage.HasSupportedTarget(definition, catalog),
            "魔兽材料任务按精确形状接入寻物目标");
        // 同一形状不能外溢到别的任务号，也不能反向把黑市EXP寻物认成魔兽寻物。
        That(!QuestLifecyclePlan.BeastSeeking(definition with { Id = 10001 }) &&
            !QuestLifecyclePlan.BeastSeeking(catalog.Get(6053)) && !QuestLifecyclePlan.BeastSeeking(catalog.Get(5927)),
            "魔兽寻物合同只覆盖10000这一个任务");
        var character = new DfoServer.Game.Characters.Character(1, 0, "Acce", 90, new(38, 2, 450, 240), null)
            { Level = 90, CompletedQuests = [5927], DungeonState = CharacterDungeonState.NewOrdinary };
        var now = DateTimeOffset.UtcNow;
        var accept = QuestLifecyclePlan.Accept(character, definition, catalog, items.Items, items.GoldLimits, items.EpicPieces, now);
        bool realMaterials = items.Items.Contains(10303237) && items.Items.Contains(10303238);
        var active = accept.ActiveAfter.Single(row => row.QuestId == 10000);
        That(active is { ConditionState: 1 } && active.AcceptanceId != Guid.Empty, "缺材料时10000可接取，初值为未达成1");
        Character With(int first, int second)
        {
            if (!realMaterials)
                return character with { ActiveMainQuests = accept.ActiveAfter.ToArray(), Inventory = new([
                    new(1, new(InventoryListType.Main, 121), new(10303237, ItemKind.Material, first)),
                    new(2, new(InventoryListType.Main, 122), new(10303238, ItemKind.Material, second))]) };
            var grant = InventoryRewardGrantService.TryGrantBatch(
                character with { ActiveMainQuests = accept.ActiveAfter.ToArray() },
                [new(10303237, first), new(10303238, second)], items.Items, items.GoldLimits, items.EpicPieces);
            That(grant.Accepted && grant.Plan is not null, $"材料入包：{first}/{second}");
            return character with { ActiveMainQuests = accept.ActiveAfter.ToArray(), Inventory = grant.Plan!.After };
        }
        uint State(Character basis) => QuestInventoryProgress.Refresh(basis.ActiveMainQuests, basis.Inventory, catalog,
            now.ToUnixTimeSeconds()).Single(row => row.QuestId == 10000).ConditionState;
        var lacking = With(219, 180);
        var full = With(220, 180);
        That(State(lacking) == 1 && State(full) == 0, "两件材料都达到声明数量才把10000的进度归零");
        That(productionCatalog.TryGet(10000, out _) == registered, "本地夹具不改变生产10000登记状态");
        Console.WriteLine($"PASS {Count - checks} 魔兽材料任务10000接取与进度断言 / " +
            (registered && realMaterials ? "正式资源与真实材料发放" : "仅测试目录或合成Material实例；不代表当前生产任务接取/发奖成功"));
    }

    static void RejectExactly(Action action, string reason, string message)
    {
        bool rejected = false;
        try { action(); }
        catch (InvalidDataException error) when (error.Message == reason) { rejected = true; }
        That(rejected, message);
    }
}
