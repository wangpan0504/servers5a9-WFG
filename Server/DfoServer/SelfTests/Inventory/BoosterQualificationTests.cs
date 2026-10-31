using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class BoosterQualificationTests
{
    public static void Run(TownCatalog towns, ItemResources resources)
    {
        var now = DateTimeOffset.Parse("2026-10-18T21:59:59Z");
        var clock = new DailyBoosterTestClock(now);
        string root = Path.Combine(Path.GetTempPath(), "booster-qualification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            using var owner = new AccountOwners().Acquire("qualification", () => { });
            repository.Create(owner, new("Qualify", towns.Room, towns.InitialReturn));
            var session = new GameSession(repository, towns, owner);
            session.Select(0); session.MarkReady();
            var before = session.Selected!;
            string snapshot = JsonSerializer.Serialize(repository.List(owner));
            bool normalRejection = false;
            try
            {
                var result = session.PlanAndCommitInventory(character =>
                {
                    var plan = new InventoryPlan(character.Inventory, character.Inventory with { Gold = 1 }, "qualification_test",
                        new(character, now.AddSeconds(1).ToUnixTimeSeconds(), clock));
                    clock.Advance(TimeSpan.FromSeconds(1));
                    return new(true, 0, plan);
                }, 19);
                normalRejection = !result.Accepted && result.Error == 19 && result.Receipt is null;
            }
            catch (InvalidOperationException) { }

            var template = resources.Items.Get(490704500);
            var source = new InventoryItem(1, new(InventoryListType.Main, 65),
                InventoryRewardGrantService.Create(template, utcNow: now, shopExpiration: true));
            var role = before with { Level = 18, Inventory = new([source]) };
            var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits,
                resources.EpicPieces, debugSettings: new GameDebugSettings { IgnoreCashShopAbsoluteExpiration = true },
                contracts: resources.Contracts);
            bool maxLevelRejected = !service.OpenCeraPackage(role, new(65, []), out _, now).Accepted;
            Console.WriteLine($"qualification regression: normal expiry rejected={normalRejection}, root maximum level rejected={maxLevelRejected}");
            That(normalRejection && maxLevelRejected, "正常提交跨截止走失败19且根礼包遵守最高17级");
            That(ReferenceEquals(session.Selected, before) && snapshot == JsonSerializer.Serialize(repository.List(owner)),
                "期限拒绝保留在线角色、资产、revision与账本");
            foreach (byte level in new byte[] { 1, 17 })
                That(service.OpenCeraPackage(role with { Level = level }, new(65, []), out _, now).Accepted,
                    "根礼包合法领取等级保留 " + level);
            foreach (var invalid in new[] { template with { Jobs = 2 }, template with { RequiredSkill = ushort.MaxValue },
                template with { MinimumLevel = 2 }, template with { UseForbidden = true }, template with { Id = template.Id + 1 },
                template with { Kind = ItemKind.Equipment } })
            {
                try
                {
                    resources.Items.PrefillTemplates([new(template.Id, invalid)]);
                    That(!service.OpenCeraPackage(role with { Level = 1, Job = 0 }, new(65, []), out _, now).Accepted,
                        "根礼包源职业、技能、最低等级、禁用及模板身份均复验");
                }
                finally { resources.Items.PrefillTemplates([new(template.Id, template)]); }
            }
            const int ordinaryId = 490704509;
            var ordinaryTemplate = resources.Items.Get(ordinaryId);
            var ordinary = new InventoryItem(1, source.Position, InventoryRewardGrantService.Create(ordinaryTemplate, utcNow: now) with
                { ExpireTime = (int)now.AddSeconds(2).ToUnixTimeSeconds() });
            var ordinaryRole = before with { Inventory = new([ordinary]) };
            var ordinaryPlan = service.Open(ordinaryRole, new(65, 1), out _, now).Plan!;
            That(ordinaryPlan.CharacterPrecondition!.Matches(ordinaryRole, now.AddSeconds(1).ToUnixTimeSeconds()) &&
                !ordinaryPlan.CharacterPrecondition.Matches(ordinaryRole, now.AddSeconds(2).ToUnixTimeSeconds()),
                "非每日普通开盒绑定源截止，等于截止即拒绝");
            foreach (var invalid in new[] { ordinaryTemplate with { MaximumLevel = 1 }, ordinaryTemplate with { Jobs = 2 },
                ordinaryTemplate with { RequiredSkill = ushort.MaxValue }, ordinaryTemplate with { UseForbidden = true } })
            {
                try
                {
                    resources.Items.PrefillTemplates([new(ordinaryId, invalid)]);
                    That(!service.Open(ordinaryRole with { Level = 2 }, new(65, 1), out _, now).Accepted,
                        "非daily普通入口同样遵守完整源资格");
                    That(!InventoryAvatarPackageService.SourceEligible(ordinaryRole with { Level = 2 }, ordinary, invalid),
                        "avatar及mixed518共享源资格包含最大等级、职业、技能与禁用");
                }
                finally { resources.Items.PrefillTemplates([new(ordinaryId, ordinaryTemplate)]); }
            }
            var rewardTemplate = resources.Items.Get(490704518);
            try
            {
                resources.Items.PrefillTemplates([new(rewardTemplate.Id, rewardTemplate with { Expiration = new(DailyDelete: 1) })]);
                var rewardPlan = service.Open(ordinaryRole, new(65, 1), out _, now).Plan!;
                That(!rewardPlan.CharacterPrecondition!.Matches(ordinaryRole, now.AddSeconds(1).ToUnixTimeSeconds()),
                    "非每日开盒取本次实际奖励更早截止");
                var range = ItemSlotBoundService.Range(ItemKind.Consumable, role.Inventory);
                var full = Enumerable.Range(range.Start, range.End - range.Start + 1).Select((slot, index) =>
                    source with { Uid = index + 1, Position = new(InventoryListType.Main, (short)slot) }).ToImmutableArray();
                var fullRole = role with { Level = 1, Inventory = new(full) };
                resources.Items.PrefillTemplates([new(ordinaryId, ordinaryTemplate with { Expiration = new(DailyDelete: 1) })]);
                var mailed = service.OpenCeraPackage(fullRole, new(65, []), out _, now);
                That(mailed.Accepted && mailed.Plan!.OverflowItems.Any(item => item.Core.ItemId == ordinaryId) &&
                    !mailed.Plan.CharacterPrecondition!.Matches(fullRole, now.AddSeconds(1).ToUnixTimeSeconds()),
                    "固定包实际溢出邮件奖励也纳入最早截止");
            }
            finally { resources.Items.PrefillTemplates([new(rewardTemplate.Id, rewardTemplate), new(ordinaryId, ordinaryTemplate)]); }
            Places(resources, session);
        }
        finally { Directory.Delete(root, true); }
    }

    static void Places(ItemResources resources, GameSession session)
    {
        InventoryResult Noop(Character character) => new(true, 0, new(character.Inventory, character.Inventory, "booster_place_probe"));
        foreach (int id in new[] { 490704518, 490704519, 490704520, 490704521 })
        {
            That(resources.Boosters.UsePlaces(id) is { Village: true, SeriaRoom: true, PendingRule: null },
                "四盒真实资源地点以允许并集解释 " + id);
            That(session.PlanAndCommitBooster(Noop, resources.Boosters.UsePlaces(id)).Accepted,
                "真实Seria房间允许四盒领取地点 " + id);
        }
        That(session.LeaveRoom(), "地点回归通过真实房间出口进入Town");
        foreach (int id in new[] { 490704518, 490704519, 490704520, 490704521 })
            That(session.PlanAndCommitBooster(Noop, resources.Boosters.UsePlaces(id)).Accepted,
                "真实Town允许四盒领取地点 " + id);
        That(!session.PlanAndCommitBooster(Noop, new(SeriaRoom: true)).Accepted &&
            !session.PlanAndCommitBooster(Noop, new(PendingRule: "未知地点")).Accepted,
            "仅房间和未知地点声明在Town安全拒绝");
        That(resources.Boosters.UsePlaces(490704515) is { Village: false, SeriaRoom: false, PendingRule: null },
            "无声明的每日盒保留既有副本资格");
        const string open = "[action usable place]\n", close = "\n[/action usable place]";
        That(BoosterCatalog.ParseUsePlaces(open + "`[seria room]`" + close) is { Village: false, SeriaRoom: true, PendingRule: null },
            "仅房间声明不能放宽成整个Town");
        That(!session.PlanAndCommitBooster(Noop, BoosterCatalog.ParseUsePlaces(open + "`[dungeon]`" + close)).Accepted,
            "仅副本声明不能意外在城镇开放");
        foreach (string bad in new[] { open, open + "`[village]`", open + "`[unknown]`" + close,
            open + "`[village]` 1" + close, open + "`[village]` `[village]`" + close,
            open + "`[village]`" + close + open + "`[seria room]`" + close,
            "[nested]\n" + open + "`[village]`" + close + "\n[/nested]" })
            That(BoosterCatalog.ParseUsePlaces(bad).PendingRule is not null, "未知、重复、嵌套及未闭合地点安全Pending");
    }
}
