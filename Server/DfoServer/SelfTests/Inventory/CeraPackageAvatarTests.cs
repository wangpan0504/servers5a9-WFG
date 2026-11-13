using System.Globalization;
using System.Text.Json;
using PvfLib;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class CeraPackageAvatarTests
{
    // 原始解析保留资源声明，正式目录仅对获准的神选内容做精确修正。
    public static void ShenxuanDefinitions(PvfArchive pvf, ItemResources resources)
    {
        foreach (int source in Enumerable.Range(490703475, 56))
        {
            var raw = BoosterCatalog.Parse(pvf.ReadText($"stackable/cash/chn_{source}.stk"));
            That(raw.Available && raw.PackageRewards.Length == 20 &&
                raw.PackageRewards[^1] == new BoosterReward(5050548, 1, 3),
                $"神选源{source}的原始解析仍完整保留20项声明");
            var actual = resources.Boosters.Get(source);
            That(actual.Available && actual.PackageRewards.Length == 19 &&
                actual.PackageRewards.SequenceEqual(raw.PackageRewards.Take(19)),
                $"神选源{source}只移除获准的末项，前19项顺序数量不变");
        }
        var original = BoosterCatalog.Parse(pvf.ReadText("stackable/cash/chn_490703475.stk"));
        foreach (var (id, path, definition) in new (int, string, BoosterDefinition)[]
        {
            (490703474, "cash/chn_490703474.stk", original),
            (490703531, "cash/chn_490703531.stk", original),
            (490703475, "cash/other.stk", original),
            (490703475, "cash/chn_490703475.stk", new([new(1, [new(1106, 1, 1)])])),
            (490703475, "cash/chn_490703475.stk", original with { PendingRule = "资格未实现" }),
            (490703475, "cash/chn_490703475.stk", original with { PackageRewards = original.PackageRewards.RemoveAt(19) }),
            (490703475, "cash/chn_490703475.stk", original with { PackageRewards = original.PackageRewards.Add(new(1106, 1, 1)) }),
            (490703475, "cash/chn_490703475.stk", original with { PackageRewards = original.PackageRewards.SetItem(19, new(1112, 1, 3)) }),
            (490703475, "cash/chn_490703475.stk", original with { PackageRewards = original.PackageRewards.SetItem(19, new(5050548, 1, 2)) }),
            (490703475, "cash/chn_490703475.stk", original with { PackageRewards = original.PackageRewards.SetItem(19, new(5050548, 2, 3)) }),
            (490703475, "cash/chn_490703475.stk", original with { PackageRewards = original.PackageRewards.SetItem(0, new(5050548, 1, 1)) })
        })
            That(ReferenceEquals(BoosterCatalog.CorrectShenxuanPackageContents(id, path, definition), definition),
                "神选内容修正不吞其它来源、结构变化、重复未知奖励或原拒绝规则");
    }

    const int PackageId = 2683533;
    static readonly InventorySlot SourceSlot = new(InventoryListType.Main, 65);

    public static void Run(TownCatalog towns, ItemResources resources)
    {
        var definition = resources.Boosters.Get(PackageId);
        var totals = definition.PackageRewards.GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Count));
        That(definition.Available && totals.Values.Sum() == 9 &&
            totals.Keys.All(id => resources.Items.Get(id).Kind == ItemKind.Avatar),
            "2683533是九件装扮的固定商城礼包");
        var choices = totals.Keys.Select(id =>
            new CeraPackageChoice(id, resources.AvatarPackages.GetAvatar(id).Abilities.Min())).ToArray();
        var sourceTemplate = resources.Items.Get(PackageId);
        byte job = EligibleJob(sourceTemplate);
        byte level = Math.Max((byte)1, sourceTemplate.MinimumLevel);
        var skills = sourceTemplate.RequiredSkill == 0 ? Array.Empty<CharacterSkill>() :
            [new CharacterSkill(sourceTemplate.RequiredSkill, 1)];
        var service = Service(resources, ignoreAbsoluteExpiration: true);

        Qualification(towns, resources, service, sourceTemplate, choices, job, level, skills);
        Placement(towns, resources, service, sourceTemplate, choices, totals, job, level, skills);
        DeadlineCommit(towns, resources, sourceTemplate, choices, job, level, skills);
        NonAvatarDeadline(towns, resources);
    }

    static void Qualification(TownCatalog towns, ItemResources resources, InventoryBoosterService service,
        ItemTemplate sourceTemplate, CeraPackageChoice[] choices, byte job, byte level, CharacterSkill[] skills)
    {
        var role = Character(towns, resources, sourceTemplate, job, level, skills, ItemSlotBoundService.AvatarCapacity);
        ushort missingSkill = sourceTemplate.RequiredSkill == ushort.MaxValue ? (ushort)(ushort.MaxValue - 1) : ushort.MaxValue;
        ItemTemplate[] denied =
        [
            sourceTemplate with { UseForbidden = true },
            sourceTemplate with { MinimumLevel = checked((byte)(level + 1)) },
            sourceTemplate with { RequiredSkill = missingSkill }
        ];
        try
        {
            foreach (var template in denied)
            {
                resources.Items.PrefillTemplates([KeyValuePair.Create(PackageId, template)]);
                var result = service.OpenCeraPackage(role, new(65, choices), out var receipt);
                That(!result.Accepted && result.Plan is null && receipt is null &&
                    role.Inventory.Find(SourceSlot)?.Core.ItemId == PackageId,
                    "518非空选择仍校验源礼包的禁用、等级和技能资格");
            }
            resources.Items.PrefillTemplates([KeyValuePair.Create(PackageId,
                sourceTemplate with { Jobs = (ushort)(sourceTemplate.Jobs & ~(1 << job)) })]);
            That(service.OpenCeraPackage(role, new(65, choices), out _).Accepted,
                "518源STK的职业匹配交给客户端，不影响合法装扮选择");
        }
        finally
        {
            resources.Items.PrefillTemplates([KeyValuePair.Create(PackageId, sourceTemplate)]);
        }
        That(service.OpenCeraPackage(role, new(65, choices), out _).Accepted,
            "恢复真实模板后2683533可按原资格打开");
    }

    static void Placement(TownCatalog towns, ItemResources resources, InventoryBoosterService service,
        ItemTemplate sourceTemplate, CeraPackageChoice[] choices, IReadOnlyDictionary<int, int> totals,
        byte job, byte level, CharacterSkill[] skills)
    {
        foreach (int free in new[] { 3, 0 })
        {
            var role = Character(towns, resources, sourceTemplate, job, level, skills, free);
            var occupied = role.Inventory.Items.Select(item => item.Position).ToHashSet();
            var result = service.OpenCeraPackage(role, new(65, choices), out var receipt);
            var added = result.Plan?.After.Items.Where(item =>
                item.Position.List == InventoryListType.Avatar && !occupied.Contains(item.Position)).ToArray() ?? [];
            var overflow = result.Plan?.OverflowItems.ToArray() ?? [];
            var actual = added.Concat(overflow).ToArray();
            That(result.Accepted && receipt!.Rewards.Sum(row => row.Count) == 9 &&
                result.Plan!.After.Find(SourceSlot) is null && added.Length == free && overflow.Length == 9 - free &&
                actual.Length == 9 && totals.All(pair => actual.Where(item => item.Core.ItemId == pair.Key).Sum(item => item.Core.Count) == pair.Value),
                free == 0
                    ? "2683533满装扮栏只生成九件邮件附件，不重复拼接同一溢出集合"
                    : "2683533部分空位按三件入栏、六件邮件完整分配");
            That(actual.All(item => item.Uid == 0 && item.Avatar is not null &&
                item.Core.Durability == choices.Single(choice => choice.ItemId == item.Core.ItemId).Option),
                "2683533入栏和邮件装扮均保留客户端选择属性");
        }
    }

    static void DeadlineCommit(TownCatalog towns, ItemResources resources, ItemTemplate sourceTemplate,
        CeraPackageChoice[] choices, byte job, byte level, CharacterSkill[] skills)
    {
        int[] rewardIds = resources.Boosters.Get(PackageId).PackageRewards.Select(row => row.ItemId).Distinct().ToArray();
        var originals = rewardIds.ToDictionary(id => id, resources.Items.Get);
        long deadline = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string root = Path.Combine(Path.GetTempPath(), "cera-package-avatar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            resources.Items.PrefillTemplates(originals.Select(pair => KeyValuePair.Create(pair.Key,
                pair.Value with { Expiration = new ItemExpiration(pair.Key == rewardIds[0] ? deadline : 0) })));
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("cera-package-avatar-test", () => { });
            var seed = Character(towns, resources, sourceTemplate, job, level, skills, 0);
            var inventory = seed.Inventory with
            {
                Items = [.. seed.Inventory.Items.Select(item => item with
                {
                    Uid = 0,
                    Core = item.Core.Kind == ItemKind.Avatar ? item.Core with { Value = 0 } : item.Core
                })]
            };
            var current = repository.Create(owner, new("CeraDeadline", towns.Room, towns.InitialReturn)
            {
                Job = job,
                Level = level,
                Skills = skills,
                Inventory = inventory
            });
            var service = Service(resources, ignoreAbsoluteExpiration: false);
            var sourceRole = Character(towns, resources, sourceTemplate, job, level, skills, ItemSlotBoundService.AvatarCapacity);
            var source = sourceRole.Inventory.Find(SourceSlot)!;
            long sourceDeadline = deadline - 1;
            sourceRole = sourceRole with
            {
                Inventory = sourceRole.Inventory with
                {
                    Items = sourceRole.Inventory.Items.Replace(source,
                        source with { Core = source.Core with { ExpireTime = checked((int)sourceDeadline) } })
                }
            };
            var sourceResult = service.OpenCeraPackage(sourceRole, new(65, choices), out _,
                DateTimeOffset.FromUnixTimeSeconds(sourceDeadline - 1));
            That(sourceResult.Accepted &&
                sourceResult.Plan!.CharacterPrecondition!.Matches(sourceRole, sourceDeadline - 1) &&
                !sourceResult.Plan.CharacterPrecondition.Matches(sourceRole, sourceDeadline),
                "2683533源实例的截止时间也绑定到最终库存计划");
            var result = service.OpenCeraPackage(current, new(65, choices), out _,
                DateTimeOffset.FromUnixTimeSeconds(deadline - 1));
            That(result.Accepted && result.Plan!.OverflowItems.Length == 9 &&
                result.Plan.CharacterPrecondition!.Matches(current, deadline - 1) &&
                !result.Plan.CharacterPrecondition.Matches(current, deadline),
                "2683533邮件奖励的最早期限绑定到最终库存计划");
            var plan = result.Plan!;
            string before = Snapshot(repository, owner, path);
            Reject<InvalidOperationException>(() => repository.CommitInventory(owner, current, plan),
                "到期后的固定礼包规划不能提交");
            That(Snapshot(repository, owner, path) == before &&
                repository.LoadCharacter(owner, current.Slot).Inventory.Find(SourceSlot)?.Core.ItemId == PackageId,
                "到期提交拒绝后源、邮件、UID和审计均保持不变");
        }
        finally
        {
            resources.Items.PrefillTemplates(originals.Select(pair => KeyValuePair.Create(pair.Key, pair.Value)));
            SqliteConnection.ClearAllPools();
            TestFiles.DeleteDirectory(root, recursive: true);
        }
    }

    static void NonAvatarDeadline(TownCatalog towns, ItemResources resources)
    {
        const int mixedPackageId = 490704193;
        var definition = resources.Boosters.Get(mixedPackageId);
        int[] rewardIds = definition.PackageRewards.Select(row => row.ItemId).Distinct().ToArray();
        var originals = rewardIds.ToDictionary(id => id, resources.Items.Get);
        int physicalId = rewardIds.First(id =>
            CurrencyWallet.RewardKind(id) is null && !resources.EpicPieces.ByPiece.ContainsKey(id) &&
            resources.Contracts.TryAcquisitionContract(id, out _) == false &&
            resources.Items.Get(id).AcquisitionContract is null &&
            resources.Items.Get(id).Kind != ItemKind.Avatar);
        long deadline = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            resources.Items.PrefillTemplates(originals.Select(pair => KeyValuePair.Create(pair.Key,
                pair.Value with { Expiration = new ItemExpiration(pair.Key == physicalId ? deadline : 0) })));
            var sourceTemplate = resources.Items.Get(mixedPackageId);
            byte job = EligibleJob(sourceTemplate);
            byte level = Math.Max((byte)1, sourceTemplate.MinimumLevel);
            CharacterSkill[] skills = sourceTemplate.RequiredSkill == 0 ? [] : [new(sourceTemplate.RequiredSkill, 1)];
            var choices = rewardIds.Where(id => resources.Items.Get(id).Kind == ItemKind.Avatar)
                .Select(id => new CeraPackageChoice(id, resources.AvatarPackages.GetAvatar(id).Abilities.Min())).ToArray();
            Character role = new(1, 0, "CeraMixed", 0, towns.Room, towns.InitialReturn)
            {
                Job = job,
                Level = level,
                Skills = skills,
                Inventory = new([new(10_000, SourceSlot,
                    InventoryRewardGrantService.Create(sourceTemplate, shopExpiration: true))])
            };
            var result = Service(resources, ignoreAbsoluteExpiration: false).OpenCeraPackage(
                role, new(65, choices), out _, DateTimeOffset.FromUnixTimeSeconds(deadline - 1));
            That(result.Accepted && result.Plan!.CharacterPrecondition!.Matches(role, deadline - 1) &&
                !result.Plan.CharacterPrecondition.Matches(role, deadline),
                "含时装固定包的非时装实体奖励期限也参与最终计划截止");
        }
        finally
        {
            resources.Items.PrefillTemplates(originals.Select(pair => KeyValuePair.Create(pair.Key, pair.Value)));
        }
    }

    static Character Character(TownCatalog towns, ItemResources resources, ItemTemplate sourceTemplate,
        byte job, byte level, CharacterSkill[] skills, int freeAvatarSlots)
    {
        var now = DateTimeOffset.UtcNow;
        var filler = resources.Items.Get(resources.Boosters.Get(PackageId).PackageRewards[0].ItemId);
        var items = new List<InventoryItem>
        {
            new(10_000, SourceSlot, InventoryRewardGrantService.Create(sourceTemplate, utcNow: now, shopExpiration: true))
        };
        int occupied = ItemSlotBoundService.AvatarCapacity - freeAvatarSlots;
        for (int slot = 0; slot < occupied; slot++)
        {
            long uid = slot + 1;
            var core = InventoryRewardGrantService.Create(filler, utcNow: now, shopExpiration: true) with { Value = checked((int)uid) };
            items.Add(InventoryRewardGrantService.CreateItem(filler,
                new(InventoryListType.Avatar, checked((short)slot)), core) with { Uid = uid });
        }
        return new(1, 0, "CeraAvatar", 0, towns.Room, towns.InitialReturn)
        {
            Job = job,
            Level = level,
            Skills = skills,
            Inventory = new([.. items])
        };
    }

    static InventoryBoosterService Service(ItemResources resources, bool ignoreAbsoluteExpiration) =>
        new(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces,
            debugSettings: new GameDebugSettings { IgnoreCashShopAbsoluteExpiration = ignoreAbsoluteExpiration },
            contracts: resources.Contracts, avatars: resources.AvatarPackages);

    static byte EligibleJob(ItemTemplate template)
    {
        for (byte job = 0; job < 16; job++)
            if ((template.Jobs & (1 << job)) != 0)
                return job;
        throw new InvalidDataException("2683533没有可用职业");
    }

    static string Snapshot(CharacterRepository repository, AccountOwners.Lease owner, string path) =>
        JsonSerializer.Serialize(repository.List(owner)) + ":" +
        Scalar(path, "SELECT count(*) FROM inventory_audit_log") + ":" +
        Scalar(path, "SELECT count(*) FROM mailbox_messages") + ":" +
        Scalar(path, "SELECT count(*) FROM mailbox_attachments") + ":" +
        Scalar(path, "SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");

    static string Scalar(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "<null>";
    }
}
