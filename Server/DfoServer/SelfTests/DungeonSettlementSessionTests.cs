using static DfoServer.Testing.TestCheck;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer;

internal static class DungeonSettlementSessionTests
{
    // 使用匹配资源与明确合成死亡，只验证正式Session和文件库，不代表实机通关。
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int before = TestCheck.Count;
        var learning = SkillLearningCatalog.Load(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf);
        var kills = DungeonKillExperienceCatalog.Load(pvf);
        var dungeons = DungeonCatalog.Load(pvf);
        var rooms = DungeonRoomCatalog.Load(pvf);
        var quests = DfoServer.GameWorld.Quests.QuestCatalog.Load(pvf);
        var clear = DungeonClearExperienceCatalog.Load(pvf);
        var definition = dungeons.Get(3);
        var maps = definition.Mazes.Single(maze => maze.ConnectsQuest(3145)).MapSpecification!;
        var expectedReward = clear.Calculate(definition, 0, 99);
        CommittedSettlement();
        ReturnWithoutSettlement();
        PublicationGuard();
        RetryCollectionRules();
        Console.WriteLine($"PASS {TestCheck.Count - before} 通关Session经验、冻结报告、空翻牌及返回断言（非实机）");

        void CommittedSettlement()
        {
            using var f = new Fixture("ClearSession", towns, characters, learning, quests);
            const uint expectedKills = 27 * 93 + 137;
            // 选择合法角色初值，使结算而非最后一次击杀跨越等级边界。
            ulong midpoint = expectedKills + expectedReward.TotalExperience / 2UL;
            ulong boundary = growth.CumulativeExperience.Skip(1).Take(CharacterGrowthCatalog.MaximumSupportedLevel - 1)
                .First(value => value >= growth.GetLevelStartExperience(3) + midpoint);
            ulong initialExperience = boundary - midpoint;
            byte initialLevel = checked((byte)growth.CumulativeExperience.Take(CharacterGrowthCatalog.MaximumSupportedLevel)
                .TakeWhile(value => value <= initialExperience).Count());
            f.Sql($"UPDATE characters SET level={initialLevel},experience={initialExperience} WHERE id={f.Created.Id}");
            f.Sql($"UPDATE character_skill_points SET sp=sp+{growth.GetSpDelta(1, initialLevel)} WHERE character_id={f.Created.Id}");
            f.SelectEntry();
            var entry = Enter(f);
            byte[] report = [1, 2, 3, 4];
            Check(f.Session.ConfirmFormalDungeonBoss(f.Created.Id, entry.Monsters.Monsters[0].ObjectKey, 3, 504) is null &&
                Settle(f, report).Outcome == DungeonRunOutcome.Rejected && f.Settlements == 0,
                "未加载未清图的Boss协调和结果报告不创建通关事实或奖励");
            Check(f.Session.AdvanceFormalDungeonCards(false) is null && f.Session.AdvanceFormalDungeonCards(true) is null &&
                f.Session.SelectFormalDungeonEmptyCard(0, 0) is null && f.Session.FormalDungeonResultFocus() is null,
                "结算前不开放评分、翻牌和结果焦点");
            var completed = ClearRun(f);
            var killed = f.Session.Selected!;
            Check(completed.Experience == expectedKills && killed.Learning.Experience == initialExperience + expectedKills &&
                f.Kills == completed.Count && f.Settlements == 0 && f.Session.Phase == SessionPhase.Settlement,
                "六房正式确认只落击杀经验，通关奖励等待结果报告");
            Check(f.Session.ConfirmFormalDungeonBoss((ushort)(f.Created.Id + 1), completed.Boss.ObjectKey, 6, 0) is null &&
                f.Session.ConfirmFormalDungeonBoss(f.Created.Id, (ushort)(completed.Boss.ObjectKey + 1), 6, 0) is null &&
                f.Session.ConfirmFormalDungeonBoss(f.Created.Id, completed.Boss.ObjectKey, 4, 0) is null &&
                f.Session.ConfirmFormalDungeonBoss(f.Created.Id, completed.Boss.ObjectKey, 6, 1) is null,
                "Boss协调绑定当前角色、已死亡Boss key及已接受报告的等级/HP原值");
            var bossReceipt = f.Session.ConfirmFormalDungeonBoss(f.Created.Id, completed.Boss.ObjectKey, 6, 0);
            Check(bossReceipt is not null && f.Session.ConfirmFormalDungeonBoss(f.Created.Id, completed.Boss.ObjectKey, 6, 0) is not null &&
                ReferenceEquals(killed, f.Session.Selected) && Same(killed, f.Read()) && f.Settlements == 0,
                "不同生成等级及零HP的已接受报告可协调Boss，重复不击杀、不加经验或写通关账本");
            var enable = f.Session.EnableFormalDungeonSettlement(completed.Boss.ObjectKey);
            Check(enable is { } accepted && accepted.BossKey == completed.Boss.ObjectKey &&
                accepted.Receipt.Snapshot.LivingMonsters.Count == 1 && f.Kills == completed.Count,
                "结算通知引用已确认敌对Boss，任务友方仍存活且不伪造击杀");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enable!.Value.Receipt);
            Check(f.Session.EnableFormalDungeonSettlement(completed.Boss.ObjectKey) is null,
                "已发布开启通知的同Boss协调不再次开启结果页");
            Check(f.Session.SettleFormalDungeon((ushort)(f.Created.Id + 1), 99, 99, report, dungeons, clear, growth).Outcome == DungeonRunOutcome.Rejected &&
                f.Session.SettleFormalDungeon(f.Created.Id, 99, 98, report, dungeons, clear, growth).Outcome == DungeonRunOutcome.Rejected &&
                Settle(f, []).Outcome == DungeonRunOutcome.Rejected && f.Settlements == 0,
                "不匹配本人/最高评分及空报告不能冻结通关准备");

