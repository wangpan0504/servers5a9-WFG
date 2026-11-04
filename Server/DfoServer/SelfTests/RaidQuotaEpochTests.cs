using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 真实SQLite额度归属：刷新只改变指定轴，不改历史资格和已发资产。
internal static class RaidQuotaEpochTests
{
    internal static MailboxPersistenceTests.Fixture CreateFixture()
    {
        var fixture = new MailboxPersistenceTests.Fixture();
        fixture.Execute("INSERT INTO accounts(account_id,salt,password_hash,iterations) VALUES('other',x'01',x'02',1),('receiver',x'01',x'02',1),('sender',x'01',x'02',1);");
        return fixture;
    }
    public static void RunCore()
    {
        int before = Count;
        foreach (int types in new[] { 0, 1, 4 })
            foreach (bool reward in new[] { false, true }) ResetAxes(types, reward);
        GuideHistory();
        PendingRewards();
        ResetRollback();
        Migration();
        Console.WriteLine($"PASS RaidQuotaEpochTests ({Count - before} checks)");
    }

    static void ResetAxes(int types, bool reward)
    {
        using var f = RaidQuotaEpochTests.CreateFixture();
        var now = DateTimeOffset.UtcNow;
        byte[] used = types switch { 0 => [], 1 => [2], _ => [0, 1, 2, 3] };
        foreach (byte type in used)
        {
            SeedRun(f, type, Instance(10 + type), complete: true, now: now);
            SeedReceipt(f, type, Instance(10 + type), RaidClearCounts.Week(type, now));
        }
        // 另一账号有真实消耗，也不能被本账号刷新推进。
        f.Execute($"""
            INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state)
            VALUES({f.Receiver.Id},'receiver','eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',2,'quota-other-account',1,1);
            """);
        string history = History(f), assets = Assets(f);
        var countsBefore = f.Repository.ReadRaidClearCounts(f.OtherOwner, f.Other.Id, RaidClearCounts.Periods(now));
        That(countsBefore.Sum(row => row.WeeklyUsed) == types, "四类团本旧世代真实通关读数与已用类型数相等");
        int advanced = reward ? f.Repository.ResetRewardLimits(f.OtherOwner).RaidEpochsAdvanced :
            f.Repository.ResetDungeonEntries(f.OtherOwner).RaidEpochsAdvanced;
        That(advanced == types && f.Query("SELECT count(*) FROM account_raid_quota_epochs WHERE account='other'") is long rows && rows == types,
            "空账号不造世代行，一类及四类仅按实际使用类型推进");
        foreach (byte type in Enumerable.Range(0, 4).Select(value => (byte)value))
        {
            var epoch = ReadEpoch(f, type);
            bool changed = used.Contains(type);
            That(epoch.Entry == (changed && !reward ? 1 : 0) && epoch.Reward == (changed && reward ? 1 : 0),
                "入场和奖励世代相互独立，未用类型保持默认零");
        }
        var countsAfter = f.Repository.ReadRaidClearCounts(f.OtherOwner, f.Other.Id, RaidClearCounts.Periods(now));
        That(countsAfter.Sum(row => row.WeeklyUsed) == (reward ? types : 0),
            "奖励刷新保留原入场通关读数，入场刷新才排除旧世代");
        That(History(f) == history && Assets(f) == assets &&
            f.Query("SELECT count(*) FROM account_raid_quota_epochs WHERE account='receiver'") is 0L,
            "刷新保留完整历史JSON与时间、所有角色资产和另一账号额度");
        string stable = f.Snapshot();
        if (reward) f.Repository.ResetRewardLimits(f.OtherOwner); else f.Repository.ResetDungeonEntries(f.OtherOwner);
        That(f.Snapshot() == stable, "没有新当前世代事实时重复刷新全库快照不变");
        f.Integrity();
    }

