using System.Collections.Immutable;
using System.Reflection;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class PromotionRecipeConsumerTests
{
    const int Source = 880012001, Equipment = 880012002, Material = 880012003,
        Unknown = 880012004, Equipment2 = 880012005;

    public static void RunResources(ItemResources resources)
    {
        // Exact shipped evidence for the selected audit leaf. This records only the
        // typed contract proved by the probe; its downstream templates still pass
        // through the same consumer checks below when selected by an activity.
        var exact = resources.ItemRecipes.Get(7253);
        That(exact is { BlueprintItemId: 7253, CatalogDirect: false, MaintainAbility: null,
            MakingType: null, Skills.IsEmpty: true, FailureOutputs.IsEmpty: true } &&
            exact.Inputs is [{ ItemId: 7250, Count: 20 }] &&
            exact.Outputs is [{ ItemId: 7254, Count: 1 }],
            "资源7253 typed配方合同保持精确输入输出与空技能/失败分支");

        Fixture New() => new(resources);
        var valid = New();
        That(valid.Validate() is null, "typed实体配方连接完整成功产物与非零材料，金币不是物品根");
        var direct = New(); direct.Definition = direct.Definition with { CatalogDirect = true };
        That(direct.Validate() is null, "允许ID模式的typed配方仍保实体图消费者，不重写模式资格");
        var goldOnly = New(); goldOnly.Definition = goldOnly.Definition with { Inputs = [new(0, 200)] };
        That(goldOnly.Validate() is null, "纯金币配方不把编号0送入物品材料检查");
        var making = New(); making.Definition = making.Definition with { MakingType = 2, MakingLevel = 255 };
        That(making.Validate() is null, "制作推荐等级不新增为角色制作准入门槛");
        var noMaking = New(); noMaking.UseMaking = false;
        noMaking.Definition = noMaking.Definition with { MakingType = 2, MakingLevel = 1 };
        Reject(noMaking, "recipe_making_unavailable", "需要熟练度目录的配方不能无目录放行");
        var noRecipe = New(); noRecipe.UseRecipes = false;
        Reject(noRecipe, "recipe_definition", "没有typed配方不能仅按标签放行");
        var wrongId = New(); wrongId.Definition = wrongId.Definition with { BlueprintItemId = Source + 100 };
        Reject(wrongId, "recipe_definition", "异源typed定义不借当前奖励模板放行");
        var wrongKind = New(); wrongKind.Templates[Source] = wrongKind.Templates[Source] with { Kind = ItemKind.Material };
        Reject(wrongKind, "recipe_definition", "实体设计图必须沿真实Consumable源");
        foreach (var change in new Func<ItemTemplate, ItemTemplate>[]
        {
            item => item with { UseForbidden = true }, item => item with { Jobs = 0 },
            item => item with { MinimumLevel = 100, MaximumLevel = 1 }
        })
        {
            var bad = New(); bad.Templates[Source] = change(bad.Templates[Source]);
            Reject(bad, "recipe_definition", "源的实际使用合同保留");
        }
        var fullSuccess = New(); fullSuccess.Definition = fullSuccess.Definition with { Outputs = [new(Equipment, 1), new(Unknown, 1)] };
        Reject(fullSuccess, Unknown.ToString(), "成功后项未知消费者不能被首件正常装备掩盖");
        foreach (int rate in new[] { 0, 1, 9999, 10000 })
        {
            var failure = New(); failure.Definition = failure.Definition with { SuccessRate = rate, FailureOutputs = [new(Unknown, 1)] };
            Reject(failure, "recipe-failure", "全部声明失败产物沿正式制作预检，不能裁掉未知后继");
            var emptyFailure = New(); emptyFailure.Definition = emptyFailure.Definition with { SuccessRate = rate };
            That(emptyFailure.Validate() is null, "无失败产物时不虚构返还");
        }
        var multiple = New(); multiple.Definition = multiple.Definition with { Outputs = [new(Equipment, 1), new(Equipment2, 1)] };
        Reject(multiple, "multiple-equipment-output", "与正式Craft相同拒绝同支多装备");
        var countTwo = New(); countTwo.Definition = countTwo.Definition with { Outputs = [new(Equipment, 2)] };
        Reject(countTwo, "multiple-equipment-output", "与正式Craft相同拒绝装备count2");
        var forbiddenOutput = New(); forbiddenOutput.Templates[Equipment] = forbiddenOutput.Templates[Equipment] with { Kind = ItemKind.Creature };
        Reject(forbiddenOutput, "unsupported-output", "一般奖励支持的其它owner不能绕过配方产物限制");
        var invalidCost = New(); invalidCost.Definition = invalidCost.Definition with { Inputs = [new(Material, 0)] };
        Reject(invalidCost, "recipe_input_definition", "typed成本非正不能声明材料消费者");
        var materialOwner = New(); materialOwner.IncludeMaterialRoot = true;
        That(materialOwner.Validate() is null, "同根材料叶由该真实配方输入连接");
        var missingOwner = New(); missingOwner.IncludeMaterialRoot = true;
        missingOwner.Definition = missingOwner.Definition with { Inputs = [new(0, 200)] };
        Reject(missingOwner, "material_consumer_not_connected", "未被当前图配方消费的材料仍拒绝");
        var paidCycle = New(); paidCycle.Definition = paidCycle.Definition with { Outputs = [new(Source, 1)] };
        That(paidCycle.Validate() is null, "真实扣图扣料的制作边不误登记为免费循环");
        var skills = New(); skills.Definition = skills.Definition with { Skills = [new(900, 1)] };
        Reject(skills, "recipe_skill_qualification_unverified", "未静态证明的need-skill保留真实失败，不放行或预授技能");
        var preserved = New(); preserved.Definition = preserved.Definition with { MaintainAbility = "normal" };
        Reject(preserved, "recipe_preservation_unverified", "属性保留方案未证明前不套普通新实例产物");
        var matrix = New(); matrix.Definition = matrix.Definition with { UpgradeTransitions = [[1000000]] };
        Reject(matrix, "recipe_preservation_unverified", "活动矩阵不省略或造表放行");
    }

    static void Reject(Fixture fixture, string reason, string message)
    {
        string? error = fixture.Validate();
        That(error is not null && error.Contains(reason, StringComparison.Ordinal), message + ": " + error);
    }

    sealed class Fixture
    {
        readonly ItemResources resources;
        readonly ItemCatalog items;
        readonly BoosterCatalog boosters;
        readonly AvatarPackageCatalog avatars;
        readonly CashShopCatalog shop;
        public Dictionary<int, ItemTemplate> Templates { get; }
        public ItemRecipeDefinition Definition { get; set; } = new(Source, false,
            [new(Material, 2), new(0, 100)], [new(Equipment, 1)]);
        public bool UseRecipes { get; set; } = true;
        public bool UseMaking { get; set; } = true;
        public bool IncludeMaterialRoot { get; set; }

        public Fixture(ItemResources resources)
        {
            this.resources = resources;
            var archive = (PvfArchive)typeof(ItemCatalog).GetField("pvf", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(resources.Items)!;
            items = new(archive, new Dictionary<int, string>(), new Dictionary<int, string>(), 100);
            ItemTemplate Template(int id, ItemKind kind, byte slot = 0) => new(id, kind, slot, 1, 0,
                kind == ItemKind.Equipment ? 1 : 9999, 0, 0, 0, "[free]", ushort.MaxValue, 0, "隔离配方模板");
            Templates = new()
            {
                [Source] = Template(Source, ItemKind.Consumable) with { StackableType = "[recipe] 1" },
                [Equipment] = Template(Equipment, ItemKind.Equipment, 12),
                [Equipment2] = Template(Equipment2, ItemKind.Equipment, 12),
                [Material] = Template(Material, ItemKind.Material),
                [Unknown] = Template(Unknown, ItemKind.Consumable) with { StackableType = "[etc] 0" }
            };
            boosters = new(archive, new Dictionary<int, string>());
            avatars = new(_ => throw new InvalidDataException("隔离配方不应读取装扮资源"), new Dictionary<int, string>(), new Dictionary<int, string>());
            shop = CashShopCatalog.Parse($"[item]\n1 {Equipment} 1 0 0 100 0 0 0\n");
        }

        public string? Validate()
        {
            items.PrefillTemplates(Templates);
            var rules = new PromotionRules(new("recipe-fixture", "period", "v1", "default", "package"),
                10, 3, 31, Equipment, 1, 5000, [new(1, Equipment, 1)],
                IncludeMaterialRoot ? [new(2, Source, 1), new(3, Material, 1)] : [new(2, Source, 1)]);
            object?[] required = [items, boosters, avatars, shop, resources.EpicPieces, resources.Contracts,
                resources.RandomOptions, resources.AttributePairs, resources.Creatures, resources.CollectBoxes,
                resources.LimitCubes, resources.Enchantments, resources.Enhancements, resources.RecoveryConsumables,
                resources.Amplifications, resources.AmplifyUpgrades];
            var constructor = typeof(PromotionConsumerValidation).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
            // String-based optional binding lets this same test compile on the RED base.
            var validator = (PromotionConsumerValidation)constructor.Invoke(constructor.GetParameters().Select((parameter, index) =>
                index < required.Length ? required[index] : parameter.Name switch
                {
                    "recipes" => UseRecipes ? new ItemRecipeCatalog([Definition]) : null,
                    "makingSkills" => UseMaking ? resources.ItemMakingSkills : null,
                    _ => parameter.DefaultValue
                }).ToArray());
            return validator.Validate(rules);
        }
    }
}
