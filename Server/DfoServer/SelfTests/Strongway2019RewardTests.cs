using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class Strongway2019RewardTests
{
    public static void RunCore()
    {
        int before = Count;
        CompletionTakeover();
        CompletionRecovery();
        MatchingAuthority();
        AwardAndLimits();
        RejectionsAndRollback();
        PeriodBoundaries();
        MigrationAndSchema();
        Console.WriteLine($"PASS {Count - before} 首期强者之路真实终点、账号奖励额度、邮件原子提交与回放断言");
    }

    static void AwardAndLimits()
    {
        using var fixture = new Fixture();
        var firstDay = DateTimeOffset.Parse("2026-11-04T06:00:00+08:00");
        var first = fixture.Clear(fixture.First.Slot, 5359, 490012735, firstDay);
        var awarded = fixture.Repository.CommitStrongway2019Reward(fixture.Owner, first);
        That(awarded is { Granted: true, Duplicate: false, MailId: > 0 },
            "5359真实通关在原子事务内获得一封490012735邮件并消耗账号额度");
        var mail = fixture.Repository.LoadMailbox(fixture.Owner, first.Expected.Id, first.Created).Entries.Single();
        That(mail.Id == awarded.MailId && mail.Attachments is [{ Item.Core: { ItemId: 490012735, Count: 1 } }] &&
            fixture.Repository.ReadStrongway2019RewardState(fixture.Owner, new(20261104, 20261104), Strongway2019RewardBucket.Challenge) == new Strongway2019RewardState(1, 1),
            "奖励附件为完整新物品，账号共享次数与邮件一起落库");
        string after = fixture.Snapshot();
        fixture.Repository.LoadCharacter(fixture.Owner, fixture.Second.Slot);
        var replay = new CharacterRepository(fixture.Path).CommitStrongway2019Reward(fixture.Owner, first);
        That(replay is { Granted: true, Duplicate: true } && replay.MailId == awarded.MailId && fixture.Snapshot() == after &&
            fixture.Repository.LoadMailbox(fixture.Owner, fixture.Second.Id, first.Created).Entries.IsEmpty,
            "仓储重建及切换同账号角色后重放原终点，只返回原收件人的邮件且不重复占额度");
        Reject<InvalidDataException>(() => fixture.Repository.CommitStrongway2019Reward(fixture.Owner,
            first with { Item = first.Item with { Core = first.Item.Core with { Value = 2 } } }),
            "同实例不能改变冻结附件数量后重试");
        Reject<InvalidDataException>(() => fixture.Repository.CommitStrongway2019Reward(fixture.Owner,
            first with { Created = first.Created + 86400 }), "已提交回执不能跨日移动归属");

        var sameDay = fixture.Clear(fixture.Second.Slot, 5363, 490012736, firstDay.AddMinutes(1));
        That(fixture.Repository.CommitStrongway2019Reward(fixture.Owner, sameDay) is { Granted: false, MailId: 0 } &&
            fixture.Snapshot() == after, "普通和简单跨角色共享账号日一次，不留下第二封邮件或额度");
        var matching = fixture.Match(fixture.Second.Slot, firstDay.AddMinutes(2));
        That(fixture.Repository.CommitStrongway2019Reward(fixture.Owner, matching) is { Granted: true, Duplicate: false, MailId: > 0 } &&
            fixture.Repository.ReadStrongway2019RewardState(fixture.Owner, new(20261104, 20261104), Strongway2019RewardBucket.Matching) == new Strongway2019RewardState(1, 1),
            "5360胜利使用独立匹配额度，同日挑战已领奖仍可领490012737");
        var loss = fixture.Match(fixture.Second.Slot, firstDay.AddDays(1), victory: false);
        string beforeLoss = fixture.Snapshot();
        That(fixture.Repository.CommitStrongway2019Reward(fixture.Owner, loss) is { Granted: false, MailId: 0 } &&
            fixture.Snapshot() == beforeLoss &&
            fixture.Repository.ReadStrongway2019RewardState(fixture.Owner, new(20261105, 20261104), Strongway2019RewardBucket.Matching) == new Strongway2019RewardState(0, 1),
            "匹配失败不发邮件、不占次日额度或追加周次数");
        var secondDay = fixture.Clear(fixture.First.Slot, 5363, 490012736, firstDay.AddDays(1));
        var secondAward = fixture.Repository.CommitStrongway2019Reward(fixture.Owner, secondDay);
        That(secondAward is { Granted: true, MailId: > 0 } &&
            fixture.Repository.LoadMailbox(fixture.Owner, fixture.First.Id, secondDay.Created).Entries.Any(value =>
                value.Id == secondAward.MailId && value.Attachments is [{ Item.Core.ItemId: 490012736 }]),
            "次日简单模式可领取自己的736奖励，仍累计普通共享周次数");
        var exhausted = fixture.Clear(fixture.Second.Slot, 5359, 490012735, firstDay.AddDays(2));
        string exhaustedBefore = fixture.Snapshot();
        That(fixture.Repository.CommitStrongway2019Reward(fixture.Owner, exhausted) is { Granted: false, MailId: 0 } &&
            fixture.Snapshot() == exhaustedBefore &&
            new CharacterRepository(fixture.Path).ReadStrongway2019RewardState(fixture.Owner, new(20261106, 20261104), Strongway2019RewardBucket.Challenge) == new Strongway2019RewardState(0, 2),
            "账号本周两次已用尽，换角色到第三天也不能领，重读不重置周额度");
        var nextWeek = fixture.Clear(fixture.Second.Slot, 5359, 490012735, firstDay.AddDays(7));
        That(fixture.Repository.CommitStrongway2019Reward(fixture.Owner, nextWeek) is { Granted: true, MailId: > 0 } &&
            fixture.Repository.ReadStrongway2019RewardState(fixture.Owner, new(20261111, 20261111), Strongway2019RewardBucket.Challenge) == new Strongway2019RewardState(1, 1),
            "下周三06点新周期可以领奖，旧周永久回执不被删除");
    }

    static void RejectionsAndRollback()
    {
        using var fixture = new Fixture();
        var at = DateTimeOffset.Parse("2026-11-04T06:00:00+08:00");
        var plan = fixture.Clear(fixture.First.Slot, 5359, 490012735, at);
        string before = fixture.Snapshot();
        foreach (var invalid in new[]
        {
            plan with { Fact = plan.Fact! with { } },
            plan with { Fact = plan.Fact! with { Identity = plan.Fact!.Identity with { Epoch = plan.Fact!.Identity.Epoch + 1 } } },
            plan with { Expected = fixture.Second },
            plan with { Item = plan.Item with { Core = plan.Item.Core with { ItemId = 490012736 } } }
        })
            Reject<InvalidDataException>(() => fixture.Repository.CommitStrongway2019Reward(fixture.Owner, invalid),
                "首次发奖拒绝复制事实、旧epoch、错角色或错误5359产物");
        using (var foreign = fixture.Owners.Acquire("strongway-foreign", () => { }))
            Reject<InvalidOperationException>(() => fixture.Repository.CommitStrongway2019Reward(foreign, plan),
                "其他账号owner不能消费本角色真实通关");
        That(fixture.Snapshot() == before, "身份或产物拒绝不写邮件、物品或奖励额度");
        var changed = fixture.Repository.Save(fixture.Owner, plan.Expected, new(38, 0, 450, 234), null);
        Reject<InvalidOperationException>(() => fixture.Repository.CommitStrongway2019Reward(fixture.Owner, plan),
            "首次发奖重新核对角色revision，旧快照不能提交奖励");
        var oldAttempt = plan with { Expected = changed };
        var currentAttempt = fixture.Clear(fixture.First.Slot, 5359, 490012735, at);
        oldAttempt = oldAttempt with { Expected = currentAttempt.Expected };
        Reject<InvalidOperationException>(() => fixture.Repository.CommitStrongway2019Reward(fixture.Owner, oldAttempt),
            "已存在新的正式入场attempt时，不能凭旧Run终点补领首次奖励");
        foreach (int dungeon in new[] { 5364, 9999 })
        {
            var unsupported = fixture.Clear(fixture.Second.Slot, dungeon, 490012735, at);
            Reject<InvalidDataException>(() => fixture.Repository.CommitStrongway2019Reward(fixture.Owner, unsupported),
                "未开放英雄5364和无关地下城不能套用首期奖励");
        }
        var rollbackPlan = fixture.Clear(fixture.First.Slot, 5359, 490012735, at);
        string rollbackBefore = fixture.Snapshot();
        fixture.Sql("CREATE TRIGGER strongway_reward_fail BEFORE INSERT ON account_strongway2019_rewards BEGIN SELECT RAISE(ABORT,'strongway rollback'); END");
        Reject<SqliteException>(() => fixture.Repository.CommitStrongway2019Reward(fixture.Owner, rollbackPlan),
            "邮件已投递后的奖励回执晚期SQL失败必须整笔回滚");
        fixture.Sql("DROP TRIGGER strongway_reward_fail");
        That(fixture.Snapshot() == rollbackBefore &&
            fixture.Repository.ReadStrongway2019RewardState(fixture.Owner, new(20261104, 20261104), Strongway2019RewardBucket.Challenge) == new Strongway2019RewardState(0, 0),
            "SQL失败不留下邮件附件、分配物品或已耗奖励额度");
        var stale = fixture.Owner;
        fixture.Owner = fixture.Owners.Acquire(Fixture.Account, () => { });
        Reject<InvalidOperationException>(() => fixture.Repository.CommitStrongway2019Reward(stale, rollbackPlan),
            "旧owner generation即使持有真实终点也不能提交奖励");
        var awarded = fixture.Repository.CommitStrongway2019Reward(fixture.Owner, rollbackPlan);
        That(awarded is { Granted: true, Duplicate: false, MailId: > 0 } &&
            new CharacterRepository(fixture.Path).CommitStrongway2019Reward(fixture.Owner, rollbackPlan) is { Granted: true, Duplicate: true },
            "当前owner重试同一冻结终点成功，重建仓储只回放这一份邮件");
        Reject<InvalidOperationException>(() => fixture.Repository.CommitStrongway2019Reward(stale, rollbackPlan),
            "永久回执已存在也不能绕过旧owner generation检查");
    }

    static void MatchingAuthority()
    {
        using var fixture = new Fixture();
        var at = DateTimeOffset.Parse("2026-11-04T06:00:00+08:00");
        var exit = fixture.Match(fixture.First.Slot, at, exit: true);
        That(exit.Fact is null && exit.Run.ClearFact is null && exit.Terminal is { Reason: Strongway2019FinishReason.Exit, RewardParticipants.Length: 4 },
            "真实竞赛对侧退出冻结四名胜者，胜者不伪造个人ClearFact");
        var awarded = fixture.Repository.CommitStrongway2019Reward(fixture.Owner, exit);
        That(awarded is { Granted: true, Duplicate: false, MailId: > 0 } &&
            fixture.Repository.LoadMailbox(fixture.Owner, exit.Expected.Id, exit.Created).Entries.Single().Attachments is [{ Item.Core.ItemId: 490012737 }],
            "权威退出胜利虽无ClearFact仍完整发匹配奖励，不丢胜队邮件");
        string before = fixture.Snapshot();
        That(exit.Run.Return(exit.Run.Scene.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted &&
            exit.Run.Scene.Identity != exit.Identity, "退出胜者后续返城使Run epoch推进，奖励计划仍保留原冻结身份");
        That(new CharacterRepository(fixture.Path).CommitStrongway2019Reward(fixture.Owner, exit) is { Granted: true, Duplicate: true } &&
            fixture.Snapshot() == before, "退出胜利永久回执重放不重复邮件或额度");
        using var other = new Fixture();
        var clear = other.Match(other.First.Slot, at);
        Reject<InvalidDataException>(() => other.Repository.CommitStrongway2019Reward(other.Owner, clear with { Terminal = clear.Terminal! with { } }),
            "值相同的复制Terminal不能冒充原竞赛签发的终局引用");
        Reject<InvalidDataException>(() => other.Repository.CommitStrongway2019Reward(other.Owner, clear with { Competition = null }),
            "孤立终局或真实ClearFact不能绕过匹配胜队鉴权");
        var copied = clear with { Fact = clear.Fact! with { } };
        Reject<InvalidDataException>(() => other.Repository.CommitStrongway2019Reward(other.Owner, copied),
            "匹配清图如附个人ClearFact也必须保留原Run引用");
        That(other.Repository.CommitStrongway2019Reward(other.Owner, clear) is { Granted: true, MailId: > 0 },
            "真实终点胜利以固定竞赛终局鉴权并发匹配邮件");
    }

    static void PeriodBoundaries()
    {
        foreach (var (instant, day, week) in new[]
        {
            ("2026-11-03T05:59:59+08:00", 20261102, 20261028),
            ("2026-11-03T06:00:00+08:00", 20261103, 20261028),
            ("2026-11-04T05:59:59+08:00", 20261103, 20261028),
            ("2026-11-04T06:00:00+08:00", 20261104, 20261104),
            ("2026-11-03T22:00:00Z", 20261104, 20261104)
        })
            That(Strongway2019RewardPeriod.At(DateTimeOffset.Parse(instant)) == new Strongway2019RewardPeriod(day, week),
                "首期奖励按UTC+8日06点、周三06点划界，不使用机器时区或团本周四：" + instant);
    }

    static void MigrationAndSchema()
    {
        using (var legacy = new Fixture())
        {
            string first = JsonSerializer.Serialize(legacy.Repository.LoadCharacter(legacy.Owner, legacy.First.Slot));
            string second = JsonSerializer.Serialize(legacy.Repository.LoadCharacter(legacy.Owner, legacy.Second.Slot));
            legacy.Sql("DROP TABLE account_strongway2019_rewards; PRAGMA user_version=176");
            var upgraded = new CharacterRepository(legacy.Path);
            That(legacy.Scalar("PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                legacy.Scalar("SELECT count(*) FROM account_strongway2019_rewards") is 0L &&
                JsonSerializer.Serialize(upgraded.LoadCharacter(legacy.Owner, legacy.First.Slot)) == first &&
                JsonSerializer.Serialize(upgraded.LoadCharacter(legacy.Owner, legacy.Second.Slot)) == second,
                "真实176旧库建立空奖励回执并保留已有角色完整快照，不补造历史额度");
            That(upgraded.ReadStrongway2019RewardState(legacy.Owner, new(20261104, 20261104), Strongway2019RewardBucket.Challenge)
                == new Strongway2019RewardState(0, 0), "176迁移后的账号没有凭空占用日周额度");
        }
        using (var malformed = new Fixture())
        {
            string first = JsonSerializer.Serialize(malformed.Repository.LoadCharacter(malformed.Owner, malformed.First.Slot));
            malformed.Sql("ALTER TABLE account_strongway2019_rewards ADD COLUMN unknown INTEGER");
            Reject<InvalidDataException>(() => new CharacterRepository(malformed.Path),
                "当前版本奖励账本形状被修改时拒绝打开，不把未知结构静默当作正式表");
            That(malformed.Scalar("PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                malformed.Scalar("SELECT count(*) FROM pragma_table_info('account_strongway2019_rewards') WHERE name='unknown'") is 1L &&
                JsonSerializer.Serialize(malformed.Repository.LoadCharacter(malformed.Owner, malformed.First.Slot)) == first,
                "schema拒绝保留原结构、版本与既有角色，不删表自动修复");
        }
    }

    sealed class Fixture : IDisposable
    {
        internal const string Account = "strongway-reward";
        readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-strongway-reward-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public CharacterRepository Repository { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; set; }
        public Character First { get; }
        public Character Second { get; }

        public Fixture()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "game.db");
            Repository = new(Path);
            Owner = Owners.Acquire(Account, () => { });
            First = Repository.Create(Owner, new("SWFirst", new(38, 1, 450, 234), null)
                { Level = 95, DungeonState = new(256, 0, 0, 0, 0) });
            Second = Repository.Create(Owner, new("SWSecond", new(38, 1, 450, 234), null)
                { Level = 95, DungeonState = new(256, 0, 0, 0, 0) });
        }

        public Strongway2019RewardPlan Clear(ushort slot, int dungeon, int item, DateTimeOffset at)
        {
            var (plan, authority, monster) = Start(slot, dungeon, item, at);
            var run = plan.Run;
            var death = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, monster)).Pending!;
            That(death.Result.ClearFact is not null && run.ConfirmCommittedDeath(death).ClearFact is { } fact && ReferenceEquals(run.ClearFact, fact),
                "实际权威终点死亡经Run确认产生唯一ClearFact，不手工构造可领奖事实");
            return plan with { Fact = run.ClearFact, Identity = run.ClearFact!.Identity };
        }

        public Strongway2019RewardPlan Match(ushort slot, DateTimeOffset at, bool victory = true, bool exit = false)
        {
            var (plan, authority, monster) = Start(slot, 5360, 490012737, at);
            var fillers = Enumerable.Range(100, 7).Select(id => Strongway2019CompetitionTests.CreateRewardRun(checked((ushort)id), Guid.NewGuid(), 1)).ToArray();
            SinglePlayerDungeonRun[] first = [plan.Run, .. fillers.Take(3)];
            SinglePlayerDungeonRun[] second = fillers.Skip(3).ToArray();
            var competition = new Strongway2019Competition(Guid.NewGuid(), first, second,
                Strongway2019CompetitionTests.Catalog(30, 10_000), TimeSpan.FromSeconds(600));
            competition.ConfirmOpening(plan.Run, plan.Run.Scene.Identity, 3);
            competition.ConfirmOpening(second[0], second[0].Scene.Identity, 3);
            competition.ConfirmOpening(plan.Run, plan.Run.Scene.Identity, 4);
            competition.ConfirmOpening(second[0], second[0].Scene.Identity, 4);
            if (!victory) competition.Exit(plan.Run, plan.Run.Scene.Identity);
            else if (exit) competition.Exit(second[0], second[0].Scene.Identity);
            else
            {
                var pending = plan.Run.PrepareConfirmedDeath(authority.Death(plan.Run.Scene.Identity, monster)).Pending!;
                plan.Run.ConfirmCommittedDeath(pending);
                competition.ObserveClear(plan.Run, plan.Run.Scene.Identity, plan.Run.ClearFact!);
                plan = plan with { Fact = plan.Run.ClearFact, Identity = plan.Run.ClearFact!.Identity };
            }
            return plan with { Competition = competition, Terminal = competition.Snapshot.Terminal };
        }

        internal (Strongway2019RewardPlan Plan, FixtureAuthority Authority, ushort Monster) Start(ushort slot, int dungeon, int item, DateTimeOffset at)
        {
            var current = Repository.LoadCharacter(Owner, slot);
            var map = new DungeonMap("boss", new(0, 0), 1, "map/strongway-reward-fixture.map");
            var maze = new DungeonMaze(0, 1, 1, "strongway-reward-fixture", [map.Position], [map.Position], [map], []);
            var entry = new DungeonEntryPlan(current.Id, current.Revision + 1, checked((uint)dungeon), 0, byte.MaxValue, 0, maze, map, map)
                { DisableMonsterAndClearExperience = true, DisableFatigueConsumption = true, Strongway2019TimeLimitSeconds = dungeon == 5360 ? null : 3600 };
            var monster = new DungeonRoomMonster(1, [1, 0, 95, 100, 100, 0, 1, 1], "[fixed]", "[boss]", "monster/strongway-fixture.mob");
            var monsters = DungeonMonsterPlan.FromRoom(new(map, [monster], [], []), current.Id, checked((ushort)current.DungeonState!.NextObjectKey));
            var authority = new FixtureAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry, current.DungeonState.LastAttempt + 1, 1, new([monsters], []), authority, out var created, out _),
                "合成末图仍由真实SinglePlayerDungeonRun校验怪物计划和尝试身份");
            var run = created!;
            current = Repository.ReserveDungeonAttempt(Owner, current, current.DungeonState.NextObjectKey + 100);
            That(run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
                "真实入场回执保存后完成加载");
            var attachment = new InventoryItem(0, new(InventoryListType.Main, 9), new(item, ItemKind.Material, 1));
            return (new(current, run, null, attachment, at.ToUnixTimeSeconds()), authority, monsters.Monsters.Single().ObjectKey);
        }

        public void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Path + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }

        public string Snapshot()
        {
            using var db = new SqliteConnection("Data Source=" + Path + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT (SELECT count(*) FROM account_strongway2019_rewards)||':'||(SELECT count(*) FROM mailbox_messages)||':'||(SELECT count(*) FROM mailbox_attachments)||':'||(SELECT count(*) FROM character_inventory_items)";
            return (string)command.ExecuteScalar()!;
        }

        public object? Scalar(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Path + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql;
            return command.ExecuteScalar();
        }

        public void Dispose()
        {
            Owner.Dispose();
            TestFiles.DeleteDirectory(directory, true);
        }
    }

    internal sealed class FixtureAuthority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key) => ConfirmDeath(identity, Guid.NewGuid(), key, 1);
    }
}