    static void GuideHistory()
    {
        using var f = RaidQuotaEpochTests.CreateFixture();
        int week = FiendwarRewards.Cycle.Key(DateTimeOffset.UtcNow), previous = week - 7;
        f.Execute($"""
            INSERT INTO account_fiendwar_guides(account,slot,week,entry_epoch,character_id)
            VALUES('other',0,{previous},0,{f.Other.Id}),('other',0,{week},0,{f.Other.Id}),('other',1,{week},0,65535);
            """);
        string history = Rows(f, "SELECT * FROM account_fiendwar_guides ORDER BY week,entry_epoch,slot");
        var reset = f.Repository.ResetDungeonEntries(f.OtherOwner);
        That(reset.RaidEpochsAdvanced == 1 && reset.RaidGuideBindingsArchived == 2 && ReadEpoch(f, 2).Entry == 1,
            "只当前漩涡周和当前世代两槽触发推进，旧周不占新额度");
        That(Rows(f, "SELECT * FROM account_fiendwar_guides ORDER BY week,entry_epoch,slot") == history,
            "刷新不删除旧周或旧世代绑定，包括已删角色的历史槽");
        string stable = f.Snapshot();
        f.Repository.ResetDungeonEntries(f.OtherOwner);
        That(f.Snapshot() == stable, "只有旧世代绑定时重复刷新不会继续推进");
        f.Execute($"""
            INSERT INTO account_fiendwar_guides(account,slot,week,entry_epoch,character_id)
            VALUES('other',0,{week},1,{f.Other.Id}),('other',1,{week},1,65535);
            """);
        Reject<SqliteException>(() => f.Execute($"INSERT INTO account_fiendwar_guides VALUES('other',1,{week},1,65534)"),
            "同一周同一世代仍只能使用原两槽");
        Reject<SqliteException>(() => f.Execute($"INSERT INTO account_fiendwar_guides VALUES('missing',0,{week},0,65535)"),
            "新世代引导表仍要求真实账号外键");
        That(f.Query("SELECT count(*) FROM account_fiendwar_guides") is 5L, "两代两槽与旧周历史独立共存");
        f.Repository.ResetRewardLimits(f.OtherOwner);
        That(ReadEpoch(f, 2) == new CharacterRepository.RaidQuotaEpochs(1, 0), "仅引导入场绑定不会凭空推进奖励轴");
        f.Repository.ResetDungeonEntries(f.OtherOwner);
        That(ReadEpoch(f, 2).Entry == 2 && f.Query("SELECT count(*) FROM account_fiendwar_guides") is 5L,
            "新世代真实使用后可再次恢复两槽，历史仍完整");
        f.Integrity();
    }

    static void PendingRewards()
    {
        using var f = RaidQuotaEpochTests.CreateFixture();
        var now = DateTimeOffset.UtcNow;
        int week = RaidClearCounts.Week(0, now);
        Guid[] pending = [Instance(101), Instance(102), Instance(103)];
        foreach (var instance in pending) SeedRun(f, 0, instance, complete: false, now: now);
        string frozen = Rows(f, "SELECT * FROM character_raid_runs ORDER BY instance_id");
        f.Repository.ResetRewardLimits(f.OtherOwner);
        That(ReadEpoch(f, 0) == new CharacterRepository.RaidQuotaEpochs(0, 1), "未领奖旧run也保留独立归属并预留旧奖励额度");
        Character character = f.Repository.LoadCharacter(f.OtherOwner, 0);
        PreparedRaidReward Prepared(Guid instance) => new(instance, 1, 1, 0, character.Id,
            "quota-legacy-anton-fixture", [new InventoryReward(0, 17)], true, week);
        RaidRewardCommit? Claim(PreparedRaidReward prepared) => f.Repository.CommitRaidReward(f.OtherOwner, character,
            prepared, new(character.Inventory, character.Inventory with { Gold = checked(character.Inventory.Gold + 17) }, "raid_reward", new(character)), now);
        foreach (int id in new[] { 201, 202 })
        {
            var committed = Claim(Prepared(Instance(id)));
            That(committed is { Duplicate: false }, "新世代旧安徒恩兼容领取按当前额度入账");
            character = committed!.Character;
        }
        That(Claim(Prepared(Instance(203))) is null, "当前新世代两次最终奖励仍受原周上限限制");
        var oldPrepared = Prepared(pending[0]);
        var old = Claim(oldPrepared);
        That(old is { Duplicate: false } && old.Character.Inventory.Gold == 51 &&
            f.Query($"SELECT reward_epoch FROM character_raid_reward_receipts WHERE instance_id='{pending[0]:N}'") is 0L,
            "新世代已满仍可精确补领旧run权益，资产只发一次且回执归原零世代");
        character = old!.Character;
        string saved = f.Snapshot();
        That(Claim(oldPrepared) is { Duplicate: true } && f.Snapshot() == saved, "旧奖励回放先命中原主键，不重复发金币或消耗新额度");
        Reject<InvalidDataException>(() => Claim(oldPrepared with { Card = 1 }), "刷新后仍拒绝同回执更换牌面");
        f.Repository.ResetRewardLimits(f.OtherOwner);
        That(ReadEpoch(f, 0).Reward == 2, "新世代已领取事实支持第二轮刷新");
        var second = Claim(Prepared(pending[1]));
        That(second is { Duplicate: false }, "第二笔旧pending仍回原世代剩余的一次权益");
        character = second!.Character;
        That(Claim(Prepared(pending[2])) is null && character.Inventory.Gold == 68,
            "原零世代用满后不能借当前空世代突破旧pending上限");
        That(Rows(f, "SELECT * FROM character_raid_runs ORDER BY instance_id") == frozen &&
            f.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE reward_epoch=0") is 2L &&
            f.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE reward_epoch=1") is 2L &&
            f.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE reward_epoch=2") is 0L,
            "两轮刷新和迟到补领奖励分别留原归属，冻结run原文不被重签");
        saved = f.Snapshot();
        f.Repository.ResetRewardLimits(f.OtherOwner);
        That(f.Snapshot() == saved, "旧pending迟到写旧回执不触发空新世代再次推进");
        f.Integrity();
    }

