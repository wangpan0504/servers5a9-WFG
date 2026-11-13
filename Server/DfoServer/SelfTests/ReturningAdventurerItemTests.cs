using DfoServer.GameWorld.Events;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ReturningAdventurerItemTests
{
    public static void RunResources(PvfArchive pvf, ItemResources resources, TownCatalog towns)
    {
        int checks = Count;
        BoosterSelectionResourceTests.Run(pvf);
        var stackable = PvfScript.List(pvf.ReadText("stackable/stackable.lst"));
        var roots = new HashSet<int>();
        foreach (string path in new[] { AdventurerBenefitCatalog.ReturningPath, AdventurerBenefitCatalog.ReturningEventPath })
        {
            var document = new PvfTokenDocument(pvf.ReadTokens(path), pvf.ResolveString);
            That(document.Diagnostics.IsEmpty, "冒险家奖励活动声明完整 " + path);
            Visit(document.Sections);
        }
        var pending = new Queue<int>(roots.Order());
        var visited = new HashSet<int>();
        int boxes = 0, capsules = 0, rewards = 0, openings = 0;
        var now = DateTimeOffset.UtcNow;
        var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces,
            new Random(20261112), debugSettings: new() { IgnoreCashShopAbsoluteExpiration = true },
            contracts: resources.Contracts, avatars: resources.AvatarPackages, randomOptions: resources.RandomOptions);
        while (pending.TryDequeue(out int id))
        {
            if (!visited.Add(id)) continue;
            var template = resources.Items.Get(id);
            if (!stackable.TryGetValue(id, out string? path)) continue;
            string text = pvf.ReadText("stackable/" + path);
            string type = ItemCatalog.ReadStackableType(text);
            if (type.Contains("booster", StringComparison.Ordinal) || type.Contains("package", StringComparison.Ordinal) ||
                type.Contains("legacy", StringComparison.Ordinal) || type.Contains("tarot", StringComparison.Ordinal))
            {
                boxes++;
                var definition = resources.Boosters.Get(id);
                That(definition.Available, $"冒险家奖励盒可解析 {id} {template.Name}: {definition.PendingRule}");
                var children = definition.Pools.SelectMany(pool => pool.Rewards).Select(row => (row.ItemId, row.Count))
                    .Concat(definition.PackageRewards.IsDefault ? [] : definition.PackageRewards.Select(row => (row.ItemId, row.Count)))
                    .Concat(definition.PackageSelections.IsDefault ? [] : definition.PackageSelections.SelectMany(group => group.Rewards)
                        .Select(row => (row.ItemId, row.Count))).ToList();
                if (definition.Selection is { } selection)
                    foreach (var category in selection.Categories)
                    {
                        That(category.PendingRule is null, $"冒险家自选分类完整 {id}/{category.Category}/{category.Subcategory}: {category.PendingRule}");
                        children.AddRange(category.Rewards.Select(row => (row.ItemId, row.Count)));
                    }
                foreach (var child in children.Distinct())
                {
                    That(child.Count > 0 && (child.ItemId == 0 || resources.Items.TryGet(child.ItemId, out _)),
                        $"冒险家奖励产物模板和数量有效 {id} -> {child.ItemId} x{child.Count}");
                    if (child.ItemId > 0) pending.Enqueue(child.ItemId);
                    rewards++;
                }
                Console.WriteLine($"INFO adventurer-box id={id} root={roots.Contains(id)} rewards={children.Count} name={template.Name}");
                CheckOpening(template, definition);
            }
            else if (text.Contains("[expPercentUp]", StringComparison.Ordinal) || text.Contains("[expUp]", StringComparison.Ordinal))
            {
                capsules++;
                That(resources.Items.ExperienceConsumable(id) is not null, $"冒险家奖励经验胶囊可解析 {id} {template.Name}");
            }
        }
        That(roots.Contains(490013849) && roots.Contains(490013860) && roots.Contains(490013866) &&
            visited.Contains(490014080) && visited.Contains(490014273) && boxes >= 50 && capsules > 0,
            "检查包含新手/回归两组欢迎礼、两组任务、满级礼和递归子盒及胶囊，不只按盒子名称检索");
        Console.WriteLine($"PASS {Count - checks} 冒险家奖励递归检查: roots={roots.Count}, items={visited.Count}, boxes={boxes}, capsules={capsules}, reward-rows={rewards}, openings={openings}");

        void CheckOpening(ItemTemplate template, BoosterDefinition definition)
        {
            var source = new InventoryItem(1, new(InventoryListType.Main, 65),
                InventoryRewardGrantService.Create(template, utcNow: now, shopExpiration: true));
            var role = new Character(1, 0, "AdventurerBox", 0, towns.Room, towns.InitialReturn)
            {
                Level = checked((byte)Math.Min(95, template.MaximumLevel)),
                GrowType = definition.FirstGrowTypes.IsEmpty ? (byte)1 : definition.FirstGrowTypes[0],
                Inventory = new([source])
            };
            if (definition.Selection is { } selection)
            {
                foreach (var category in selection.Categories.Where(category => !category.Rewards.IsEmpty))
                {
                    var ids = category.Rewards.Select(row => row.ItemId).ToArray();
                    foreach (var selected in selection.SelectionCount == 0 ? new[] { ids } : ids.Select(id => new[] { id }))
                        Check(service.Open(role, new(65, 1, new(category.Category, category.Subcategory, selected)), out _, now));
                }
            }
            else if (!definition.PackageRewards.IsDefaultOrEmpty)
            {
                var choices = definition.PackageRewards.Where(row => resources.Items.Get(row.ItemId).Kind == ItemKind.Avatar)
                    .Select(row => new CeraPackageChoice(row.ItemId, resources.AvatarPackages.GetAvatar(row.ItemId).Abilities.Min())).ToList();
                if (!definition.PackageSelections.IsDefault)
                    choices.AddRange(definition.PackageSelections.Select(group => new CeraPackageChoice(group.Rewards[0].ItemId, 0)));
                Check(service.OpenCeraPackage(role, new(65, choices.ToArray()), out _, now));
            }
            else if (definition.Lottery)
                Check(service.OpenLottery(role, new(0, 65), out _, now));
            else
            {
                var characterPools = definition.Pools.Where(pool => pool.JobTag is not null).ToArray();
                if (characterPools.Length == 0) Check(service.Open(role, new(65, 1), out _, now));
                foreach (var pool in characterPools)
                {
                    byte job = checked((byte)Enumerable.Range(0, 16).Single(job => ItemCatalog.JobTag((byte)job) == pool.JobTag));
                    var eligible = role with { Job = job, GrowType = pool.GrowType < 0 ? (byte)1 : (byte)pool.GrowType,
                        Level = checked((byte)Math.Min(role.Level, pool.MaximumLevel)) };
                    Check(service.Open(eligible, new(65, 1), out _, now));
                }
            }

            void Check(InventoryResult result)
            {
                openings++;
                That(result.Accepted && result.Plan is not null && result.Plan.After.Items.All(item => item.Uid != source.Uid) &&
                    role.Inventory.Find(source.Position) == source,
                    $"冒险家礼盒真实发放规划成功且原输入不变 {template.Id} {template.Name}: error={result.Error}, reason={result.RejectReason}");
            }
        }

        void Visit(IEnumerable<PvfTokenSection> sections)
        {
            foreach (var section in sections)
            {
                if (section.Name is "reward" or "welcome reward" or "return daily reward" or
                    "return proper dungeon reward" or "existing proper dungeon reward" or "newbie levelup reward")
                    foreach (var token in section.Name == "welcome reward" ? section.Values.Where((_, index) => index % 2 == 0) :
                        section.Values.Skip(section.Name == "newbie levelup reward" ? 1 : 0).Take(1))
                        if (token.Type == 0 && stackable.ContainsKey(token.Value)) roots.Add(token.Value);
                Visit(section.Children);
            }
        }
    }
}
