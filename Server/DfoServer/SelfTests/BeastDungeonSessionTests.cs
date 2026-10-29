using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Skills;
using DfoServer.Game.Session;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing;
using DfoServer.Testing.Inventory;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 在隔离文件库中走生产Session；合成角色与时钟不代表客户端点击或实机战斗。
internal static partial class BeastDungeonSessionTests
{
    public static void Run(TcpTestResources resources)
    {
        using (var fixture = new BeastSessionFixture(resources)) Run(resources,fixture);
        Run(resources,null);
    }

    static void Run(TcpTestResources resources, BeastSessionFixture? fixture)
    {
        var towns = fixture?.Towns ?? resources.Towns;
        var itemCatalog = fixture?.Items ?? resources.ItemResources.Items;
        Console.WriteLine(fixture is null ? "开始真实资源适用分支（缺输入不代表真实正向通过）" : "开始显式合成完整十五房/六阶段Session及Party（非真实PVF正向、非实机）");
        int enteredCount = 0, unavailableCount = 0;
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-beast-session-" + Guid.NewGuid().ToString("N"));
        var owners = new AccountOwners();
        var repository = new CharacterRepository(Path.Combine(root,"game.db"),resources.Quests);
        var catalog = fixture?.Dungeons ?? SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = fixture?.Rooms ?? SelfTestCatalogs.Rooms(resources.Archive);
        var drops = fixture?.Drops ?? SelfTestCatalogs.MonsterDrops(resources.Archive);
        var kills = SelfTestCatalogs.KillExperience(resources.Archive);
        var actualStageMaps = new HashSet<int>();
        int[] availableNativeDungeons = fixture is null
            ? Enumerable.Range(310,5).Where(id=>!catalog.Get(id).BeastLayouts.Any(layout=>layout.ResourceError is not null)).ToArray()
            : [];
        var expectedStageMaps = availableNativeDungeons.SelectMany(id=>Enumerable.Range(0,2).SelectMany(encounter=>
            Enumerable.Range(0,6).Select(stage=>ExpectedStage(id,encounter,stage).Map))).ToHashSet();
        try
        {
            CheckTicketPolicy(resources);
            foreach (var (dungeon, encounter) in Enumerable.Range(310,5).SelectMany(id => new[] { (id, 0), (id, 1) })
                .Concat(Enumerable.Range(BeastDungeonRotation.FirstGatewayDungeonId,5).Select(id => (id, 0))))
            {
                bool gateway = BeastDungeonRotation.IsGateway((uint)dungeon);
                using var owner = owners.Acquire("beast-session-"+dungeon+"-"+encounter,()=>{});
                // 真实输入的原图入口必须来自原中央公园31/worldmap39；合成夹具与专属网关按世界地图登记取首个可选区。
                var area = towns.Areas.Values.FirstOrDefault(row => row.DungeonWorldMap is { } world &&
                    (fixture is not null || gateway || row.Town == 31 && row.DungeonWorldMap == 39) &&
                    towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeon));
                if (area is null)
                {
                    That(fixture is null && gateway,"合成完整夹具和真实普通入口必须有选图几何");
                    VerifyMissingGatewayEntrance(resources,catalog,dungeon);
                    unavailableCount++;
                    continue;
                }
                var completed = new HashSet<int>(area.Quests) { 5927 };
                if (area.AnyQuests.Length != 0) completed.Add(area.AnyQuests[0]);
                completed.UnionWith(area.Map(completed).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
                var bounds = area.Map(completed).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(completed.Contains)).Bounds;
                var position = new Location(area.Town,area.Area,(ushort)(bounds.X+bounds.Width/2),(ushort)(bounds.Y+bounds.Height/2));
                var candidate = CharacterCreation.Prepare("Beast"+dungeon+encounter,resources.Characters.Get(0),position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) },resources.Learning);
                repository.Create(owner,candidate with { Level=95,AcceptedStoryLevel=95,CompletedQuests=completed.ToArray(),InitialLearning=learning });
                if (!gateway) GiveTickets(repository, owner);
                var at = new DateTimeOffset(2026,10,26,12,0,0,TimeSpan.FromHours(8));
                while (!BeastDungeonRotation.Contains(BeastDungeonRotation.ServiceDay(at),dungeon) || !gateway &&
                    (BeastDungeonRotation.Pair(BeastDungeonRotation.ServiceDay(at)).First == dungeon ? 0 : 1) != encounter) at=at.AddDays(1);
                var clock = new DailyBoosterTestClock(at);
                var session = new GameSession(repository,towns,owner,questCatalog:resources.Quests,timeProvider:clock,
                    items:itemCatalog,goldLimits:resources.ItemResources.GoldLimits,pieces:resources.ItemResources.EpicPieces,
                    entry:gateway ? new(new(2,49),40,"[beast]") : new(new(2,1),22,"[normal]"));
                session.Select(0,resumeTown:true); session.MarkReady();
                if (!gateway) That(session.BeginDungeonSelection() is not null && session.CaptureBeastDungeonSelection() is not null,
                    $"首版从普通频道原中央公园正式选图进入：{session.Selected!.Position}/{session.ChannelEntry?.Type}");
                if (gateway)
                {
                    // 探针必须用自己的角色：普通频道选角会把角色移出专属中央公园并保存落点（真实修复行为），
                    // 共用同一角色会先改写主角色的revision，主Session随后入场就不再是在测频道门禁。
                    using var probeOwner = owners.Acquire("beast-session-probe-"+dungeon,()=>{});
                    repository.Create(probeOwner,candidate with { Name="BP"+dungeon, Level=95,AcceptedStoryLevel=95,
                        CompletedQuests=completed.ToArray(),InitialLearning=learning });
                    var wrongChannel = new GameSession(repository,towns,probeOwner,questCatalog:resources.Quests,timeProvider:clock,
                        items:itemCatalog,goldLimits:resources.ItemResources.GoldLimits);
                    wrongChannel.Select(0,resumeTown:true); wrongChannel.MarkReady();
                    That(wrongChannel.EnterFormalDungeon((uint)dungeon,0,0,catalog,rooms,drops,
                        items:itemCatalog,pieces:resources.ItemResources.EpicPieces,beastMode:true) is null &&
                        wrongChannel.LastFormalDungeonRejection == "beast-gateway-channel-and-mode-required",
                        "裸Session不能绕过专属频道门禁");
                    int closed = Enumerable.Range(1,5).First(days=>!BeastDungeonRotation.Contains(BeastDungeonRotation.ServiceDay(at.AddDays(days)),dungeon));
                    clock.Advance(TimeSpan.FromDays(closed));
                    That(session.EnterFormalDungeon((uint)dungeon,0,0,catalog,rooms,drops,
                        items:itemCatalog,pieces:resources.ItemResources.EpicPieces,beastMode:true) is null &&
                        repository.LoadBeastDungeonUsed(owner,session.Selected!.Id,BeastDungeonRotation.ServiceDay(clock.GetUtcNow()),dungeon)==0,
                        "非开放日专属入口拒绝且不扣次");
                    clock.Advance(TimeSpan.FromDays(-closed));
                }
                // 难度取实机原值：全量日志 217,237 行里五张兼容入口图（5335–5339）的 CMD16 一律是难度 2，
                // 原生 310–314 则出现 0/1/4（4 是第六档）。以前这里一律用 0，测的不是客户端真正发的东西。
                // 兼容入口的 PVF 难度表只有 `1 1 1 0 0`，客户端发 5 会被服务端以 difficulty-unsupported 拒绝，
                // 这一点在没有第六按钮实机包之前不改行为。
                byte difficulty = gateway ? (byte)2 : (byte)4;
                string unchanged = JsonSerializer.Serialize(repository.LoadCharacter(owner,0));
                var scene = session.EnterFormalDungeon((uint)dungeon,0,difficulty,catalog,rooms,drops,
                    items:itemCatalog,pieces:resources.ItemResources.EpicPieces,beastMode:true,
                    prepareDrops:fixture?.PrepareDrops);
                if (fixture is null && SourceUnavailable(catalog,dungeon))
                {
                    string reason = gateway ? "entry-plan:beast-gateway-source-unavailable" : "entry-plan:beast-layout-resource-unavailable";
                    var expectedPhase = gateway ? SessionPhase.Town : SessionPhase.MapSelect;
                    That(scene is null && session.LastFormalDungeonRejection == reason &&
                        JsonSerializer.Serialize(repository.LoadCharacter(owner,0)) == unchanged &&
                        session.CurrentFormalDungeonScene() is null && session.Phase == expectedPhase &&
                        repository.LoadBeastDungeonUsed(owner,session.Selected!.Id,BeastDungeonRotation.ServiceDay(at),dungeon)==0,
                        $"真实坏兽布局经正式Session精确拒绝，不改角色、资产、疲劳、attempt或次数：{dungeon} / phase={session.Phase}, expected={expectedPhase} / reason={session.LastFormalDungeonRejection}, expected={reason}");
                    unavailableCount++;
                    continue;
                }
                enteredCount++;
                That(scene is not null,"正式魔兽Session完整路线准备及原子入场："+dungeon+" / 难度"+difficulty+" / "+session.LastFormalDungeonRejection);
                That(repository.LoadBeastDungeonUsed(owner,session.Selected!.Id,BeastDungeonRotation.ServiceDay(at),dungeon)==(gateway ? 1 : 0),
                    "beast-first-room:原图加载前只预约，专属入口保留原次数合同");
                That(scene!.Entry.BeastMode && scene.Entry.Maze.MapSpecification!.Count == 15 &&
                    scene.Entry.Maze.StaticTopology.Supported && session.CompleteFormalDungeonLoading() is not null,
                    "五张魔兽图均通过正式Run及地图加载，不能只证明资源能打开");
                // 「有魔兽过场图」的判据在魔兽区块身上，不在客户端；第六难度也必须带着它进场。
                // 兼容入口固定取 310 的 BeastLayouts[0]，所以这里钉死资源路径；原图只要求非空，各自的区块可不同。
                That(gateway
                        ? scene.Entry.Maze.BeastLayout?.CutsceneImage == "Map/CutScene/Beast.img"
                        : !string.IsNullOrEmpty(scene.Entry.Maze.BeastLayout?.CutsceneImage),
                    "魔兽入场带区块声明的过场图："+dungeon+" / 难度"+difficulty+" / "+scene.Entry.Maze.BeastLayout?.CutsceneImage);
                That(repository.LoadBeastDungeonUsed(owner,session.Selected!.Id,BeastDungeonRotation.ServiceDay(at),dungeon)==1,
                    "正式魔兽Session成功入场只扣一次本图次数");
                if (gateway && dungeon != BeastDungeonRotation.FirstGatewayDungeonId) continue;
                var cleared = new HashSet<DfoServer.GameWorld.Dungeons.DungeonPoint>();
                var bound = new Dictionary<DfoServer.GameWorld.Dungeons.DungeonPoint, int>();
                var targets = new[] { scene.Entry.BossMap }.Concat(scene.Entry.Maze.MapSpecification!
                    .Where(map => map.Kind == "Beast" && map != scene.Entry.BossMap)).ToArray();
                foreach (var target in targets)
                {
                    var path = QuestStoryRouteTests.Path(scene.Entry.Maze.StaticTopology,
                        session.CurrentFormalDungeonScene()!.Snapshot.CurrentMap.Position,target.Position);
                    ClearCurrent();
                    foreach (var point in path.Skip(1))
                    {
                        That(session.MoveFormalDungeon((byte)point.X,(byte)point.Y) is not null,
                            "魔兽按真实门墙过图，提前清理静态末格也不锁死后续阶段");
                        That(session.CompleteFormalDungeonLoading() is not null,"魔兽每次过图保持正式加载屏障");
                        ClearCurrent();
                    }
                    if (target == targets[0])
                        That(cleared.Count < 6 && session.Phase == SessionPhase.Dungeon,
                            "先清脚本最后一格仍未完成全部六阶段，不能提前进入结算后卡死");
                    if (session.Phase == SessionPhase.Settlement) break;
                }
                That(cleared.Count == 6 && session.Phase == SessionPhase.Settlement &&
                    session.EnableFormalDungeonSettlement() is not null,
                    "全部六间阶段房真实清完后，原Run才签发唯一通关事实");
                That(BeastDungeonRotation.StageRewardItems.All(id => session.Selected!.Inventory.Items
                    .Where(item => item.Core.ItemId == id).Sum(item => item.Core.Count) == 1),
                    "完整正式魔兽Session六阶段各到账一次袖珍罐，回访不重复发放");
                if (fixture is null && !gateway) That(session.CaptureBeastRun()!.Progress.View() is
                    { Terminal: BeastTerminal.Cleared, CompletedStages: 6, Score: 20700, Rank: 0 },
                    "原图六阶段指定Boss各计一次分，原Run通关终局为20700分");