    static void ResetRollback()
    {
        using var f = RaidQuotaEpochTests.CreateFixture();
        foreach (byte type in new byte[] { 0, 1, 2, 3 }) SeedRun(f, type, Instance(300 + type), complete: false);
        f.Execute("CREATE TRIGGER reject_last_quota BEFORE UPDATE ON account_raid_quota_epochs WHEN NEW.raid_type=3 BEGIN SELECT RAISE(ABORT,'quota-late-failure'); END;");
        string saved = f.Snapshot();
        Reject<SqliteException>(() => f.Repository.ResetDungeonEntries(f.OtherOwner), "第四类入场额度失败拒绝整笔刷新");
        That(f.Snapshot() == saved, "后类型失败回滚前面三类推进和新增账号额度行");
        Reject<SqliteException>(() => f.Repository.ResetRewardLimits(f.OtherOwner), "第四类奖励额度失败拒绝整笔刷新");
        That(f.Snapshot() == saved, "奖励轴晚期失败回滚全部角色、资产、回执与双轴状态");
        f.Execute("DROP TRIGGER reject_last_quota;");
        Reject<SqliteException>(() => f.Execute("INSERT INTO account_raid_quota_epochs VALUES('other',0,-1,0)"), "负世代不能入库");
        Reject<SqliteException>(() => f.Execute("INSERT INTO account_raid_quota_epochs VALUES('other',0,0.5,0)"), "世代不能漂移成REAL");
        f.Execute($"INSERT INTO account_raid_quota_epochs VALUES('other',0,{long.MaxValue},0); UPDATE character_raid_runs SET entry_epoch={long.MaxValue} WHERE raid_type=0;");
        saved = f.Snapshot();
        Reject<OverflowException>(() => f.Repository.ResetDungeonEntries(f.OtherOwner), "世代溢出明确拒绝而非SQLite隐式浮点递增");
        That(f.Snapshot() == saved, "版本溢出不改变任何额度或历史");
        f.Integrity();
    }

