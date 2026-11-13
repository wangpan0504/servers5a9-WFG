using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ExpertJobExtractionTests
{
    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var now = DateTimeOffset.UtcNow;
        foreach (var (job, tool) in new (byte Job, int Tool)[] { (1, 2600482), (2, 2600463), (4, 2600474) })
        {
            var character = new Character(1, 0, "Extract", 0, towns.Room, towns.InitialReturn)
            { Level = 50, ExpertJobType = job, ExpertJobExperience = 0 };
            var tools = InventoryRewardGrantService.TryGrantBatch(character, [new(tool, 1), new(10400, 1)],
                resources.Items, resources.GoldLimits, resources.EpicPieces, now);
            That(tools.Accepted && tools.Plan is not null, "三类真实萃取器与白装可准备");
            character = character with { Inventory = tools.Plan!.After with
            { Items = tools.Plan.After.Items.Select((item, i) => item with { Uid = i + 1 }).ToImmutableArray() } };
            var extractor = character.Inventory.Items.Single(item => item.Core.ItemId == tool);
            var target = character.Inventory.Items.Single(item => item.Core.ItemId == 10400);
            bool Plan(Character source, out ExpertJobCraftPlan plan) => ExpertJobCraft.TryPlanExtract(source,
                resources.ExpertJobs, resources.Items, resources.GoldLimits, resources.EpicPieces,
                extractor.Position.Index, (byte)target.Position.List, target.Position.Index, _ => 0, now, out plan, out _);
            That(Plan(character, out var valid) && valid.Inventory.After.Find(target.Position) is null &&
                valid.Inventory.After.Find(extractor.Position) == extractor && valid.Products.Length > 0,
                "萃取消费真实装备、发材料并保留无限次工具");
            That(Plan(character with { ExpertJobExperience = int.MaxValue }, out var capped) &&
                capped.Experience == resources.ExpertJobs.ClampExperience(job, int.MaxValue) &&
                capped.Inventory.After.Find(target.Position) is null && capped.Products.Length > 0,
                "三职业满级溢出仍完成萃取与发材料，累计经验封顶");
            var expiring = character with { Inventory = character.Inventory with
            { Items = character.Inventory.Items.Replace(extractor, extractor with { Core = extractor.Core with { ExpireTime = checked((int)now.ToUnixTimeSeconds() + 10) } }) } };
            That(Plan(expiring, out var expiringPlan) && expiringPlan.Inventory.CharacterPrecondition is { } precondition &&
                !precondition.Matches(expiring, now.ToUnixTimeSeconds() + 11),
                "萃取器在规划后到期必须阻止提交，不凭旧快照继续发材料");
            var misplaced = character with { Inventory = character.Inventory with
            { Items = character.Inventory.Items.Replace(extractor, extractor with { Position = new(InventoryListType.Main, 2000) }) } };
            That(!ExpertJobCraft.TryPlanExtract(misplaced, resources.ExpertJobs, resources.Items, resources.GoldLimits,
                resources.EpicPieces, 2000, (byte)target.Position.List, target.Position.Index, _ => 0, now, out _, out _),
                "伪造未开放工具槽不能萃取");
            int advancedId = job switch { 1 => 2600483, 2 => 2600464, _ => 2600475 };
            var advanced = character with { Inventory = character.Inventory with
            { Items = character.Inventory.Items.Replace(extractor, extractor with
                { Core = InventoryRewardGrantService.Create(resources.Items.Get(advancedId)) }) } };
            That(!Plan(advanced, out _), "Lv1不能越级使用要求Lv3的高级提取器");
            That(Plan(advanced with { ExpertJobExperience = resources.ExpertJobs.ExperienceForLevel(job, 3) }, out _),
                "达到资源要求的Lv3后可使用原高级提取器");
            var rare = character with { Inventory = character.Inventory with
            { Items = character.Inventory.Items.Replace(target, target with { Core = InventoryRewardGrantService.Create(resources.Items.Get(20238)) }) } };
            That(Plan(rare, out var bigWin) && bigWin.Products.Length == 2 &&
                bigWin.Products.Any(product => product.ItemId is >= 2610031 and <= 2610047),
                "真实稀有装备的大成功表必须发出精髓或副职业材料，不能只给基础产物");
            That(ExpertJobCraft.TryPlanExtract(rare, resources.ExpertJobs, resources.Items, resources.GoldLimits,
                resources.EpicPieces, extractor.Position.Index, 0, target.Position.Index, limit => limit - 1, now, out var ordinary, out _) &&
                ordinary.Products.Length == 2 && !ordinary.Products.SequenceEqual(bigWin.Products),
                "普通附加与大成功使用各自完整产物池，保留绝对权重和等级区间");
        }
        Console.WriteLine($"PASS {Count - checks} 副职业萃取资格与到期断言");
    }
}
