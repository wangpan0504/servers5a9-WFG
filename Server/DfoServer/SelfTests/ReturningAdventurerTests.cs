using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Events;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ReturningAdventurerTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow.AddDays(1);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public static void RunCore()
    {
        Bytes(AdventurerBenefitPackets.Returning(0x12345678, 1, 2),
            Convert.FromHexString("01000000010001000200000078563412"), "原生欢迎可领、每日已领状态各占u16");
        var state = new ReturningAdventurerState(1, 1, 1, [new(4, 1, 1), new(20, 7, 0)]);
        byte[] expected = new byte[1200]; expected[0] = 4; expected[4] = 1; expected[8] = 1; expected[12] = 20; expected[16] = 7;
        Bytes(AdventurerBenefitPackets.ReturningMissions(state), expected, "1762严格100条12B槽，其余槽全零");
        Bytes(AdventurerBenefitPackets.ReturningAck(), new byte[12], "1734原生只读12B应答");
        That(ReturningAdventurerRequests.Read(Convert.FromHexString("0400000024000000")) == new ReturningAdventurerRequest(4, 36) &&
            ReturningAdventurerRequests.Read(new byte[7]) is null && ReturningAdventurerRequests.Read(new byte[21]) is null,
            "1734正文严格8B，不能把含帧头21B当正文");
        using var console = new StringWriter(); using var log = new ServerLog("", console, fileEnabled: false);
        log.Write("returning_adventurer_claim", new { Character = 1, Action = 1, Argument = 0, Accepted = true });
        log.Write("returning_adventurer_rejected", new { Character = 1, Action = 1, Argument = 0, Accepted = false });
        That(console.ToString().Contains("[INFO] [returning_adventurer_claim] Returning adventurer reward committed") &&
            console.ToString().Contains("[WARN] [returning_adventurer_rejected] Returning adventurer reward rejected"),
            "回归领奖实际提交与拒绝采用独立英文日志和正确级别");
    }

    public static void RunResources(PvfArchive pvf)
    {
        int before = Count;
        var catalog = new ReturningAdventurerCatalog(pvf);
        That(catalog.Missions.Length == 36 && catalog.Missions.Select(row => row.Id).SequenceEqual(Enumerable.Range(1, 36)) &&
            catalog.Welcome == new ReturningAdventurerReward(490013906, 1) && catalog.Daily == new ReturningAdventurerReward(490013907, 1),
            "真实36条回归任务及身份1欢迎奖励，不混发新手礼包");
        That(catalog.Missions[3] is { Level: 70, Type: "dungeon clear", Count: 1, Conditions: [400000096] } &&
            catalog.Missions[19] is { Count: 8, Conditions.Length: 13 }, "遗迹及多副本累计任务保留原生门槛和计数");
        var resources = new ItemResources(pvf);
        string root = Path.Combine(Path.GetTempPath(), "s5a9-returning-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "game.db");
        try
        {
            var clock = new Clock(); var owners = new AccountOwners();
            using var owner = owners.Acquire("returning", () => { });
            var benefits = new AdventurerBenefitService(new(pvf), AdventurerBenefitConfiguration.Default, _ => { });
            var settings = new GameDebugSettings { AdventurerBenefits = benefits };
            var repository = new CharacterRepository(path);
            repository.ConfigureReturningAdventurers(catalog, benefits, _ => 5000, clock);
            var service = new ReturningAdventurerService(repository, catalog, benefits, resources.Items, resources.GoldLimits,
                resources.EpicPieces, settings, clock);
            var character = repository.Create(owner, new("ReturnTest", new(1, 0, 100, 100), null));
            That(service.Read(owner, character).Until == 0 && service.Prepare(owner, character, 1, 0).Claim is null,
                "普通账号无任务奖励资格");
            long until = benefits.SetReturning(owner.Account, clock.Now.ToUnixTimeSeconds());
            That(benefits.SetReturning(owner.Account, clock.Now.AddHours(1).ToUnixTimeSeconds()) == until,
                "重复GM设置不延长或更换仍有效的福利周期");
            var state = service.Read(owner, character);
            That(state.Welcome == 1 && state.Daily == 1 && state.Missions.Length == 36 && state.Missions.All(row => row.State == 0),
                "回归立即有欢迎及每日可领奖，未达成任务不会被伪造完成");
            character = repository.CommitLearning(owner, character, new(0, Array.AsReadOnly(new CharacterSkillTree[]
                { new(0, 0, 0, []), new(1, 0, 0, []) })));
            That(service.Read(owner, character).Missions.Single(row => row.Id == 2).Progress == 0,
                "登录初始化技能树不算技能设置任务");
            character = repository.CommitLearning(owner, character, character.Learning);
            That(service.Read(owner, character).Missions.Single(row => row.Id == 2) is { Progress: 1, State: 1 },
                "真实保存技能事务推进任务，低等级其他任务保留不可领");
            character = Claim(1, 0, character);
            That(service.Read(owner, character).Welcome == 2 && service.Prepare(owner, character, 1, 0).Claim is null && Claims() == 1,
                "欢迎礼包实际入包且重复领取无写入");
            character = Claim(4, 2, character);
            That(service.Read(owner, character).Missions.Single(row => row.Id == 2).State == 2 &&
                service.Prepare(owner, character, 4, 2).Claim is null && Claims() == 2, "任务领取成功后原生状态2，重复点击不发第二份");
            var daily = service.Prepare(owner, character, 3, 0);
            That(daily.Claim is not null, "每日礼包形成背包计划");
            Sql("CREATE TRIGGER returning_fail BEFORE UPDATE OF revision ON characters BEGIN SELECT RAISE(ABORT,'returning rollback'); END");
            Reject<SqliteException>(() => service.Commit(owner, character, daily.Claim!), "账本先写后背包失败必须整体回滚");
            Sql("DROP TRIGGER returning_fail");
            That(Claims() == 2 && service.Read(owner, character).Daily == 1 && repository.List(owner).Single().Revision == character.Revision,
                "晚故障回滚领取账本、资产及revision，重试资格保留");
            character = Claim(3, 0, character);
            That(Claims() == 3 && service.Read(owner, character).Daily == 2, "每日奖励提交一次");
            var other = repository.Create(owner, new("ReturnAlt", character.Position, null));
            That(service.Read(owner, other).Welcome == 2 && service.Read(owner, other).Daily == 2 &&
                service.Read(owner, other).Missions.Single(row => row.Id == 2).State == 2, "账号换角共享周期领取账本");
            var reopened = new CharacterRepository(path);
            reopened.ConfigureReturningAdventurers(catalog, benefits, _ => 5000, clock);
            That(reopened.ReadReturningAdventurer(owner, character).Welcome == 2 && Claims() == 3,
                "重开数据库保留领取状态及任务，不重发奖励");
            clock.Now = clock.Now.AddDays(1);
            That(service.Read(owner, character).Daily == 1 && service.Read(owner, character).Welcome == 2,
                "服务日变更只开放每日礼包，不重置欢迎和任务奖励");
            var template = resources.Items.Get(catalog.Daily.ItemId);
            var range = ItemSlotBoundService.Range(template.Kind, other.Inventory.Expansion);
            var fullCore = InventoryRewardGrantService.Create(template, template.StackLimit, clock.Now,
                shopExpiration: settings.IgnoreCashShopAbsoluteExpiration);
            var occupied = Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot =>
                new InventoryItem(0, new(InventoryListType.Main, checked((short)slot)), fullCore)).ToImmutableArray();
            other = repository.CommitInventory(owner, other, new(other.Inventory, other.Inventory with { Items = occupied }, "returning_full_bag_fixture"));
            var full = service.Prepare(owner, other, 3, 0);
            That(full.Claim is null && full.Inventory?.Accepted == false && Claims() == 3 && service.Read(owner, other).Daily == 1,
                "背包满拒绝领取并保留资格，不偷偷转邮件或记录已领");
            var pending = service.Prepare(owner, character, 3, 0).Claim!;
            clock.Now = DateTimeOffset.FromUnixTimeSeconds(until);
            That(!service.Commit(owner, character, pending).Accepted && service.Read(owner, character).Until == 0 && Claims() == 3,
                "规划后到期的奖励在写事务内拒绝，不产生领取事实");

            Character Claim(uint action, uint argument, Character current)
            {
                var plan = service.Prepare(owner, current, action, argument);
                That(plan.Claim is not null && plan.Inventory?.Accepted == true, "真实资源奖励通过背包容量规划");
                var committed = service.Commit(owner, current, plan.Claim!);
                That(committed.Accepted && committed.Character is not null, "背包与账号领取账本同事务提交");
                return committed.Character!;
            }
            long Claims()
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM account_returning_reward_claims";
                return (long)command.ExecuteScalar()!;
            }
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - before} returning adventurer resource/storage assertions");
    }
}