            // 早期存储拒绝用于验证Session待提交门禁；晚期回滚和提交回执恢复由仓储用例覆盖。
            f.Sql("CREATE TRIGGER clear_session_fail BEFORE UPDATE OF experience ON characters BEGIN SELECT RAISE(ABORT,'clear session pending'); END");
            Reject<SqliteException>(() => Settle(f, report), "经验写入失败保留同一结算准备");
            Check(ReferenceEquals(killed, f.Session.Selected) && Same(killed, f.Read()) && f.Settlements == 0 &&
                f.Kills == completed.Count && f.Session.ReturnFromFormalDungeon() is null &&
                f.Session.PickFormalDungeonItem(ushort.MaxValue, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected,
                "结算存储失败不提前更新角色，待提交阶段拒绝退出及拾取");
            Check(f.Session.ConfirmFormalDungeonBoss(f.Created.Id, completed.Boss.ObjectKey, 6, 0) is null &&
                f.Session.AdvanceFormalDungeonCards(false) is null && f.Session.AdvanceFormalDungeonCards(true) is null &&
                f.Session.SelectFormalDungeonEmptyCard(0, 0) is null && f.Session.FormalDungeonResultFocus() is null &&
                Settle(f, [9, 2, 3, 4]).Outcome == DungeonRunOutcome.Rejected,
                "待提交准备不开放结果流程，篡改报告不替换原准备");
            f.Sql("DROP TRIGGER clear_session_fail");
            var settled = Settle(f, report);
            var committed = f.Session.Selected!;
            Check(settled.Outcome == DungeonRunOutcome.Accepted && settled.Receipt is not null && settled.Reward == expectedReward &&
                settled.Presentation is { RankPoint: 99, RankGrade: 99 } presentation && presentation.MonsterExperience == expectedKills &&
                f.Settlements == 1 && f.Kills == completed.Count && Same(committed, f.Read()) &&
                f.Session.CanPublishFormalDungeon(settled.Receipt) && !f.Session.CanPublishFormalDungeon(bossReceipt!),
                "原报告重试提交一条通关receipt，展示冻结评分与击杀总量并安装实际角色回执");
            Check(committed.Learning.Experience == killed.Learning.Experience + expectedReward.TotalExperience &&
                committed.Level > killed.Level && committed.Revision == killed.Revision + 1 &&
                committed.Learning.Trees.All(tree => tree.Sp == killed.Learning.Trees.Single(old => old.Context == tree.Context).Sp +
                    growth.GetSpDelta(killed.Level, committed.Level)),
                "通关仅加基础加评分经验，跨级后两树SP按实际等级差提交，不重加击杀经验");
            Check(JsonSerializer.Serialize(committed.Inventory) == JsonSerializer.Serialize(killed.Inventory) &&
                committed.DungeonState == killed.DungeonState && committed.ActiveMainQuests.SequenceEqual(killed.ActiveMainQuests) &&
                committed.CompletedQuests.SequenceEqual(killed.CompletedQuests),
                "通关经验不改变金币、物品、副本高水位或任务奖励进度");

            byte[] frozenReport = report.ToArray();
            report[0] = 9;
            var replay = Settle(f, frozenReport);
            Check(replay.Outcome == DungeonRunOutcome.Duplicate && replay.Reward == settled.Reward && replay.Presentation == settled.Presentation &&
                ReferenceEquals(committed, f.Session.Selected) && Same(committed, f.Read()) && f.Settlements == 1 && f.Kills == completed.Count,
                "首报告复制冻结，调用方修改原数组后精确报告重放不再发经验或改展示");
            Check(Settle(f, report).Outcome == DungeonRunOutcome.Rejected && Settle(f, [1, 2, 3]).Outcome == DungeonRunOutcome.Rejected &&
                ReferenceEquals(committed, f.Session.Selected) && f.Settlements == 1,
                "已提交通关拒绝变更字段或宽度的报告");

            Check(f.Session.AdvanceFormalDungeonCards(true) is null && f.Session.SelectFormalDungeonEmptyCard(0, 0) is null,
                "提交经验后仍须69评分完成才允许70展示卡牌及71选择");
            Check(f.Session.AdvanceFormalDungeonCards(false) is not null && f.Session.AdvanceFormalDungeonCards(false) is not null &&
                f.Session.AdvanceFormalDungeonCards(true) is not null && f.Session.AdvanceFormalDungeonCards(true) is not null,
                "69和70合法顺序及精确重复只推进结算展示状态");
            Check(f.Session.SelectFormalDungeonEmptyCard(1, 0) is null && f.Session.SelectFormalDungeonEmptyCard(0, 8) is null &&
                f.Session.SelectFormalDungeonEmptyCard(0, 0) is not null && f.Session.SelectFormalDungeonEmptyCard(0, 0) is not null &&
                f.Session.SelectFormalDungeonEmptyCard(0, 4) is null && f.Session.FormalDungeonResultFocus() is not null,
                "只允许本人免费行，重复幂等、其他成员席拒绝，72焦点不退出或发奖");
            Check(ReferenceEquals(committed, f.Session.Selected) && Same(committed, f.Read()) && f.Settlements == 1 &&
                f.Kills == completed.Count && f.Session.Phase == SessionPhase.Settlement,
                "空翻牌全过程不新增物品金币、扣费用或再次写经验");
            Check(f.Session.ReturnFromFormalDungeon() is not null && f.Session.Phase == SessionPhase.Town && f.Session.DungeonSnapshot is null &&
                Same(committed, f.Read()) && !f.Session.CanPublishFormalDungeon(settled.Receipt!),
                "空翻牌后42清理实例并保留实际经验，旧结算通知失效");
            Check(Settle(f, frozenReport).Outcome == DungeonRunOutcome.Rejected && f.Session.AdvanceFormalDungeonCards(false) is null &&
                f.Session.SelectFormalDungeonEmptyCard(0, 3) is null && f.Session.FormalDungeonResultFocus() is null,
                "退出后迟到报告、翻牌及焦点不访问旧实例");
        }

