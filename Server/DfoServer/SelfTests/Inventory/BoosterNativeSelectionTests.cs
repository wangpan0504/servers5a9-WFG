using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Game.Accounts;
using DfoServer.Game.Session;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class BoosterNativeSelectionTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);

    public static void RunCore()
    {
        int checks = Count;
        var fixture = new Fixture();
        var role = fixture.Role();
        string before = JsonSerializer.Serialize(role);
        var result = fixture.Service.Open(role, new(65, 1, new(0, 0, [10007742])), out var receipt, Now);
        That(result.Accepted && result.Plan is not null && receipt is not null,
            "原生creature选择行按真实Consumable模板发宝珠，不把来源标签当实体Kind");
        var expected = role.Inventory with
        {
            Items = [new(0, new(InventoryListType.Main, 65), new(10007742, ItemKind.Consumable, 1)), role.Inventory.Items[1]]
        };
        That(JsonSerializer.Serialize(result.Plan!.After) == JsonSerializer.Serialize(expected) &&
            JsonSerializer.Serialize(role) == before && result.Plan.ChangedSlots.SequenceEqual(new[] { new InventorySlot(InventoryListType.Main, 65) }) &&
            result.Plan.ChangedCurrencies.IsEmpty && result.Plan.OverflowItems.IsEmpty &&
            receipt!.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(10007742, 1) }),
            "宝珠完整字面库存预期保留哨兵、普通核心、期限及UID，规划不就地扣源");
        OrdinaryKinds();
        StacksAndCapacity();
        SourcesAndExpiry();
        OwnersAndTypedRows();
        FullGroupEcho();
        Console.WriteLine($"PASS {Count - checks} 原生选择普通类型、完整核心、容量、期限与owner边界断言");
    }

    static void OrdinaryKinds()
    {
        foreach (var (kind, list, slot, value) in new[]
        {
            (ItemKind.Material, InventoryListType.Main, (short)121, 1),
            (ItemKind.CreatureEquipment, InventoryListType.Pet, (short)140, 999_999_998)
        })
        {
            var fixture = new Fixture();
            fixture.Put(Template(10007742, kind) with { StackLimit = kind == ItemKind.CreatureEquipment ? 1 : 1000,
                Durability = 35, AttachType = "[sealing]" });
            var role = fixture.Role();
            var result = fixture.Service.Open(role, new(65, 1, new(0, 0, [10007742])), out var receipt, Now);
            var reward = new InventoryItem(0, new(list, slot), new(10007742, kind, value) { Durability = 35, SealFlag = 1 });
            var expected = role.Inventory with { Items = [role.Inventory.Items[1], reward] };
            That(result.Accepted && Json(result.Plan!.After) == Json(expected) && receipt!.Rewards.Single() == new BoosterGrantedItem(10007742, 1),
                "原生选择按Material或CreatureEquipment完整字面核心、正确页和无附属详情创建 " + kind);
        }
    }

    static void StacksAndCapacity()
    {
        var fixture = new Fixture();
        var source = fixture.Role().Inventory.Items[0];
        var stack = new InventoryItem(20000, new(InventoryListType.Main, 67), new(10007742, ItemKind.Consumable, 999));
        var role = fixture.Role() with { Inventory = new([source, stack]) };
        var result = fixture.Service.Open(role, new(65, 1, new(0, 0, [10007742])), out _, Now);
        That(result.Accepted && Json(result.Plan!.After) == Json(role.Inventory with { Items = [stack with { Core = stack.Core with { Value = 1000 } }] }),
            "原生宝珠合并兼容堆保留UID和完整属性，恰到堆叠上限");
        Reject(fixture, role with { Inventory = new([source, stack with { Core = stack.Core with { Value = 1000 } }]) },
            "原生宝珠入库上限不靠空槽拆分绕过");
        var distinct = stack with { Core = stack.Core with { SortLockFlag = 1, Extension27 = 123 } };
        role = role with { Inventory = new([source, distinct]) };
        result = fixture.Service.Open(role, new(65, 1, new(0, 0, [10007742])), out _, Now);
        That(result.Accepted && Json(result.Plan!.After) == Json(role.Inventory with { Items =
            [new(0, new(InventoryListType.Main, 65), new(10007742, ItemKind.Consumable, 1)), distinct] }),
            "不兼容完整属性堆保持原样，新增宝珠只复用已耗尽源槽");
        var full = Enumerable.Range(65, 56).Select(slot => new InventoryItem(slot + 10000, new(InventoryListType.Main, (short)slot),
            new(10007740, ItemKind.Consumable, 1))).ToArray();
        role = fixture.Role() with { Inventory = new([.. full]) };
        result = fixture.Service.Open(role, new(65, 1, new(0, 0, [10007742])), out _, Now);
        That(result.Accepted && result.Plan!.After.Items.Length == 56 && result.Plan.After.Find(source.Position) is { Uid: 0, Core.ItemId: 10007742 },
            "原生宝珠满页可复用最后源格，无额外预留格");
        Reject(fixture, role with { Inventory = new([full[0] with { Core = full[0].Core with { Value = 2 } }, .. full[1..]]) },
            "未耗尽源格的满页拒绝无消耗");
        fixture.Put(Template(10007742, ItemKind.CreatureEquipment) with { StackLimit = 1 });
        var pets = Enumerable.Range(140, 49).Select(slot => new InventoryItem(slot + 20000, new(InventoryListType.Pet, (short)slot),
            new(10007742, ItemKind.CreatureEquipment, 999_999_998))).ToArray();
        Reject(fixture, fixture.Role() with { Inventory = new([source, .. pets]) }, "宠物装备页满拒绝，不借Main源格或创建邮件");
        role = fixture.Role() with { Inventory = new([source, .. pets[..^1]]) };
        result = fixture.Service.Open(role, new(65, 1, new(0, 0, [10007742])), out _, Now);
        That(result.Accepted && result.Plan!.After.Find(new(InventoryListType.Pet, 188)) is { Uid: 0, Core.Value: 999_999_998, Avatar: null, Creature: null },
            "宠物装备页最后合法槽完整入库");
    }

    static void SourcesAndExpiry()
    {
        var fixture = new Fixture();
        for (byte job = 0; job < 16; job++)
        {
            fixture.Put(Template(10007740) with { Jobs = (ushort)(1 << job), MinimumLevel = 20, MaximumLevel = 20 });
            var role = fixture.Role() with { Job = job, Level = 20, GrowType = 255 };
            var source = role.Inventory.Items[0] with { Position = new(InventoryListType.Main, 3), Core = role.Inventory.Items[0].Core with { SortLockFlag = 1 } };
            role = role with { Inventory = new([source]) };
            var result = fixture.Service.Open(role, new(3, 1, new(0, 0, [10007742])), out _, Now);
            That(result.Accepted && result.Plan!.After.Find(new(InventoryListType.Main, 65)) is { Core.Kind: ItemKind.Consumable },
                "合法源职业与等级、快捷栏和排序锁不增加奖励资格限制 " + job);
            Reject(fixture, role with { Level = 19 }, "低于源最低等级不扣源", 3);
            Reject(fixture, role with { Level = 21 }, "高于源最高等级不扣源", 3);
            Reject(fixture, role with { Job = (byte)((job + 1) % 16) }, "源职业不符不扣源", 3);
        }
        fixture.Put(Template(10007740));
        foreach (var source in new[] { Template(10007740) with { UseForbidden = true },
            Template(10007740) with { RequiredSkill = ushort.MaxValue } })
        {
            fixture.Put(source);
            Reject(fixture, fixture.Role(), "源禁用和未满足源技能要求不扣源");
        }
        fixture.Put(Template(10007740));
        foreach (var list in new[] { InventoryListType.PersonalCargo, InventoryListType.AccountCargo })
        {
            var role = fixture.Role();
            role = role with { Inventory = new([role.Inventory.Items[0] with { Position = new(list, 0) }]) };
            Reject(fixture, role, "仓库中的同ID源不能伪装成Main开箱来源 " + list, 0);
        }
        foreach (var core in new[] { new ItemCore(10007740, ItemKind.Consumable, 1) { EquipmentLockId = 1 },
            new ItemCore(10007740, ItemKind.Consumable, 1) { ExpireTime = 2_000_000_000 } })
            Reject(fixture, fixture.Role() with { Inventory = new([new(10000, new(InventoryListType.Main, 65), core)]) }, "源锁与源过期拒绝无消耗");
        foreach (var (expiration, expected) in new[] { (new ItemExpiration(), 0), (new ItemExpiration(Days: 2), 2_000_172_800),
            (new ItemExpiration(DailyDelete: 1), 2_000_066_400) })
        {
            fixture.Put(Template(10007742) with { Expiration = expiration });
            var result = fixture.Service.Open(fixture.Role(), new(65, 1, new(0, 0, [10007742])), out _, Now);
            That(result.Accepted && result.Plan!.After.Find(new(InventoryListType.Main, 65))!.Core ==
                new ItemCore(10007742, ItemKind.Consumable, 1) { ExpireTime = expected }, "普通期限完整保留 " + expiration);
        }
        foreach (var expiration in new[] { new ItemExpiration(AbsoluteTime: 2_000_000_000), new ItemExpiration(DailyDelete: 2), new ItemExpiration(WeeklyDeleteDay: 7) })
        {
            fixture.Put(Template(10007742) with { Expiration = expiration });
            Reject(fixture, fixture.Role(), "奖励已过期或周期删除语义未支持时整次拒绝");
        }
    }

    static void OwnersAndTypedRows()
    {
        foreach (int id in new[] { 1, 50001, 10007742 })
        {
            var fixture = new Fixture();
            fixture.Put(Template(id));
            fixture.Definitions[10007740] = Definition(id);
            if (id == 10007742)
                fixture.Put(Template(id) with { AcquisitionContract = new(id, 22, 86400) });
            Reject(fixture, fixture.Role(), "钱包、碎片或获取契约owner不转普通实体 " + id, target: id);
        }
        foreach (var mode in new[] { "premium", "contract", "activation", "avatar", "required" })
        {
            var fixture = new Fixture();
            if (mode == "premium") fixture.Put(Template(10007742) with { IsPremiumService = true });
            if (mode == "contract") fixture.Service = fixture.CreateService(new(new Dictionary<int, PremiumContract>(), recognizedItems: [10007742]));
            if (mode == "activation") fixture.SetActivations([10007742]);
            if (mode == "avatar") fixture.Put(Template(10007742, ItemKind.Avatar) with { StackLimit = 1 });
            if (mode == "required")
            {
                var definition = fixture.Definitions[10007740];
                var selection = definition.Selection!;
                var category = selection.Categories[0];
                fixture.Definitions[10007740] = definition with { Selection = selection with { Categories =
                    [category with { Rewards = [category.Rewards[0] with { RequiredKind = ItemKind.Creature }] }] } };
            }
            Reject(fixture, fixture.Role(), "原生选择仍保留owner、Avatar额外选择及显式类型保护 " + mode);
        }
        var random = new Fixture();
        random.Definitions[10007740] = new([new(1, [new(10007742, 1, 1)]) { RequiredKind = ItemKind.Creature }]);
        var role = random.Role();
        string before = Json(role);
        var rejected = random.Service.Open(role, new(65, 1), out var receipt, Now);
        That(!rejected.Accepted && rejected.Plan is null && receipt is null && Json(role) == before,
            "随机Creature池仍预检全部候选，不继承选择行的宽松类型解释");
    }

    static void FullGroupEcho()
    {
        var fixture = new Fixture();
        fixture.Definitions[10007740] = Definition(10007742, 0);
        var role = fixture.Role();
        string before = Json(role);
        var result = fixture.Service.Open(role, new(65, 1, new(0, 0, [10007742])), out var receipt, Now);
        var expected = role.Inventory with { Items =
            [new(0, new(InventoryListType.Main, 65), new(10007742, ItemKind.Consumable, 1)), role.Inventory.Items[1]] };
        That(result.Accepted && Json(result.Plan!.After) == Json(expected) && Json(role) == before &&
            receipt!.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(10007742, 1) }),
            "原生count0单项分类按完整回显发奖，不能假定所有源都是count1");
        foreach (var selection in new BoosterSelectionRequest[] { new(0, 0, []), new(0, 0, [10007742, 10007742]),
            new(0, 0, [10007743]), new(1, 0, [10007742]) })
        {
            var rejected = fixture.Service.Open(role, new(65, 1, selection), out receipt, Now);
            That(!rejected.Accepted && rejected.Plan is null && receipt is null && Json(role) == before,
                "原生count0拒绝省略、重复、外来ID与跨分类回显且源不变");
        }
    }

    static string Json(object value) => JsonSerializer.Serialize(value);

    public static void RunResources(TownCatalog towns, CharacterCatalog characters, ItemResources resources, DateTimeOffset now)
    {
        int checks = Count, rows = 0, consumables = 0, petEquipment = 0;
        int[] sources = [10007740, 10007999, 10008215, 10089751, 10089752, 10089753, 10089754, 10098016];
        var ids = new HashSet<int>();
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        foreach (int sourceId in sources)
        {
            var sourceTemplate = resources.Items.Get(sourceId);
            var definition = resources.Boosters.Get(sourceId);
            That(definition.Available && definition.PendingRule is null, "真实普通类型源保持Available且无Pending " + sourceId + ":" + definition.PendingRule);
            That(definition.Selection is not null, "真实普通类型源保留选择定义 " + sourceId);
            var selection = definition.Selection!;
            That(selection.SelectionCount is 0 or 1 && selection.Categories.All(category => category.PendingRule is null),
                "真实源保留count0全领或count1单选及完整分类 " + sourceId + ":" + selection.SelectionCount);
            var legalRoles = ResourceRoles(towns, characters, sourceTemplate, now).ToArray();
            That(legalRoles.Length != 0, "真实源至少存在一个已实现且满足资源职业/等级/技能/期限的角色 " + sourceId);
            foreach (var category in selection.Categories)
                foreach (var choice in category.Rewards)
                {
                    That(selection.SelectionCount != 0 || category.Rewards.Length == 1,
                        "真实count0六分类各为单元素完整向量，不跨分类拼接 " + sourceId);
                    int[] echo = selection.SelectionCount == 0 ? category.Rewards.Select(row => row.ItemId).ToArray() : [choice.ItemId];
                    rows++;
                    ids.Add(choice.ItemId);
                    var template = resources.Items.Get(choice.ItemId);
                    That(choice.NativeCreatureSelectionRow && choice.RequiredKind is null &&
                        template.Kind is ItemKind.Consumable or ItemKind.CreatureEquipment, "真实36行保留原生选择来源而不推断Kind " + choice.ItemId);
                    if (template.Kind == ItemKind.Consumable) consumables++; else petEquipment++;
                    foreach (var legal in legalRoles)
                        foreach (byte grow in new byte[] { 0, 1, 15, 16, 255 })
                        {
                            var role = legal with { GrowType = grow };
                            string before = Json(role);
                            var result = service.Open(role, new(65, 1, new(category.Category, category.Subcategory, echo)), out var receipt, now);
                            var position = template.Kind == ItemKind.Consumable ? new InventorySlot(InventoryListType.Main, 65) : new(InventoryListType.Pet, 140);
                            var expectedCore = new ItemCore(choice.ItemId, template.Kind,
                                template.Kind == ItemKind.Consumable ? choice.Count : 999_999_998)
                            {
                                Durability = template.Durability,
                                SealFlag = template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0,
                                ExpireTime = ExpectedExpiration(template.Expiration, now)
                            };
                            var expected = role.Inventory with { Items = [new(0, position, expectedCore)] };
                            That(result.Accepted && result.Plan is not null && Json(result.Plan.After) == Json(expected) &&
                                receipt!.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(choice.ItemId, choice.Count) }) &&
                                result.Plan.ChangedCurrencies.IsEmpty && result.Plan.OverflowItems.IsEmpty && Json(role) == before,
                                $"真实普通选择完整字面库存/容器/质量/期限/详情/不就地扣源 {sourceId}:{choice.ItemId}:job{role.Job}:grow{grow}");
                        }
                }
        }
        That(rows == 36 && ids.Count == 36 && consumables == 33 && petEquipment == 3 &&
            new[] { 10006762, 10006763, 10006764 }.All(ids.Contains), "八源真实分母为33Consumable和3CreatureEquipment，不由样例推及所有资源");
        SessionGuards(towns, characters, resources, service, now);
        Console.WriteLine($"PASS {Count - checks} 八源36项原生普通选择真实资源、源职业等级资格与合成Grow边界断言");
    }

    static void SessionGuards(TownCatalog towns, CharacterCatalog characters, ItemResources resources, InventoryBoosterService service, DateTimeOffset now)
    {
        string root = Path.Combine(Path.GetTempPath(), "native-selection-guards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (int sourceId in new[] { 10007740, 10007999 })
            {
                var role = ResourceRoles(towns, characters, resources.Items.Get(sourceId), now).First();
                var category = resources.Boosters.Get(sourceId).Selection!.Categories.First();
                var request = new BoosterOpenRequest(65, 1, new(category.Category, category.Subcategory, [category.Rewards[0].ItemId]));
                var repository = new CharacterRepository(Path.Combine(root, sourceId + ".db"));
                using var owner = new AccountOwners().Acquire("native-guard", () => { });
                // 建角种子由Repository分配UID，域夹具的占位UID不能当成既有实例。
                repository.Create(owner, new("NativeGuard", towns.Room, towns.InitialReturn)
                { Job = role.Job, Level = role.Level, Skills = role.Skills,
                    Inventory = role.Inventory with { Items = [.. role.Inventory.Items.Select(item => item with { Uid = 0 })] } });
                var session = new GameSession(repository, towns, owner);
                session.Select(0);
                InventoryResult Open(Character current) => service.Open(current, request, out _, now);
                void UnchangedRejection(BoosterUsePlacesDefinition places, string name)
                {
                    var before = session.Selected;
                    string saved = Json(repository.List(owner));
                    var result = session.PlanAndCommitBooster(Open, places);
                    That(!result.Accepted && result.Plan is null && ReferenceEquals(before, session.Selected) && saved == Json(repository.List(owner)), name);
                }
                UnchangedRejection(resources.Boosters.UsePlaces(sourceId), "未Ready时两种新Kind不能绕过Session提交资格 " + sourceId);
                session.MarkReady();
                UnchangedRejection(new(PendingRule: "未知地点"), "真实源搭配未知地点声明仍原子拒绝 " + sourceId);
                That(session.LeaveRoom(), "通过正式出口进入Town以验证房间限定 guard");
                UnchangedRejection(new(SeriaRoom: true), "合成房间限定不能被新Kind绕过，Town拒绝且库存版本均不变 " + sourceId);
                var realPlaces = resources.Boosters.UsePlaces(sourceId);
                if (realPlaces.SeriaRoom && !realPlaces.Village || realPlaces.PendingRule is not null)
                    UnchangedRejection(realPlaces, "实际源若限房间或未知地点，Town仍拒绝 " + sourceId);
                else
                    That(session.PlanAndCommitBooster(Open, realPlaces).Accepted, "实际源允许Town时经真实Session提交且只消耗一次 " + sourceId);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    // 枚举已实现职业并按源资源选择合法级别，不改写模板或绕过源资格。
    internal static IEnumerable<Character> ResourceRoles(TownCatalog towns, CharacterCatalog characters, ItemTemplate source, DateTimeOffset now)
    {
        for (byte job = 0; job < 16; job++)
        {
            CharacterProfile profile;
            try { profile = characters.Get(job); }
            catch (InvalidDataException) { continue; }
            if ((source.Jobs & (1 << job)) == 0) continue;
            byte level = Math.Max((byte)1, source.MinimumLevel);
            if (level > source.MaximumLevel) continue;
            var item = new InventoryItem(10000, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(source, utcNow: now));
            var role = new Character(1, 0, "NativeReal", 0, towns.Room, towns.InitialReturn)
            { Job = job, Level = level, Skills = profile.Skills, Inventory = new([item]) };
            That(InventoryAvatarPackageService.SourceEligible(role, item, source) && !item.IsExpired(now.ToUnixTimeSeconds()),
                "真实源资源资格不能由测试开关放宽 " + source.Id + ":" + job);
            yield return role;
        }
    }

    // 按文档期限规则独立计算期望，不调用生产Create或Resolve构造奖励oracle。
    static int ExpectedExpiration(ItemExpiration expiry, DateTimeOffset now)
    {
        That(expiry.SupportsCreation, "真实奖励期限已支持创建");
        long deadline = expiry.Days > 0 ? now.ToUnixTimeSeconds() + expiry.Days * 86400L : expiry.AbsoluteTime;
        if (deadline > 0) deadline = Math.Min(deadline, 2_145_823_200);
        if (expiry.DailyDelete == 1)
        {
            var local = now.ToOffset(TimeSpan.FromHours(8));
            var reset = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, local.Offset);
            if (reset <= now) reset = reset.AddDays(1);
            long daily = Math.Min(reset.ToUnixTimeSeconds(), 2_145_823_200);
            deadline = deadline > 0 ? Math.Min(deadline, daily) : daily;
        }
        if (expiry.WeeklyDeleteDay is { } weekday)
        {
            var local = now.ToOffset(TimeSpan.FromHours(8));
            var reset = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, local.Offset);
            while (reset <= now || (int)reset.DayOfWeek != weekday) reset = reset.AddDays(1);
            long weekly = Math.Min(reset.ToUnixTimeSeconds(), 2_145_823_200);
            deadline = deadline > 0 ? Math.Min(deadline, weekly) : weekly;
        }
        return checked((int)deadline);
    }

    static void Reject(Fixture fixture, Character role, string name, short slot = 65, int target = 10007742)
    {
        string before = Json(role);
        var result = fixture.Service.Open(role, new(slot, 1, new(0, 0, [target])), out var receipt, Now);
        That(!result.Accepted && result.Plan is null && receipt is null && Json(role) == before, name);
    }

    static BoosterDefinition Definition(int target, int selectionCount = 1) => BoosterCatalog.Parse($"[stackable type]\n`[booster selection]` 0\n[booster selection num]\n{selectionCount}\n" +
        $"[booster select category]\n0 0\n[creature]\n{target} 1\n[/creature]\n[/booster select category]");

    static ItemTemplate Template(int id, ItemKind kind = ItemKind.Consumable) =>
        new(id, kind, 0, 1, 0, 1000, 1, 2, 1, "[free]", ushort.MaxValue, 0, "测试宝珠", "[enchant waste] 0");

    sealed class Fixture
    {
        public ItemCatalog Items { get; } = new(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        public BoosterCatalog Boosters { get; }
        public ConcurrentDictionary<int, BoosterDefinition> Definitions { get; } = new();
        public InventoryBoosterService Service { get; set; }
        public void Put(ItemTemplate template) => Items.PrefillTemplates([new(template.Id, template)]);
        public void SetActivations(int[] ids) => typeof(BoosterCatalog).GetField("<DirectActivationItems>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Boosters, ids.ToFrozenSet());
        public InventoryBoosterService CreateService(PremiumContractCatalog? contracts = null) => new(Items, Boosters,
            new GoldLimitCatalog("[gold limit from level]\n0 1000000\n[/gold limit from level]"),
            new EpicPieceCatalog("[piece list]\n50000 50001\n[/piece list]"), contracts: contracts);

        public Fixture()
        {
            Items.PrefillTemplates([new(10007740, Template(10007740)), new(10007742, Template(10007742))]);
            // 无资源夹具只预填正式定义缓存；不为测试新增生产构造入口或访问真实归档。
            Boosters = (BoosterCatalog)RuntimeHelpers.GetUninitializedObject(typeof(BoosterCatalog));
            typeof(BoosterCatalog).GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Boosters, Definitions);
            SetActivations([]);
            Definitions[10007740] = Definition(10007742);
            Service = CreateService();
        }

        public Character Role() => new(1, 0, "NativeSelection", 0, new(1, 0, 10, 10), null)
        {
            Inventory = new([new(10000, new(InventoryListType.Main, 65), new(10007740, ItemKind.Consumable, 1)),
                new(10001, new(InventoryListType.Main, 66), new(10007740, ItemKind.Consumable, 1))])
        };
    }
}
