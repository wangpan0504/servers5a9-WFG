using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class EnchantmentTests
{
    // 真实模板与目标部位来自匹配资源；不由被测资格方法反推期待。
    static void Spring2020RealBeadQualificationResources(ItemResources resources)
    {
        var legacy = resources.Items.Get(400150031);
        var sacred = resources.Items.Get(100150548);
        That(legacy is { Kind: ItemKind.Equipment, Rarity: 2, EquipmentSlot: 15, IsLegacyEquipment: true } &&
            sacred is { Kind: ItemKind.Equipment, Rarity: 3, EquipmentSlot: 15, IsLegacyEquipment: true },
            "真实item category legacy装配标记；稀有传承与神器圣物保留各自Rarity");
        Spring2020RealBeadQualificationMatrix(resources, resources.Enchantments,
        [
            (490705300, 490705381, 13, [400330267], [100150548]),
            (490705301, 490705382, 15, [100150548, 400150031], [100200523]),
            (490705302, 490705383, 18, [100200523], [440003]),
            (490705303, 490705454, 22, [440003], [400350053]),
            (490705304, 490705451, 23, [400350053], [100390009]),
            (490705305, 490705452, 25, [100390009], [400330267])
        ], 2_000_000_000);
    }

    static void Spring2020RealBeadQualificationMatrix(ItemResources resources, EnchantmentCatalog enchantments,
        IReadOnlyList<(int BeadId, int CardId, byte Slot, int[] AllowedTargetIds, int[] DeniedTargetIds)> matrix,
        long now)
    {
        (int BeadId, int CardId, byte Slot)[] expected =
        [
            (490705300, 490705381, 13), (490705301, 490705382, 15), (490705302, 490705383, 18),
            (490705303, 490705454, 22), (490705304, 490705451, 23), (490705305, 490705452, 25)
        ];
        if (matrix.Count != expected.Length || !matrix.Select(row => (row.BeadId, row.CardId, row.Slot)).OrderBy(row => row.BeadId)
            .SequenceEqual(expected))
            throw new InvalidDataException("必须显式绑定原六珠完整资格矩阵，缺项不能空跑通过");
        foreach (var row in matrix)
        {
            if (row.AllowedTargetIds.Length == 0 || row.DeniedTargetIds.Length == 0 ||
                row.AllowedTargetIds.Concat(row.DeniedTargetIds).Any(id => id <= 0) ||
                row.AllowedTargetIds.Concat(row.DeniedTargetIds).Distinct().Count() !=
                    row.AllowedTargetIds.Length + row.DeniedTargetIds.Length)
                throw new InvalidDataException("每珠须绑定有依据的真实正反目标，禁止空集、重复或冲突期待");
            var bead = resources.Items.Get(row.BeadId);
            var definition = enchantments.Get(row.BeadId) ?? throw new InvalidDataException("原宝珠真实定义未闭合：" + row.BeadId);
            That(bead.Kind == ItemKind.Consumable && System.Text.RegularExpressions.Regex.IsMatch(bead.StackableType, @"^\[enchant waste\]\s+0$") &&
                definition.BeadItemId == row.BeadId && definition.CardItemId == row.CardId && definition.Upgrade == 0 &&
                definition.RandomTitle is null && definition.RandomCards is null &&
                definition.Targets.SetEquals([(ItemKind.Equipment, row.Slot)]),
                "六珠保留原Card/index/目标部位；不能拒绝整珠或用合成包装冒真实资源：" + row.BeadId);
            // 此回归固定合法源角色，未知额外资格必须暴露，不能在夹具中清除限制。
            That(bead.MinimumLevel <= 95 && bead.Jobs != 0 && bead.RequiredSkill == 0 && !bead.UseForbidden,
                "真实源角色资格与测试前提保持明确，新增前提需root另按资源绑定");
            byte job = checked((byte)Enumerable.Range(0, 16).First(value => (bead.Jobs & (1 << value)) != 0));
            var service = new InventoryEnchantmentService(resources.Items, enchantments,
                random: _ => throw new InvalidDataException("固定单卡宝珠不能进入抽样"));
            foreach (var (targetId, allowed) in row.AllowedTargetIds.Select(id => (id, true))
                .Concat(row.DeniedTargetIds.Select(id => (id, false))))
            {
                var gear = resources.Items.Get(targetId);
                That(gear.Kind == ItemKind.Equipment, "原资源目标使用真实装备模板，不能伪造类型");
                if (allowed) That(gear.EquipmentSlot == row.Slot, "合法目标部位必须与保存卡正文一致");
                That(enchantments.CanEnchantCard(row.CardId, 0, gear) == allowed,
                    "真实卡片完整模板资格跟随已证期待，禁止只校部位：" + row.CardId + ":" + targetId);
                foreach (var targetPosition in new[] { new InventorySlot(InventoryListType.Main, 9),
                    new InventorySlot(InventoryListType.Equipment, gear.EquipmentSlot) })
                {
                    var source = new InventoryItem(81, new(InventoryListType.Main, 82),
                        InventoryRewardGrantService.Create(bead, 2, DateTimeOffset.FromUnixTimeSeconds(now)) with { SortLockFlag = 1 });
                    var target = new InventoryItem(82, targetPosition,
                        InventoryRewardGrantService.Create(gear, utcNow: DateTimeOffset.FromUnixTimeSeconds(now)) with
                        { Attr = 7, GenuineUpgrade = 4, AmplifyType = 2, AmplifyValue = 21, Rune = 8,
                            EnchantCardId = 3175, EnchantUpgrade = 2, Extension97 = 0x00012711,
                            SortLockFlag = 1, Random0 = new(1, 2, 3), Extension26 = 2, Extension31 = 31,
                            Extension35 = 35, Extension90 = 90, Extension92 = 92, Extension109 = 109,
                            Extension110 = 110, Extension114 = new(1, 114), Extension121 = 121 });
                    var unrelated = target with { Uid = 83, Position = new(InventoryListType.Main, 10) };
                    That(!source.IsExpired(now) && !target.IsExpired(now),
                        "真实Create期限须合法，不清零或忽略原模板期限来强行制造正向");
                    Character role = new(1, 0, "SpringBead", 0, new(0, 0, 0, 0), null)
                    { Level = 95, Job = job, ExpertJobType = 1, Inventory = new([source, target, unrelated], Gold: 3456)
                        { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 123) } };
                    string original = JsonSerializer.Serialize(role);
                    var request = new EnchantmentRequest(source.Position, target.Position, uint.MaxValue);
                    var result = service.Plan(role, request, now);
                    Check(result.Accepted, result.Plan, "272共享宝珠服务");
                    That(allowed || result.Error == InventoryEnchantmentService.Rejection, "272资格拒绝沿原公共错误分支");
                    // 真实601 Handler限定目标容器Main；Equipment仅验证272服务，不冒可达601 wire路径。
                    if (targetPosition.List == InventoryListType.Main)
                    {
                    bool accepted601 = ExpertJobCraft.TryPlanEnchant(role, service, resources.ExpertJobs,
                        (byte)target.Position.List, checked((ushort)target.Position.Index),
                        (byte)source.Position.List, checked((ushort)source.Position.Index), now,
                        out var planned601, out int experience, out ushort error601);
                    Check(accepted601, planned601, "601本人宝珠回退");
                    int expectedExperience = role.ExpertJobExperience + (allowed ? resources.ExpertJobs.MonsterCardExp(1,
                        resources.ExpertJobs.Level(1, role.ExpertJobExperience)).Min : 0);
                    That(experience == expectedExperience && (allowed || error601 == InventoryEnchantmentService.Rejection),
                        "601只在同资格计划成功后结算原熟练度，拒绝无经验及副作用");
                    }
                    That(JsonSerializer.Serialize(role) == original, "实际规划不修改原角色完整快照");

                    void Check(bool accepted, InventoryPlan? plan, string route)
                    {
                        if (!allowed)
                        {
                            That(!accepted && plan is null, route + "按独立资格拒绝，不生成扣源或改目标计划");
                            return;
                        }
                        var expectedAfter = role.Inventory with { Items = [source with { Core = source.Core with { Value = 1 } },
                            target with { Core = target.Core with { EnchantCardId = row.CardId, EnchantUpgrade = 0 } }, unrelated] };
                        That(accepted && plan is not null && ReferenceEquals(plan.Before, role.Inventory) &&
                            JsonSerializer.Serialize(plan.After) == JsonSerializer.Serialize(expectedAfter) &&
                            plan.CharacterPrecondition is not null && plan.CharacterPrecondition.Matches(role, now),
                            route + "只扣一珠并改Card/index，全部Core/UID/其它物品/钱包/原资格完整保持");
                    }
                }
            }
        }
    }
}