    static void Migration()
    {
        foreach (var (conflict, endlessPreview) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            using var f = RaidQuotaEpochTests.CreateFixture();
            SeedRun(f, 1, Instance(401), complete: true);
            SeedReceipt(f, 1, Instance(401), 20261029);
            f.Execute($"INSERT INTO account_fiendwar_guides VALUES('other',0,20261027,0,{f.Other.Id});");
            // 从完整191形态升级：仅退回本次新增结构，不动历史行或其它迁移。
            f.Execute("""
                DROP INDEX character_raid_entry_clear;
                DROP INDEX character_raid_reward_epoch;
                ALTER TABLE character_raid_runs DROP COLUMN entry_epoch;
                ALTER TABLE character_raid_runs DROP COLUMN reward_epoch;
                ALTER TABLE character_raid_reward_receipts DROP COLUMN reward_epoch;
                ALTER TABLE account_fiendwar_guides RENAME TO quota_new_guides;
                """ + RaidLifecycleSchema.FiendwarGuideBindings + """
                INSERT INTO account_fiendwar_guides(account,slot,week,character_id)
                  SELECT account,slot,week,character_id FROM quota_new_guides;
                DROP TABLE quota_new_guides;
                DROP TABLE account_raid_quota_epochs;
                PRAGMA user_version=191;
                """);
            if (endlessPreview)
                f.Execute("PRAGMA user_version=192; INSERT INTO account_endless_adventure_days VALUES('other',739923,147);");
            string assets = Assets(f);
            string history = Rows(f, "SELECT * FROM character_raid_runs") + Rows(f, "SELECT * FROM character_raid_reward_receipts");
            if (conflict)
            {
                f.Execute("CREATE TABLE account_raid_quota_epochs(collision TEXT);");
                string saved = f.Snapshot(), schema = Rows(f, "SELECT type,name,sql FROM sqlite_schema ORDER BY type,name");
                Reject<InvalidDataException>(() => new CharacterRepository(f.Path), "迁移尾部未知额度表结构必须拒绝");
                That(f.Query("PRAGMA user_version") is long rejectedVersion && rejectedVersion == (endlessPreview ? 192 : 191) && f.Snapshot() == saved &&
                    Rows(f, "SELECT type,name,sql FROM sqlite_schema ORDER BY type,name") == schema,
                    "192迁移失败整笔回滚列、引导表重建、索引和版本");
                continue;
            }
            _ = new CharacterRepository(f.Path);
            That(f.Query("PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                f.Query("SELECT entry_epoch+reward_epoch FROM character_raid_runs") is 0L &&
                f.Query("SELECT reward_epoch FROM character_raid_reward_receipts") is 0L &&
                f.Query("SELECT entry_epoch FROM account_fiendwar_guides") is 0L &&
                f.Query("SELECT count(*) FROM account_raid_quota_epochs") is 0L,
                "191原run、奖励及引导默认归零世代，不补造全账号额度记录");
            if (endlessPreview)
                That(f.Query("SELECT earned FROM account_endless_adventure_days WHERE account='other'") is 147L,
                    "旧活动192库补齐主线迁移仍保留真实147枚日额度，不重置或重复发币");
            That(Assets(f) == assets &&
                Rows(f, "SELECT character_id,account,instance_id,raid_type,source,admitted_at,state,phase0_event,phase0_anchor,phase0_receipt,phase1_event,phase1_anchor,phase1_receipt,clear_week,guild_id,guild_joined_at FROM character_raid_runs") +
                Rows(f, "SELECT character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type FROM character_raid_reward_receipts") == history,
                "迁移保留原生命周期身份、完整阶段JSON、邮件键、奖励历史和所有资产");
            string current = f.Snapshot();
            _ = new CharacterRepository(f.Path);
            That(f.Snapshot() == current, "192重复打开不重复迁移、补领或推进世代");
            f.Integrity();
        }
    }

    static Guid Instance(int value) => Guid.ParseExact(value.ToString("x32"), "N");
    static CharacterRepository.RaidQuotaEpochs ReadEpoch(MailboxPersistenceTests.Fixture f, byte type)
    {
        using var db = f.Open();
        return CharacterRepository.ReadRaidQuotaEpochs(db, null, "other", (RaidKind)type);
    }
    static void SeedRun(MailboxPersistenceTests.Fixture f, byte type, Guid instance, bool complete, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        int week = RaidClearCounts.Week(type, at);
        string phase = type == 2 ? "phase0" : "phase1";
        string receipt = JsonSerializer.Serialize(new { frozen = "original", Eligible = true, Week = week });
        f.Execute($"""
            INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state,
              {phase}_event,{phase}_anchor,{phase}_receipt,clear_week)
            VALUES({f.Other.Id},'other','{instance:N}',{type},'quota-history',{at.ToUnixTimeSeconds()},{(complete ? 2 : 1)},
              '{instance:N}',{at.ToUnixTimeSeconds()},'{receipt}',{(complete ? week.ToString() : "NULL")});
            """);
    }
    static void SeedReceipt(MailboxPersistenceTests.Fixture f, byte type, Guid instance, int week) => f.Execute($"""
        INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type)
        VALUES({f.Other.Id},'other','{instance:N}',1,1,0,{week},1,json_object('original','reward'),'[731,732]',1,{type});
        """);
    static string History(MailboxPersistenceTests.Fixture f) => Rows(f, "SELECT * FROM character_raid_runs ORDER BY character_id,instance_id") +
        Rows(f, "SELECT * FROM character_raid_reward_receipts ORDER BY character_id,instance_id,phase,kind") +
        Rows(f, "SELECT * FROM account_fiendwar_guides ORDER BY account,week,entry_epoch,slot");
    static string Assets(MailboxPersistenceTests.Fixture f) => Rows(f, "SELECT * FROM characters ORDER BY id") +
        Rows(f, "SELECT * FROM character_inventory_items ORDER BY item_uid") + Rows(f, "SELECT * FROM mailbox_messages ORDER BY mail_id");
    static string Rows(MailboxPersistenceTests.Fixture f, string sql)
    {
        using var db = f.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<object[]>();
        while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray());
        return JsonSerializer.Serialize(rows);
    }
    static void Reject<T>(Action action, string message) where T : Exception
    {
        bool rejected = false;
        try { action(); } catch (T) { rejected = true; }
        That(rejected, message);
    }
}