        void ReturnWithoutSettlement()
        {
            using var f = new Fixture("ClearReturn", towns, characters, learning, quests);
            f.SelectEntry();
            Enter(f);
            ClearRun(f);
            var killed = f.Session.Selected!;
            Check(f.Session.ReturnFromFormalDungeon() is not null && f.Session.Phase == SessionPhase.Town &&
                f.Session.DungeonSnapshot is null && Same(killed, f.Read()) && f.Settlements == 0,
                "未报告结算的清图实例仍能42返回，只保留已提交击杀经验");
        }

        void PublicationGuard()
        {
            using var f = new Fixture("ClearGuard", towns, characters, learning, quests);
            f.SelectEntry(); Enter(f); ClearRun(f);
            var result = Settle(f, [1, 2, 3]);
            var receipt = result.Receipt!;
            Check(f.Session.CanPublishDungeonSettlement(receipt), "结算投影绑定实际提交角色和原实例");
            f.Session.UpdateProgress(new(CharacterProgressField.Level, f.Account, f.Created.Name, f.Session.Selected!.Level, 0));
            Reject<InvalidOperationException>(() => f.Session.CanPublishDungeonSettlement(receipt),
                "同阶段GM替换快照不能静默跳过结算通知，旧投影明确失效");
            f.Session.ReturnToRoster();
            Check(!f.Session.CanPublishDungeonSettlement(receipt), "旧选角结果不可发往后续名册阶段");
        }

