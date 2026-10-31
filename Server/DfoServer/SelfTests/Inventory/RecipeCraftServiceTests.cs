using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class RecipeCraftServiceTests
{
    const int Blueprint = 65001, Target = 65002, Output = 65003, Cube = 65004, Cloth = 65005,
        Ordinary = 65006, Npc = 65007, Failed = 65008, ProbabilityUpgrade = 65009;
    static readonly InventorySlot BlueprintSlot = new(InventoryListType.Main, 65);
    static readonly InventorySlot TargetSlot = new(InventoryListType.Main, 9);
    static readonly InventorySlot CubeSlot = new(InventoryListType.Main, 121);
    static readonly InventorySlot ClothSlot = new(InventoryListType.Main, 122);

    public static void RunCore()
    {
        int checks = Count;
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [Blueprint] = new(Blueprint, ItemKind.Consumable, 0, 90, 0, 999, 10, 4, 90, "[trade]", 255, 0, "设计图"),
            [Ordinary] = new(Ordinary, ItemKind.Consumable, 0, 1, 0, 999, 120, 4, 1, "[trade]", 255, 0, "普通图"),
            [Npc] = new(Npc, ItemKind.Consumable, 0, 1, 0, 999, 9999, 4, 1, "[trade]", 255, 0, "NPC配方"),
            [Failed] = new(Failed, ItemKind.Consumable, 0, 1, 0, 999, 0, 4, 1, "[trade]", 255, 0, "概率配方"),
            [ProbabilityUpgrade] = new(ProbabilityUpgrade, ItemKind.Consumable, 0, 1, 0, 999, 0, 4, 1, "[trade]", 255, 0, "概率升级图"),
            [Target] = new(Target, ItemKind.Equipment, 14, 90, 0, 1, 10, 6, 90, "[sealing]", 255, 0, "原装备"),
            [Output] = new(Output, ItemKind.Equipment, 14, 90, 0, 1, 10, 4, 90, "[sealing]", 255, 0, "产物"),
            [Cube] = new(Cube, ItemKind.Material, 0, 1, 0, 999, 0, 3, 1, "[trade]", 255, 0, "晶块"),
            [Cloth] = new(Cloth, ItemKind.Material, 0, 1, 0, 999, 0, 3, 1, "[trade]", 255, 0, "布片")
        });
        var ordinary = new ItemRecipeDefinition(Ordinary, false, [new(Cube, 10), new(Cloth, 2), new(0, 200)], [new(Output, 1)])
            { MakingType = 2, MakingLevel = 1 };
        var making = new ItemMakingSkillCatalog(new PvfLib.ItemMakingSkillFile([new(0, 1), new(20, 20), new(40, 30)], 1, 3, 2,
            [new(1, 4, 6, 9700, 6, 12, 300), new(2, 5, 7, 9700, 6, 12, 300)]));
        var recipes = new ItemRecipeCatalog([new ItemRecipeDefinition(Blueprint, false,
            [new(Target, 1), new(Cube, 100), new(Cloth, 20)], [new(Output, 1)]) { MaintainAbility = "normal", MaintainSocketStat = true },
            ordinary, new(Npc, true, [new(Cloth, 1), new(0, 200)], [new(Cube, 1)]) { Skills = [new(900, 1)] },
            new(Failed, false, [new(Cloth, 1)], [new(Cube, 1)]) { SuccessRate = 100 },
            new(ProbabilityUpgrade, false, [new(Target, 1), new(Cube, 100), new(Cloth, 20)], [new(Output, 1)])
                { MaintainAbility = "normal", SuccessRate = 1000, FailureOutputs = [new(Target, 1), new(Cloth, 1)] }]);
        var service = new InventoryRecipeCraftService(recipes, catalog,
            new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"),
            new GoldLimitCatalog("[gold limit from level]\n0 2000000000\n[/gold limit from level]"), making, random: _ => 0);

        Character Fixture(int cubes = 250, int cloth = 60, byte targetLock = 0, bool withTarget = true, int blueprintItem = Blueprint)
        {
            var items = new List<InventoryItem>
            {
                new(11, BlueprintSlot, new(blueprintItem, ItemKind.Consumable, 1)),
                new(13, CubeSlot, new(Cube, ItemKind.Material, cubes)),
                new(14, ClothSlot, new(Cloth, ItemKind.Material, cloth))
            };
            if (withTarget)
                items.Add(new InventoryItem(12, TargetSlot, new(Target, ItemKind.Equipment, 1)
                {
                    Attr = 7, GenuineUpgrade = 3, EnchantCardId = 900, EmblemSocketCount = 1, Emblem1 = 42,
                    Random0 = new(1, 2, 3), EquipmentLockId = targetLock
                }));
            return new Character(1, 0, "Recipe", 0, new(1, 0, 0, 0), null) { Level = 90, Inventory = new([.. items], Gold: 10000) };
        }

        var character = Fixture();
        var before = character.Inventory;
        InventoryResult Craft(Character role, out RecipeCraftReceipt? detail) => service.Craft(role, 65, 0, 1, out detail);
        var result = Craft(character, out var receipt);
        That(result.Accepted && result.Plan is not null, "配方匹配时接受");
        That(receipt is { RecipeId: Blueprint, Success: true, Products: [{ ItemId: Output, Count: 1 }] }, "回执携带真实产物槽位与数量");
        var committed = new InventoryCommitReceipt(character with { Inventory = result.Plan!.After }, 1, 1);
        byte[] ack = CompoundPackets.Result(result with { Receipt = committed }, receipt);
        int product = 4 + 7 * receipt!.Consumed.Length;
        That(ack.Length == product + 124 && ack[1] == 4 && ack[product - 2] == 1 && ack[product - 1] == 1 &&
            ack[product] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(product + 1)) == TargetSlot.Index &&
            BinaryPrimitives.ReadInt32LittleEndian(ack.AsSpan(product + 3)) == Output, "原生ACK包含消耗、结果标记及完整123B产物行");
        That(ack[product + 14] == 0 && result.Plan.After.Find(TargetSlot)!.Core.SealFlag == 0,
            "保留型升级不因产物模板为sealing而重新封装已解封原件，数据库与完整回包保持一致");
        Reject<InvalidDataException>(() => CompoundPackets.Result(result, receipt), "规划中的物品不能冒充已提交实例发包");
        That(CompoundPackets.Result(InventoryResult.Reject(7)) is [0, 7, 0], "失败回包为0+u16错误码");
        var after = result.Plan!.After;
        That(after.Find(BlueprintSlot) is null, "设计图被整件消耗");
        var crafted = after.Items.Single(item => item.Uid == 12);
        That(crafted.Core.ItemId == Output && crafted.Position == TargetSlot && crafted.Core.Attr == 7 &&
            crafted.Core.GenuineUpgrade == 3 && crafted.Core.EnchantCardId == 900 && crafted.Core.EmblemSocketCount == 1 &&
            crafted.Core.Emblem1 == 42 && crafted.Core.Random0 == new RandomOption(1, 2, 3),
            "产物沿用原实例并保留强化/锻造/附魔/徽章/随机属性");
        That(after.Items.Single(item => item.Uid == 13).Core.Count == 150 &&
            after.Items.Single(item => item.Uid == 14).Core.Count == 40, "只按配方数量扣材料");
        That(before.Items.Length == 4 && before.Find(TargetSlot)!.Core.ItemId == Target && after.Gold == before.Gold, "规划不修改原快照，也不把设计图售价当制作费");

        That(!Craft(Fixture(cubes: 99), out _).Accepted, "材料不足整单拒绝");
        That(!Craft(Fixture(cloth: 19), out _).Accepted, "第二种材料不足同样拒绝");
        That(!Craft(Fixture(targetLock: 1), out _).Accepted, "锁定装备不能制造");
        That(!Craft(Fixture(withTarget: false), out _).Accepted, "缺少被改造装备时拒绝");
        That(!Craft(Fixture(blueprintItem: Cube), out _).Accepted, "槽位不是配方物品时拒绝");
        That(!service.Craft(Fixture(), uint.MaxValue, 0, 1, out _).Accepted &&
            !service.Craft(Fixture(), 65, 2, 1, out _).Accepted && !service.Craft(Fixture(), 65, 0, 0, out _).Accepted,
            "越界来源、未知模式和零数量不产生溢出或费用");

        var pure = Fixture(withTarget: false, blueprintItem: Ordinary) with { Level = 14 };
        var made = Craft(pure, out var madeReceipt);
        var newItem = made.Plan!.After.Items.Single(item => item.Core.ItemId == Output);
        That(made.Accepted && newItem.Uid == 0 && newItem.Position.Index == 9 && newItem.Core.Attr == 4 &&
            made.Plan.After.Gold == 9800 && made.Plan.After.ItemMakingSkills.Points[2] == 1 &&
            madeReceipt!.MakingGain == new ItemMakingSkillGain(2, 0, 1), "纯材料配方新建装备，按输入扣金币并原子累计对应熟练度");
        string[] restrictedRecipeStrings = ["[stackable type]", "[recipe]", "[input item]", "[/input item]",
            "[output item]", "[/output item]", "[set disjoint instance type]"];
        PvfLib.PvfToken[] restrictedTokens = [new(3, 0), new(6, 1), new(0, 1),
            new(3, 2), new(0, Cube), new(0, 10), new(0, Cloth), new(0, 2), new(0, 0), new(0, 200), new(3, 3),
            new(3, 4), new(0, Output), new(0, 1), new(3, 5), new(3, 6)];
        var restrictedDefinition = ItemRecipeCatalog.Parse(PvfLib.ItemRecipeFile.Parse(Ordinary, "stackable/restricted.stk",
            restrictedTokens, index => restrictedRecipeStrings[index]))! with { MakingType = 2, MakingLevel = 1 };
        var restrictedService = new InventoryRecipeCraftService(new([restrictedDefinition]), catalog,
            new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"),
            new GoldLimitCatalog("[gold limit from level]\n0 2000000000\n[/gold limit from level]"), making, random: _ => 0);
        string restrictedBefore = System.Text.Json.JsonSerializer.Serialize(pure);
        var restrictedMade = restrictedService.Craft(pure, 65, 0, 1, out var restrictedReceipt);
        That(restrictedMade.Accepted && restrictedMade.Plan!.After.Items.Single(item => item.Core.ItemId == Output).Core ==
            newItem.Core with { DisjointForbidden = true } && restrictedReceipt!.Products.SequenceEqual(madeReceipt!.Products) &&
            System.Text.Json.JsonSerializer.Serialize(pure) == restrictedBefore,
            "设计图根级分解声明沿实际制作保存为实例限制，完整其他属性、产物与输入快照保持");
        That(!newItem.Core.DisjointForbidden && !newItem.Core.DestructionSoulForbidden,
            "同模板普通设计图产物不推断分解或毁装限制");
        var sentinel = new InventoryItem(99, new(InventoryListType.Main, 10), newItem.Core);
        var withSentinel = pure with { Inventory = pure.Inventory with { Items = pure.Inventory.Items.Add(sentinel) } };
        var separate = restrictedService.Craft(withSentinel, 65, 0, 1, out _);
        That(separate.Accepted && separate.Plan!.After.Find(sentinel.Position) == sentinel &&
            separate.Plan.After.Items.Single(item => item.Uid == 0).Core.DisjointForbidden,
            "只给本次新产物打标，既有同模板哨兵不被补标");
        var batchBlueprint = pure.Inventory.Find(BlueprintSlot)!;
        var batchActor = pure with { Inventory = pure.Inventory with { Items = pure.Inventory.Items.Replace(batchBlueprint,
            batchBlueprint with { Core = batchBlueprint.Core with { Value = 2 } }) } };
        string batchBefore = System.Text.Json.JsonSerializer.Serialize(batchActor);
        That(restrictedService.Craft(batchActor, 65, 0, 2, out _).Error == 23 &&
            System.Text.Json.JsonSerializer.Serialize(batchActor) == batchBefore,
            "装备产物继续遵守原批量限制，拒绝不扣源或材料");
        var materialDefinition = restrictedDefinition with { BlueprintItemId = Npc, CatalogDirect = true,
            Inputs = [new(Cloth, 1), new(0, 200)], Outputs = [new(Cube, 1)], MakingType = null, MakingLevel = 0 };
        var materialService = new InventoryRecipeCraftService(new([materialDefinition]), catalog,
            new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"),
            new GoldLimitCatalog("[gold limit from level]\n0 2000000000\n[/gold limit from level]"), making, random: _ => 0);
        var materialsMade = materialService.Craft(pure, Npc, 1, 3, out var materialsReceipt);
        That(materialsMade.Accepted && materialsReceipt!.Products is [{ ItemId: Cube, Count: 3 }] &&
            materialsMade.Plan!.After.Find(CubeSlot)!.Core == pure.Inventory.Find(CubeSlot)!.Core with { Value = 253 } &&
            materialsMade.Plan.After.Gold == 9400 && materialsMade.Plan.After.Find(ClothSlot)!.Core.Count == 57 &&
            materialsMade.Plan.After.Items.All(item => !item.Core.DisjointForbidden && !item.Core.DestructionSoulForbidden),
            "非装备批量制造保留材料合堆、数量和费用，不向材料传播装备实例限制");
        var keepDefinition = restrictedDefinition with { BlueprintItemId = Blueprint, MakingType = null, MakingLevel = 0,
            Inputs = [new(Target, 1), new(Cube, 100), new(Cloth, 20)], MaintainAbility = "normal", SuccessRate = 1000,
            FailureOutputs = [new(Target, 1), new(Cloth, 1)] };
        InventoryRecipeCraftService KeepService(bool success, bool declared = true) => new(
            new([keepDefinition with { SetsDisjointInstanceType = declared }]), catalog,
            new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"),
            new GoldLimitCatalog("[gold limit from level]\n0 2000000000\n[/gold limit from level]"), making,
            random: _ => success ? 0 : 9999);
        foreach (bool inherited in new[] { false, true })
        {
            var originalTarget = character.Inventory.Find(TargetSlot)!;
            var markedTarget = originalTarget with { Core = originalTarget.Core with
                { DisjointForbidden = inherited, DestructionSoulForbidden = inherited } };
            var actor = character with { Inventory = character.Inventory with
                { Items = character.Inventory.Items.Replace(originalTarget, markedTarget) } };
            string unchanged = System.Text.Json.JsonSerializer.Serialize(actor);
            var kept = KeepService(true).Craft(actor, 65, 0, 1, out _);
            var expectedCore = markedTarget.Core with { ItemId = Output, EmblemSocketCount = 0, Emblem1 = 0, Emblem2 = 0,
                SealFlag = 1, DisjointForbidden = true };
            That(kept.Accepted && kept.Plan!.After.Find(TargetSlot) == markedTarget with { Core = expectedCore },
                "保留属性型成功制作保留UID及完整核心、旧毁装限制，仅新增声明的分解限制");
            var failed = KeepService(false).Craft(actor, 65, 0, 1, out var restrictedFailureReceipt);
            That(failed.Accepted && restrictedFailureReceipt is { Success: false } && failed.Plan!.After.Find(TargetSlot) == markedTarget &&
                System.Text.Json.JsonSerializer.Serialize(actor) == unchanged,
                "失败返原件不施加成功产物限制，也不丢失原有实例限制或修改输入");
            var ordinaryKeep = KeepService(true, declared: false).Craft(actor, 65, 0, 1, out _);
            That(ordinaryKeep.Accepted && ordinaryKeep.Plan!.After.Find(TargetSlot)!.Core ==
                expectedCore with { DisjointForbidden = inherited }, "无声明的属性保留图沿用原实例限制，不主动解除或补标");
        }
        var sealedInventory = made.Plan.After with { Items = made.Plan.After.Items.Select(item => item.Uid == 0 ? item with { Uid = 1000 } : item).ToImmutableArray() };
        byte[] sealedAck = CompoundPackets.Result(made with { Receipt = new(pure with { Inventory = sealedInventory }, 1, 1) }, madeReceipt);
        That(newItem.Core.SealFlag == 1 && sealedAck[4 + 7 * madeReceipt!.Consumed.Length + 14] == 1,
            "新造产物按EQU的sealing创建封装状态，熟练强化重建后仍在123B物品行下发SealFlag=1");
        That(made.Plan.After.Find(CubeSlot)!.Core.Count == 240 && made.Plan.After.Find(ClothSlot)!.Core.Count == 58,
            "第一输入同样是材料，不再丢失或误判为原装备");
        That(service.Craft(pure, Ordinary, 1, 1, out _).Error == 179, "物品编号模式不能绕过背包设计图消费");
        That(Craft(pure with { Inventory = pure.Inventory with { Gold = 199 } }, out _).Error == 14, "金币不足整单拒绝");
        var full = pure with { Inventory = pure.Inventory with { Items = pure.Inventory.Items.AddRange(Enumerable.Range(9, 56)
            .Select(slot => new InventoryItem(100 + slot, new(InventoryListType.Main, (short)slot), new(Target, ItemKind.Equipment, 1)))) } };
        That(Craft(full, out _).Error == 4 && pure.Inventory.ItemMakingSkills.Points[2] == 0,
            "无产物空间时不消费源、不抽强化、不增加熟练度");

        var npc = service.Craft(Fixture(withTarget: false), Npc, 1, 3, out var npcReceipt);
        That(npc.Accepted && npc.Plan!.After.Gold == 9400 && npc.Plan.After.Find(ClothSlot)!.Core.Count == 57 &&
            npc.Plan.After.Find(CubeSlot)!.Core.Count == 253 && npc.Plan.After.Find(BlueprintSlot)!.Core.Count == 1 &&
            npcReceipt!.MakingGain is null && npcReceipt.Products is [{ ItemId: Cube, Count: 3 }],
            "NPC模式批量按输入扣料、扣金币并合堆，不要求或消耗实体设计图、不误加制作熟练度");
        var missingNpc = service.Craft(Fixture(cloth: 2), Npc, 1, 3, out _);
        That(missingNpc.Error == 19 && !service.Craft(Fixture(), Npc, 1, ushort.MaxValue, out _).Accepted,
            "NPC整批缺料或超大数量无部分制作");
        var chanceService = new InventoryRecipeCraftService(recipes, catalog, new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"),
            new GoldLimitCatalog("[gold limit from level]\n0 2000000000\n[/gold limit from level]"), making, random: maximum => maximum - 1);
        var chance = chanceService.Craft(Fixture(blueprintItem: Failed), 65, 0, 1, out var chanceReceipt);
        That(chance.Accepted && chanceReceipt is { Success: false, Products.IsEmpty: true } &&
            chance.Plan!.After.Find(BlueprintSlot) is null && chance.Plan.After.Find(ClothSlot)!.Core.Count == 59,
            "原生制作失败仍消费设计图和材料，回包结果2但不伪造产物");
        var attempted = Fixture(blueprintItem: ProbabilityUpgrade);
        var failedUpgrade = chanceService.Craft(attempted, 65, 0, 1, out var failedReceipt);
        That(failedUpgrade.Accepted && failedReceipt is { Success: false, Products.Length: 2 } &&
            failedUpgrade.Plan!.After.Find(TargetSlot) == attempted.Inventory.Find(TargetSlot) &&
            failedUpgrade.Plan.After.Find(ClothSlot)!.Core.Count == 41 && failedUpgrade.Plan.After.Find(CubeSlot)!.Core.Count == 150,
            "概率失败原UID与完整打造原样返还，额外失败材料只增加一次，成功费用仍正常消费");
        var failedCommitted = failedUpgrade with { Receipt = new(attempted with { Inventory = failedUpgrade.Plan!.After }, 1, 1) };
        var failureAck = CompoundPackets.Result(failedCommitted, failedReceipt);
        That(failureAck[2 + 7 * failedReceipt!.Consumed.Length] == 2 &&
            failureAck[3 + 7 * failedReceipt.Consumed.Length] == 2, "失败也在原生ACK携带真实返还产物，不伪装成功");
        InventoryResult At(long sample, out RecipeCraftReceipt? details) => new InventoryRecipeCraftService(recipes, catalog,
            new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"),
            new GoldLimitCatalog("[gold limit from level]\n0 2000000000\n[/gold limit from level]"), making,
            random: maximum => { That(maximum == 10000, "配方概率抽样使用万分母"); return sample; })
            .Craft(attempted, 65, 0, 1, out details);
        var lower = At(999, out var lowerReceipt);
        var upper = At(1000, out var upperReceipt);
        That(lowerReceipt!.Success && !upperReceipt!.Success && lower.Plan!.After.Find(TargetSlot)!.Core is
            { ItemId: Output, SealFlag: 1, EmblemSocketCount: 0, Attr: 7, GenuineUpgrade: 3, EnchantCardId: 900 } &&
            upper.Plan!.After.Find(TargetSlot) == attempted.Inventory.Find(TargetSlot),
            "万分值1000表示百分之十，成功按产物封装及独立孔位规则，失败保持完整原件");
        var expired = pure with { Inventory = pure.Inventory with { Items = pure.Inventory.Items.Select(item =>
            item.Position == CubeSlot ? item with { Core = item.Core with { ExpireTime = 1 } } : item).ToImmutableArray() } };
        That(!Craft(expired, out _).Accepted, "过期输入不能参加制作");
        var progressed = pure with { Inventory = pure.Inventory with { ItemMakingSkills = ItemMakingSkillState.Empty.Set(2, 19) } };
        var growth = ItemMakingSkillPolicy.Gain(progressed, ordinary, making, maximum => maximum - 1);
        That(growth.State.Points[2] == 20 && growth.Gain == new ItemMakingSkillGain(2, 0, 1), "角色等级限制钳住跨阈值收益，通知实际增量");
        growth = ItemMakingSkillPolicy.Gain(pure with { Inventory = progressed.Inventory with { ItemMakingSkills = growth.State } }, ordinary, making, _ => 0);
        That(growth.Gain == new ItemMakingSkillGain(2, 22, 0), "达到角色等级阈值时消费仍可执行，熟练度不再增长");
        growth = ItemMakingSkillPolicy.Gain(progressed with { Level = 90, Inventory = progressed.Inventory with { ItemMakingSkills = ItemMakingSkillState.Empty.Set(2, 40) } }, ordinary, making, _ => 0);
        That(growth.Gain == new ItemMakingSkillGain(2, 13, 0) && growth.State.Points[2] == 40, "制作等级超过推荐两级时无经验，不能倒扣");
        That(making.Level(0, 1) == 1 && making.Level(40, 14) == 1 && making.Level(40, 30) == 3 &&
            making.Upgrade(1, maximum => maximum - 1) == 12 && making.Upgrade(1, _ => 0) == 4,
            "制作等级同时受点数与角色等级限制，强化按常见和稀有区间取值");
        That(Convert.ToHexString(CompoundPackets.MakingGain(new(2, 0, 3))) == "00020300" &&
            CompoundPackets.MakingSkills(ItemMakingSkillState.Empty).Length == 34,
            "本版351为11类绝对点数、352为状态类别和u16增量");
        var noMaking = new InventoryRecipeCraftService(recipes, catalog,
            new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"),
            new GoldLimitCatalog("[gold limit from level]\n0 2000000000\n[/gold limit from level]"),
            new ItemMakingSkillCatalog((PvfLib.ItemMakingSkillFile?)null));
        That(!noMaking.Craft(pure, 65, 0, 1, out _).Accepted && noMaking.Craft(pure, Npc, 1, 1, out _).Accepted,
            "缺熟练度资源只关闭带声明的配方，不关闭NPC普通材料制作");
        growth = ItemMakingSkillPolicy.Gain(pure with { Level = 95, Inventory = pure.Inventory with
            { ItemMakingSkills = ItemMakingSkillState.Empty.Set(2, 40) } }, ordinary, making, _ => 0);
        That(growth.Gain == new ItemMakingSkillGain(2, 22, 0), "角色满级沿当前PVF末项限制，不照抄旧70级常量");
        growth = ItemMakingSkillPolicy.Gain(pure with { Level = 90, Inventory = pure.Inventory with
            { ItemMakingSkills = ItemMakingSkillState.Empty.Set(2, 1000) } }, ordinary, making, _ => 0);
        That(growth.Gain == new ItemMakingSkillGain(2, 95, 0) && growth.State.Points[2] == 1000, "1000点上限通知为95，不能溢出或倒扣");
        long deadline = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds();
        var timed = pure with { Inventory = pure.Inventory with { Items = pure.Inventory.Items.Select(item =>
            item.Position == BlueprintSlot ? item with { Core = item.Core with { ExpireTime = checked((int)deadline) } } : item).ToImmutableArray() } };
        var timedPlan = Craft(timed, out _).Plan!;
        That(timedPlan.CharacterPrecondition is { } guard && !guard.IsExpired(deadline - 1) && guard.IsExpired(deadline),
            "设计图期限在提交前再次复核，不能使用过期前的旧规划");
        // 请求宽度与字段：实机 9 字节 610000000001000001 = 槽位97/方式0/数量1/尾 00 01。
        var extended = DfoServer.Network.Parsers.Inventory.CompoundRequests.Read(Convert.FromHexString("610000000001000001"));
        That(extended.Raw == 97 && extended.Mode == 0 && extended.Count == 1, "9字节请求解出槽位/方式/数量");
        var short7 = DfoServer.Network.Parsers.Inventory.CompoundRequests.Read(Convert.FromHexString("61000000000100"));
        That(short7.Raw == 97 && short7.Mode == 0 && short7.Count == 1, "7字节请求解出同样字段");
        Reject<InvalidDataException>(() => DfoServer.Network.Parsers.Inventory.CompoundRequests.Read(
            Convert.FromHexString("610000000001000002")), "9字节尾部不是0001时拒绝");
        Reject<InvalidDataException>(() => DfoServer.Network.Parsers.Inventory.CompoundRequests.Read(
            Convert.FromHexString("610000000000000001")), "数量为零时拒绝");
        Reject<InvalidDataException>(() => DfoServer.Network.Parsers.Inventory.CompoundRequests.Read(
            Convert.FromHexString("61000000020100")), "来源方式不是0或1时拒绝");
        Console.WriteLine($"PASS {Count - checks} 配方制造行为断言");
    }
}