                void ClearCurrent()
                {
                    var current = session.CurrentFormalDungeonScene()!;
                    if (fixture is null && !gateway && current.Snapshot.CurrentMap.Kind == "Beast")
                    {
                        var point = current.Snapshot.CurrentMap.Position;
                        if (bound.TryGetValue(point, out int priorMap))
                            That(current.Snapshot.CurrentMap.Id == priorMap && current.Monsters.Monsters.All(monster => !monster.IsBlocking),
                                "beast-dynamic:回访绑定原MAP，不重刷演员");
                        else
                        {
                            var expected = ExpectedStage(dungeon, encounter, cleared.Count);
                            That(current.Snapshot.CurrentMap.Id == expected.Map &&
                                current.Monsters.Monsters.Count(monster => monster.IsBoss && monster.MonsterId == expected.Boss) == 1,
                                "beast-dynamic:首访按遭遇阶段绑定真实MAP与指定Boss");
                            bound.Add(point, current.Snapshot.CurrentMap.Id);
                            actualStageMaps.Add(current.Snapshot.CurrentMap.Id);
                            var boss = current.Monsters.Monsters.Single(monster => monster.IsBoss && monster.MonsterId == expected.Boss);
                            var invalid = FormalDungeonSessionTests.Observation(current, boss) with { MonsterKey = ushort.MaxValue };
                            var rejected = session.ObserveFormalDungeonDeath(invalid,kills,resources.Growth);
                            var after = session.CurrentFormalDungeonScene()!.Snapshot;
                            That(rejected.Outcome == DungeonRunOutcome.Rejected && after.Identity == current.Snapshot.Identity &&
                                after.CurrentMap == current.Snapshot.CurrentMap && after.LivingMonsters.SequenceEqual(current.Snapshot.LivingMonsters),
                                "beast-dynamic:未发布对象不能推进阶段");
                        }
                    }
                    foreach (var monster in current.Monsters.Monsters.Where(monster => monster.IsBlocking && monster.CanReportDeath)
                        .OrderBy(monster => fixture is null && !gateway && current.Snapshot.CurrentMap.Kind == "Beast"
                            ? monster.MonsterId == ExpectedStage(dungeon, encounter, cleared.Count).Boss : monster.IsBoss))
                    {
                        var result = session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(
                            session.CurrentFormalDungeonScene()!,monster),kills,resources.Growth);
                        That(result.Outcome == DungeonRunOutcome.Accepted,"魔兽实际阶段演员通过生产死亡确认并提交成长");
                        if (fixture is null && !gateway && current.Snapshot.CurrentMap.Kind == "Beast" &&
                            monster.MonsterId != ExpectedStage(dungeon, encounter, cleared.Count).Boss)
                            That(result.BeastRewardSlots.IsDefaultOrEmpty && session.Phase == SessionPhase.Dungeon,
                                "beast-dynamic:同图其他Boss死亡不推进阶段或授予阶段罐");
                    }
                    if (current.Snapshot.CurrentMap.Kind == "Beast") cleared.Add(current.Snapshot.CurrentMap.Position);
                    That(cleared.Count == 6 || session.Phase == SessionPhase.Dungeon,
                        "未清完六阶段时保留正式战斗阶段，允许继续寻找其余阶段房");
                }
            }
            if (fixture is null)
            {
                That(actualStageMaps.SetEquals(expectedStageMaps),
                    $"beast-dynamic:可用原图各两种遭遇的固定六阶段MAP全部经过实际加载与死亡链：实际{actualStageMaps.Count}，期望{expectedStageMaps.Count}");
                if (availableNativeDungeons.Length == 5)
                    That(actualStageMaps.Count == 60, "beast-dynamic:五图两种遭遇的全部六十阶段MAP经过实际加载与死亡链");
                else
                    Console.WriteLine($"真实魔兽原图{string.Join(',',Enumerable.Range(310,5).Except(availableNativeDungeons))}两种遭遇的六阶段正向未执行：源布局缺资源；可用原图已验证{expectedStageMaps.Count}/60阶段MAP，缺依赖精确拒绝另验");
            }
        }
        finally { Directory.Delete(root,true); }
        RunParty(resources,310,fixture);
        if (fixture is null)
        {
            // 超时终局依赖真实六阶段MAP与指定Boss，合成夹具不承担该正向。
            if (availableNativeDungeons.Contains(310))
            {
                RunParty(resources,310,null, timeout: true, pendingBoss: true);
                RunParty(resources,310,null, timeout: true, pendingBoss: false);
            }
            else
                Console.WriteLine("真实魔兽组队两个超时终局正向未执行：原图310源布局缺资源；正常组队调用保留精确原子拒绝验证");
        }
        RunParty(resources,BeastDungeonRotation.FirstGatewayDungeonId,fixture);
        if (fixture is not null)
            That(enteredCount==15 && unavailableCount==0,"合成完整输入必须覆盖十五入口及原单人/组队六阶段成功体");
        Console.WriteLine($"PASS {Count-before} 魔兽Session {(fixture is null ? "真实资源适用分支" : "显式合成完整十五房/六阶段单人及组队")}断言（非实机）；成功入口{enteredCount}，缺依赖拒绝{unavailableCount}");
    }

    static (int Map, int Boss) ExpectedStage(int dungeon, int encounter, int stage)
    {
        int[][][] maps =
        [
            [[400363,400373,400374,400375,400376,400377],[400364,400378,400379,400380,400381,400382]],
            [[400365,400383,400384,400385,400386,400387],[400366,400389,400390,400391,400392,400393]],
            [[400371,400414,400415,400416,400417,400418],[400372,400419,400420,400421,400422,400423]],
            [[400369,400404,400405,400406,400407,400408],[400370,400409,400410,400411,400412,400413]],
            [[400367,400394,400395,400396,400397,400398],[400368,400399,400400,400401,400402,400403]]
        ];
        int[] bosses = encounter == 0 ? [107005374,107005375,107005376,107005377,107005378,107005379] : [66159,66160,66161,66162,66164,66165];
        return (maps[dungeon-310][encounter][stage], bosses[stage]);
    }

    static void CheckTicketPolicy(TcpTestResources resources)
    {
        var pieces = resources.ItemResources.EpicPieces;
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var eventTicket = new InventoryItem(1, new(InventoryListType.Main, 121), new(BeastDungeonTicket.Event, ItemKind.Material, 1));
        var normalTicket = new InventoryItem(2, new(InventoryListType.Main, 122), new(BeastDungeonTicket.Ordinary, ItemKind.Material, 1));
        var both = InventorySnapshot.Empty with { Items = [eventTicket, normalTicket] };
        var items = resources.ItemResources.Items;
        foreach (int id in new[] { BeastDungeonTicket.Event, BeastDungeonTicket.Ordinary })
        {
            if (items.Contains(id))
            {
                That(items.Get(id).Kind == ItemKind.Material, "正式PVF已登记魔兽票为材料类：" + id);
            }
            else
            {
                That(!items.TryGet(id, out _), "缺登记魔兽票不能伪造可用模板：" + id);
                bool rejected = false;
                try { items.Get(id); }
                catch (InvalidDataException error) when (error.Message == "PVF 物品不存在: " + id) { rejected = true; }
                That(rejected, "缺登记魔兽票沿正式目录精确拒绝：" + id);
                Console.WriteLine("真实魔兽票" + id + "缺登记；以下仅验证既有显式库存输入下的票策略，不代表生产票可用");
            }
        }
        That(BeastDungeonTicket.Plan(both, pieces, now) is { ChangedSlots.Length: 1 } preferred &&
            preferred.ChangedSlots[0] == eventTicket.Position, "活动票优先于普通票");
        That(BeastDungeonTicket.Plan(both with { Items = [normalTicket] }, pieces, now)?.ChangedSlots[0] == normalTicket.Position,
            "缺少活动票时消费普通票");
        That(BeastDungeonTicket.Plan(both with { Items = [eventTicket with { Core = eventTicket.Core with { EquipmentLockId = 1 } }, normalTicket] }, pieces, now)?.ChangedSlots[0] == normalTicket.Position,
            "锁定活动票不可消费，改用可用普通票");
        That(BeastDungeonTicket.Plan(both with { Items = [eventTicket with { Core = eventTicket.Core with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) } }, normalTicket] }, pieces, now)?.ChangedSlots[0] == normalTicket.Position,
            "活动票到期等号不可消费，改用普通票");
        That(BeastDungeonTicket.Plan(InventorySnapshot.Empty, pieces, now) is null, "没有门票拒绝准入");
    }

    internal static void GiveTickets(CharacterRepository repository, AccountOwners.Lease owner)
    {
        var character = repository.LoadCharacter(owner, 0);
        repository.CommitInventory(owner, character, new(character.Inventory, character.Inventory with
        {
            Items = character.Inventory.Items.Add(new(0, new(InventoryListType.Main, 121), new(BeastDungeonTicket.Event, ItemKind.Material, 5)))
                .Add(new(0, new(InventoryListType.Main, 122), new(BeastDungeonTicket.Ordinary, ItemKind.Material, 5)))
        }, "test_beast_tickets"));
    }

    static void RunParty(TcpTestResources resources, int dungeon, BeastSessionFixture? fixture, bool timeout = false, bool pendingBoss = false)
    {
        var towns = fixture?.Towns ?? resources.Towns;
        var itemCatalog = fixture?.Items ?? resources.ItemResources.Items;
        if (fixture is null && BeastDungeonRotation.IsGateway((uint)dungeon) && !towns.Areas.ContainsKey((51,2)))
        {
            Console.WriteLine("真实魔兽组队正向未执行：缺少已登记51/2入口；显式合成组队正向另行验证");
            return;
        }
        bool gateway=BeastDungeonRotation.IsGateway((uint)dungeon);
        string root = Path.Combine(Path.GetTempPath(), "S5A9-beast-party-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root,"game.db");
        var owners = new AccountOwners();
        using var alpha = owners.Acquire("beast-pa",()=>{});
        using var beta = owners.Acquire("beast-pb",()=>{});
        try
        {
            var repository = new CharacterRepository(path,resources.Quests);
            var catalog = fixture?.Dungeons ?? SelfTestCatalogs.Dungeons(resources.Archive);
            var rooms = fixture?.Rooms ?? SelfTestCatalogs.Rooms(resources.Archive);
            var drops = fixture?.Drops ?? SelfTestCatalogs.MonsterDrops(resources.Archive);
            var kills = SelfTestCatalogs.KillExperience(resources.Archive);
            var items = resources.ItemResources;
            var dropPolicy = fixture is null ? new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(resources.Archive),items.DungeonDropItems,
                drops.Get,items.Items.Get,items.DungeonSpecialDrops) : null;
            // 真实输入的原图入口必须来自原中央公园31/worldmap39；合成夹具与专属网关按世界地图登记取首个可选区。
            var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                (fixture is not null || gateway || row.Town == 31 && row.DungeonWorldMap == 39) &&
                towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeon));
            var completed = new HashSet<int>(area.Quests) {5927};
            if (area.AnyQuests.Length != 0) completed.Add(area.AnyQuests[0]);
            completed.UnionWith(area.Map(completed).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
            var bounds = area.Map(completed).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(completed.Contains)).Bounds;
            var position = new Location(area.Town,area.Area,(ushort)(bounds.X+bounds.Width/2),(ushort)(bounds.Y+bounds.Height/2));
            var at = new DateTimeOffset(2026,10,26,5,59,59,TimeSpan.FromHours(8));
            while (!BeastDungeonRotation.Contains(BeastDungeonRotation.ServiceDay(at),dungeon)) at=at.AddDays(1);
            var clocks = new[] {new DailyBoosterTestClock(at),new DailyBoosterTestClock(at.AddDays(1))};
            var leases = new[] {alpha,beta};
            var sessions = leases.Select((owner,i) =>
            {
                var candidate = CharacterCreation.Prepare("BeastParty"+i,resources.Characters.Get(0),position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with
                    {Experience=resources.Growth.GetLevelStartExperience(95)},resources.Learning);
                repository.Create(owner,candidate with {Level=95,AcceptedStoryLevel=95,CompletedQuests=completed.ToArray(),InitialLearning=learning});
                if (!gateway) GiveTickets(repository, owner);
                var session = new GameSession(repository,towns,owner,questCatalog:resources.Quests,timeProvider:clocks[i],
                    items:itemCatalog,goldLimits:resources.ItemResources.GoldLimits,pieces:resources.ItemResources.EpicPieces,
                    entry:gateway ? new(new(2,49),40,"[beast]") : new(new(2,1),22,"[normal]"));
                session.Select(0,resumeTown:true); session.MarkReady(); return session;
            }).ToArray();
            var parties = new PartyService(repository);
            foreach (var session in sessions) session.CurrentParty = () => parties.Current(session);
            That(parties.Invite(sessions[0],sessions[1],sessions[1].Selected!.Id).Accepted &&
                parties.Respond(sessions[1],sessions[0].Selected!.Id,0,true).Accepted,"魔兽使用真实邀请与接受建立两人队伍");
            var service = new PartyDungeonService(parties,repository);
            That(service.BeginSelection(sessions[0]).Length==2,"魔兽两人共同进入生产选图");
            string State() => JsonSerializer.Serialize(leases.Select(owner=>repository.LoadCharacter(owner,0)).ToArray());
            int day = BeastDungeonRotation.ServiceDay(at);
            bool Uncharged() => leases.All(owner=>repository.LoadBeastDungeonUsed(owner,repository.LoadCharacter(owner,0).Id,day,310)==0);
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText=sql; command.ExecuteNonQuery();
            }
            PartyDungeonNotice[] Enter(Action? prepared=null) => service.Enter(sessions[0],(uint)dungeon,gateway ? (byte)0 : (byte)4,catalog,rooms,drops,
                itemCatalog,resources.ItemResources.EpicPieces,entry =>
                {
                    prepared?.Invoke();
                    return fixture is null ? dropPolicy!.PrepareEntry(entry) : fixture.PrepareDrops(entry);
                },beastMode:true);
            string initial = State();
            if (fixture is null && SourceUnavailable(catalog,dungeon))
            {
                string reason = gateway ? "entry-plan:beast-gateway-source-unavailable" : "entry-plan:beast-layout-resource-unavailable";
                That(Enter().Length==0 && sessions[0].LastFormalDungeonRejection==reason && State()==initial && Uncharged() &&
                    sessions.All(session=>session.Phase==SessionPhase.MapSelect && session.CurrentFormalDungeonScene() is null && session.PartyDungeon is null),
                    "真实坏兽布局经组队准备精确拒绝，全员角色、资产、疲劳、attempt和次数不变");
                Console.WriteLine("真实魔兽组队六阶段正向未执行：源布局缺资源；已验证原子拒绝，显式合成正向另报");
                return;
            }
            bool advanced=false;
            That(Enter(()=> {if(!advanced){advanced=true;clocks[0].Advance(TimeSpan.FromSeconds(1));}}).Length==0 &&
                State()==initial && Uncharged(),"准备中跨06:00时全队拒绝，次数与角色状态均不写入");
            clocks[0].Advance(TimeSpan.FromSeconds(-1));
            ushort second=sessions[1].Selected!.Id;
            Sql($"CREATE TRIGGER fail_beast_party BEFORE UPDATE OF revision ON characters WHEN NEW.id={second} BEGIN SELECT RAISE(ABORT,'beast party late rollback'); END");
            Reject<SqliteException>(()=>Enter(),"魔兽第二队员入场提交晚期失败");
            That(State()==initial && Uncharged() && sessions.All(session=>session.Phase==SessionPhase.MapSelect),
                "第二队员失败回滚全员费用、次数与attempt，且不安装运行实例");
            Sql("DROP TRIGGER fail_beast_party");
            var entered=Enter();
            That(entered.Length==2 && entered.All(notice=>notice.Scene!.Entry.BeastMode && notice.Scene.Entry.BeastDay==day) &&
                sessions.All(session=>ReferenceEquals(session.PartyDungeon,sessions[0].PartyDungeon)),
                "队员时钟不同仍透传魔兽模式、共同日和共享实例");
            That(leases.All(owner=>repository.LoadBeastDungeonUsed(owner,repository.LoadCharacter(owner,0).Id,day,310)==(gateway ? 1 : 0)),
                "beast-first-room:全队原图首房加载前不扣次");
            var instance=sessions[0].PartyDungeon!;
            if (fixture is null && !gateway) That(ReferenceEquals(sessions[0].CaptureBeastRun()!.Progress, sessions[1].CaptureBeastRun()!.Progress),
                "队员时钟偏移仍共用同一个阶段、分数和单调deadline对象");
            void Loaded()
            {
                That(instance.CompleteLoading(sessions[0]).Length==0 && instance.CompleteLoading(sessions[1]).Length==2,
                    "魔兽组队全员加载完成才释放共享屏障");
            }
            Loaded();
            That(leases.All(owner=>repository.LoadBeastDungeonUsed(owner,repository.LoadCharacter(owner,0).Id,day,310)==1),
                "beast-first-room:全员首房加载后恰好扣一次共同日次数");
            var entry=entered[0].Scene!.Entry;
            var targets=new[] {entry.BossMap}.Concat(entry.Maze.MapSpecification!.Where(map=>map.Kind=="Beast" && map!=entry.BossMap));
            var cleared=new HashSet<DfoServer.GameWorld.Dungeons.DungeonPoint>();
            bool checkedRetry=false, checkedDeadline=false, timedOut=false;
            foreach (var target in targets)
            {
                var route=QuestStoryRouteTests.Path(entry.Maze.StaticTopology,sessions[0].CurrentFormalDungeonScene()!.Snapshot.CurrentMap.Position,target.Position);
                ClearCurrent();
                if (timedOut) break;
                foreach(var point in route.Skip(1))
                {
                    if(!checkedDeadline && cleared.Count>=3 && !cleared.Contains(point) &&
                        entry.Maze.MapSpecification!.Any(map=>map.Position==point && map.Kind=="Beast"))
                    {
                        var previous=sessions.Select(session=>session.CurrentFormalDungeonScene()!.Snapshot.CurrentMap).ToArray();
                        clocks[0].Advance(TimeSpan.FromSeconds(180));
                        That(instance.Move(sessions[0],(byte)point.X,(byte)point.Y,0).Length==0 &&
                            sessions.Select(session=>session.CurrentFormalDungeonScene()!.Snapshot.CurrentMap).SequenceEqual(previous),
                            "共同180秒边界拒绝全队新阶段，任何队员均不先移动");
                        clocks[0].Advance(TimeSpan.FromSeconds(-1));
                        That(instance.Move(sessions[0],(byte)point.X,(byte)point.Y,0).Length==2,
                            "边界前一秒使用报告者共同时间，偏移队员时钟不拆散队伍");
                        clocks[0].Advance(TimeSpan.FromSeconds(-179));
                        checkedDeadline=true;
                    }
                    else
                    That(instance.Move(sessions[0],(byte)point.X,(byte)point.Y,0).Length==2,"魔兽组队共用门墙、阶段和移动时刻");
                    Loaded(); ClearCurrent();
                    if (timedOut) break;
                }
                if(sessions[0].Phase==SessionPhase.Settlement) break;
            }
            if (timeout) That(timedOut && sessions.All(session => session.Phase == SessionPhase.Settlement),
                "组队压线原死亡恢复后共同超时，不拆成不同终局");
            else
            {
                That(checkedRetry && checkedDeadline && cleared.Count==6 && sessions.All(session=>session.Phase==SessionPhase.Settlement &&
                    BeastDungeonRotation.StageRewardItems.All(id=>session.Selected!.Inventory.Items.Where(item=>item.Core.ItemId==id).Sum(item=>item.Core.Count)==1)),
                    "两人六阶段全部通关，各自六种奖励恰好一份");
                if (fixture is null && !gateway) That(sessions.All(session => session.CaptureBeastRun()!.Progress.View() is
                    { Terminal: BeastTerminal.Cleared, CompletedStages: 6, Score: 20700, Rank: 0 }),
                    "组队全员资产完成后共同确认六阶段计分及通关终局");
            }

            void ClearCurrent()
            {
                var scene=sessions[0].CurrentFormalDungeonScene()!;
                foreach(var monster in scene.Monsters.Monsters.Where(row=>row.IsBlocking && row.CanReportDeath)
                    .OrderBy(row=>fixture is null && !gateway && scene.Snapshot.CurrentMap.Kind=="Beast"
                        ? row.MonsterId==ExpectedStage(dungeon,BeastDungeonRotation.Pair(day).First==dungeon ? 0 : 1,cleared.Count).Boss : row.IsBoss))
                {
                    var observation=FormalDungeonSessionTests.Observation(scene,monster);
                    bool advanceBeforeSecond = false;
                    PartyDungeonNotice[] Kill()=>instance.ObserveDeath(sessions[0],monster.ObjectKey,
                        member =>
                        {
                            if (advanceBeforeSecond && ReferenceEquals(member, sessions[1]))
                            {
                                clocks[0].Advance(TimeSpan.FromMilliseconds(1));
                                advanceBeforeSecond = false;
                            }
                            return member.ObserveFormalDungeonDeath(observation,kills,resources.Growth);
                        });
                    if (timeout && cleared.Count == 2 && scene.Snapshot.CurrentMap.Kind == "Beast" &&
                        (monster.MonsterId == ExpectedStage(dungeon, BeastDungeonRotation.Pair(day).First == dungeon ? 0 : 1, 2).Boss) == pendingBoss)
                    {
                        clocks[0].Advance(TimeSpan.FromMilliseconds(179999));
                        advanceBeforeSecond = !pendingBoss;
                        Sql($"CREATE TRIGGER fail_beast_edge AFTER INSERT ON character_dungeon_growth_receipts WHEN NEW.character_id={second} BEGIN SELECT RAISE(ABORT,'beast edge late rollback'); END");
                        Reject<SqliteException>(() => Kill(), "压线已接受死亡在第二队员资产提交时失败");
                        Sql("DROP TRIGGER fail_beast_edge");
                        if (pendingBoss) clocks[0].Advance(TimeSpan.FromMilliseconds(1));
                        var expired = instance.TickBeastDeadline(sessions[0]);
                        int stages = pendingBoss ? 3 : 2;
                        That(expired.Count(notice => notice.Kind == PartyDungeonNoticeKind.Death) == 2 &&
                            expired.Count(notice => notice.Kind == PartyDungeonNoticeKind.BeastUpdated) == 2 &&
                            sessions.All(session => session.CaptureBeastRun()!.Progress.View() is var view &&
                                view.Terminal == BeastTerminal.TimedOut && view.CompletedStages == stages && view.Score == (pendingBoss ? 7540 : 3900)),
                            "beast-deadline:到期先恢复已接受的原成员击杀，再全员超时；指定Boss仅共同计一次原时长分数");
                        That(sessions.All(session => BeastDungeonRotation.StageRewardItems.Select((id, index) =>
                            session.Selected!.Inventory.Items.Where(item => item.Core.ItemId == id).Sum(item => item.Core.Count) == (index < stages ? 1 : 0)).All(value => value)),
                            "压线恢复只到账已提交的阶段奖励，普通怪不冒充阶段通关");
                        string saved = State();
                        That(instance.TickBeastDeadline(sessions[1]).Length == 0 && Kill().Length == 0 && State() == saved &&
                            sessions.All(session => session.EnableFormalDungeonSettlement() is null),
                            "重复deadline和迟到死亡不能重领奖或签发普通翻牌许可");
                        timedOut = true;
                        return;
                    }
                    if(!checkedRetry && scene.Snapshot.CurrentMap.Kind=="Beast")
                    {
                        Sql($"CREATE TRIGGER fail_beast_death AFTER INSERT ON character_dungeon_growth_receipts WHEN NEW.character_id={second} BEGIN SELECT RAISE(ABORT,'beast death late rollback'); END");
                        Reject<SqliteException>(()=>Kill(),"魔兽共享死亡第二队员提交失败");
                        Sql("DROP TRIGGER fail_beast_death");
                        checkedRetry=true;
                    }
                    var committedNotices = Kill();
                    That(committedNotices.Length==2,$"魔兽共享死亡可恢复未完成成员，不重复已提交成员：dungeon={dungeon}, map={scene.Snapshot.CurrentMap.Id}, monster={monster.MonsterId}/{monster.ObjectKey}, notices={committedNotices.Length}, phases={string.Join(',',sessions.Select(session=>session.Phase))}");
                    string committed=State();
                    That(Kill().Length==0 && State()==committed,"共享死亡重复上报不重复经验与阶段奖励");
                }
                if(scene.Snapshot.CurrentMap.Kind=="Beast") cleared.Add(scene.Snapshot.CurrentMap.Position);
                That(cleared.Count==6 || sessions.All(session=>session.Phase==SessionPhase.Dungeon),
                    "全队未清完六阶段时不提前进入结算");
            }
        }
        finally {Directory.Delete(root,true);}
    }
}