        void RetryCollectionRules()
        {
            using var f = new Fixture("ClearRetry", towns, characters, learning, quests);
            f.SelectEntry();
            var drops = DungeonMonsterDropCatalog.Load(pvf);
            var pool = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(itemResources.Items.Get(1000), 50) });
            // 明示fixture概率和数量只验证规则传递，不改变Runtime资源掉率。
            var generation = new DungeonDropGenerationPlan("明确合成再挑战规则/实际资源模板", [
                new(10000, pool, 3, 3), new(10000, null, 1, 1, new(1, 17, 0))]);
            var entry = f.Session.EnterFormalDungeon(3, 3145, 0, dungeons, rooms, drops, itemResources.Items,
                draw: _ => 0, prepareDrops: (_, _) => generation)!;
            var completed = ClearRun(f);
            var settled = Settle(f, [1, 2, 3]);
            var retry = f.Session.RetryFormalDungeon(settled.Receipt!, dungeons, rooms, drops, itemResources.Items);
            Check(retry is not null && retry.Snapshot.Identity.InstanceId != entry.Snapshot.Identity.InstanceId &&
                retry.Snapshot.Identity.TaskAttempt == entry.Snapshot.Identity.TaskAttempt + 1 &&
                retry.Character.DungeonState!.NextObjectKey > entry.Character.DungeonState!.NextObjectKey && f.Kills == completed.Count,
                "集合再挑战预留独立实例与更高对象key，准入不复用死亡或旧资产结果");
            var loaded = f.Session.CompleteFormalDungeonLoading()!;
            var monster = loaded.Monsters.Monsters.First(row => row.IsBlocking);
            var death = f.Session.ObserveFormalDungeonDeath(new(monster.ObjectKey, f.Created.Id, 0x0111, f.Created.Id,
                600, 3, 0, (uint)monster.CandidateLevel, 504, 620, 3, true, new string('B', 64)), kills, growth);
            Check(death.Outcome == DungeonRunOutcome.Accepted && death.Drops.Length == 2 &&
                death.Drops.Any(row => !row.IsGold && row.Core.ItemId == 1000 && row.Core.Count == 3) &&
                death.Drops.Any(row => row.IsGold && row.GoldAmount == 17) &&
                death.Drops.All(row => row.DeathIdentity.InstanceId == retry!.Snapshot.Identity.InstanceId) &&
                death.Drops.Select(row => row.Reservation.GroundKey).Distinct().Count() == 2 && f.Kills == completed.Count + 1,
                "再挑战首个真实Session死亡仍生成原完整物品与金币规则，不回退单件显式策略");
        }

        FormalDungeonReceipt Enter(Fixture f) => f.Session.EnterFormalDungeon(3, 3145, 0, dungeons, rooms)
            ?? throw new InvalidOperationException("FAIL 结算测试无法进入正式3145");

        (FormalDungeonReceipt Receipt, DungeonPlannedMonster Boss, uint Experience, int Count) ClearRun(Fixture f)
        {
            var receipt = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("FAIL 结算测试加载失败");
            DungeonPlannedMonster? boss = null;
            FormalDungeonReceipt? final = null;
            uint experience = 0;
            int count = 0;
            for (int i = 0; i < maps.Count; i++)
            {
                Check(receipt.Snapshot.CurrentMap == maps[i], "结算测试使用批准六房路线及正式加载身份");
                foreach (var monster in receipt.Monsters.Monsters.Where(monster => monster.IsBlocking))
                {
                    if (monster.SpawnType == 3)
                    {
                        boss = monster;
                        Check(f.Session.ConfirmFormalDungeonBoss(f.Created.Id, monster.ObjectKey, (uint)monster.CandidateLevel, 504) is null,
                            "活Boss或未全清房间不接受117协调");
                    }
                    uint expectedGain = monster.CandidateLevel == 5 ? 137u : f.Session.Selected!.Level < 3 ? 104u : 93u;
                    var death = f.Session.ObserveFormalDungeonDeath(new(monster.ObjectKey, f.Created.Id, 0x0111, f.Created.Id,
                        600, 3, 0, monster.SpawnType == 3 ? 6u : (uint)monster.CandidateLevel,
                        monster.SpawnType == 3 ? 0UL : 504UL, 620, 3, true, new string('A', 64)), kills, growth);
                    Check(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null,
                        "合成死亡经正式权威、准备、存储和确认完成");
                    final = death.Receipt!;
                    experience = checked(experience + expectedGain);
                    count++;
                    if (death.ClearFact is not null)
                        Check(i == maps.Count - 1 && death.ClearFact.ConfirmedDeathEvents.Count == count,
                            "唯一清图事实包含所有已提交死亡且位于末房");
                }
                if (i < maps.Count - 1)
                {
                    var target = maps[i + 1].Position;
                    Check(f.Session.MoveFormalDungeon((byte)target.X, (byte)target.Y) is not null,
                        "清房后沿实际批准连接过房");
                    receipt = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("FAIL 结算测试过房加载失败");
                }
            }
            return (final ?? throw new InvalidOperationException("FAIL 未产生死亡回执"),
                boss ?? throw new InvalidOperationException("FAIL 批准路线没有Boss"), experience, count);
        }

        FormalDungeonSettlementResult Settle(Fixture f, byte[] body) =>
            f.Session.SettleFormalDungeon(f.Created.Id, 99, 99, body, dungeons, clear, growth);
    }

    private static bool Same(Character first, Character second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    private static void Check(bool valid, string name) => TestCheck.That(valid, name);

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-clear-session-" + Guid.NewGuid().ToString("N"));
        private readonly string database;
        private readonly CharacterRepository repository;
        private readonly AccountOwners.Lease owner;
        public GameSession Session { get; }
        public string Account => owner.Account;
        public Character Created { get; }
        public long Kills => Count("character_dungeon_growth_receipts");
        public long Settlements => Count("character_dungeon_settlement_receipts");

        public Fixture(string name, TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog learning, DfoServer.GameWorld.Quests.QuestCatalog quests)
        {
            database = Path.Combine(root, "game.db");
            repository = new(database, quests);
            owner = new AccountOwners().Acquire("clear-session", () => { });
            Session = new(repository, towns, owner);
            Created = repository.Create(owner,CharacterCreation.Prepare(name,characters.Get(0),towns.Room,towns.InitialReturn,learning.Get)
                with { ActiveMainQuests = [new(0,3145,1)] });
        }

        public void SelectEntry()
        {
            Session.Select(Created.Slot);
            Session.MarkReady();
            Check(Session.LeaveRoom() && Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                "结算测试角色经合法出房与旅行进入入口");
            Session.BeginDungeonSelection();
        }

        public Character Read() => repository.LoadCharacter(owner, Created.Slot);
        private long Count(string table)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT count(*) FROM " + table;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        public void Sql(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        private SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            return db;
        }
        public void Dispose()
        {
            owner.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
