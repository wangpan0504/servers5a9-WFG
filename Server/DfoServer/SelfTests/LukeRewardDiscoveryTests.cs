using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeRewardDiscoveryTests
{
    internal static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-luke-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); bool passed = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path); var owners = new AccountOwners();
            using var owner = owners.Acquire("discovery-owner", () => { });
            using var stranger = owners.Acquire("discovery-other", () => { });
            repository.Create(owner, new("Discovery", new(1, 0, 10, 10), new(1, 0, 10, 10)));
            repository.Create(stranger, new("OtherDisc", new(1, 0, 10, 10), new(1, 0, 10, 10)));
            var role = repository.LoadCharacter(owner, 0); var other = repository.LoadCharacter(stranger, 0);
            var first = Guid.ParseExact("00000000000000000000000000000001", "N");
            var second = Guid.ParseExact("00000000000000000000000000000002", "N");
            ImmutableArray<RaidRewardOffer> offers = [new(0, 2, [new(100, 3)], []), new(0, 7, [new(101, 5)], []), new(1, 9, [new(102, 8)], [])];
            RaidPhaseEligibility Phase(Guid instance, int phase, ushort character) => new(instance, RaidKind.Luke, phase,
                Guid.NewGuid(), 100 + phase, character, phase == 1, true, "eligible", "saved/synthetic-rule", 20261022, offers);
            var p0 = Phase(first, 0, role.Id); var p1 = Phase(first, 1, role.Id); var next = Phase(second, 0, role.Id);
            // 明确构造存储边界合成记录；不把这些行声称为真实通关或资产发放证明。
            Seed(role, owner.Account, p0, p1); Seed(role, owner.Account, next, null);
            Seed(other, stranger.Account, Phase(first, 0, other.Id), null);
            var chosen = new PreparedRaidReward(first, 0, 0, 7, role.Id, p0.Source, offers[1].Grants, false, p0.Week) { RaidType = RaidKind.Luke };
            Sql("INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type) VALUES($id,$account,$instance,0,0,7,20261022,0,$json,'[41,42]',100,1)",
                ("$id", role.Id), ("$account", owner.Account), ("$instance", first.ToString("N")), ("$json", JsonSerializer.Serialize(chosen)));
            string stable = Facts();
            var page = repository.ListLukeRewardRecovery(owner, role, limit: 1);
            That(page.Entries.Length == 2 && page.NextAfter == first && page.Entries.Select(x => x.Eligibility.Phase).SequenceEqual([0, 1]), "同实例双阶段保留独立身份，不选最新阶段");
            That(JsonSerializer.Serialize(page.Entries[0].Eligibility) == JsonSerializer.Serialize(p0) &&
                page.Entries[0].Claimed is [{ Prepared.Card: 7, MailIds.Length: 2 }] && page.Entries[1].Claimed.IsEmpty && page.Entries[0].AvailableOffers is [{ Kind: 1, Card: 9 }],
                "保存的全部备选牌及已领card原邮件回执精确保留，未领kind不自动选择");
            var tail = repository.ListLukeRewardRecovery(owner, role, page.NextAfter, 1);
            That(tail.Entries.Length == 1 && tail.Entries[0].Eligibility.Instance == second && tail.NextAfter == second &&
                repository.ListLukeRewardRecovery(owner, role, tail.NextAfter, 1).Entries.IsEmpty, "有界keyset跨历史实例无混淆，可有末尾空页");
            var reopened = new CharacterRepository(path);
            That(JsonSerializer.Serialize(reopened.ListLukeRewardRecovery(owner, role, limit: 1)) == JsonSerializer.Serialize(page), "仓储重开不依赖Battle或当前PVF，原event/anchor/offer稳定");
            That(reopened.ListLukeRewardRecovery(stranger, other).Entries.All(x => x.Eligibility.Character == other.Id), "只读本人角色历史");
            Reject<InvalidDataException>(() => reopened.ListLukeRewardRecovery(stranger, role), "其它账号不能借角色快照读取");
            Reject<InvalidDataException>(() => reopened.ListLukeRewardRecovery(owner, role with { Revision = role.Revision + 1 }), "旧角色版本拒绝");
            Reject<InvalidDataException>(() => reopened.ListLukeRewardRecovery(owner, role, Guid.Empty), "空游标拒绝");
            foreach (int limit in new[] { 0, 17 }) Reject<InvalidDataException>(() => reopened.ListLukeRewardRecovery(owner, role, limit: limit), "页大小必须有界");
            bool resourcesCalled = false;
            var replay = LukeRewardRecovery.Claim(owner, role, new(role.Id, first, 0, 0, 7), p0.Event, p0.Anchor,
                reopened, () => { resourcesCalled = true; throw new InvalidOperationException("不得加载资源"); }, DateTimeOffset.UtcNow);
            That(replay.Duplicate && replay.MailIds.SequenceEqual([41u, 42u]) && !resourcesCalled && Facts() == stable,
                "发现后的明确已领目标仅重放原回执，发现及重放均无资产/资格写入");
            string original = JsonSerializer.Serialize(next);
            foreach (var broken in new[] { next with { Event = Guid.NewGuid() }, next with { Offers = [offers[0] with { Grants = [null!] }] } })
            {
                Set(JsonSerializer.Serialize(broken));
                try { Reject<InvalidDataException>(() => reopened.ListLukeRewardRecovery(owner, role, first), "保存身份或嵌套奖励损坏拒绝，不默认补发"); }
                finally { Set(original); }
            }
            // 正常schema已禁止大写/全零；故障注入模拟外部损坏库，读侧不能规范化后漏行。
            foreach (string corrupt in new[] { "0000000000000000000000000000000A", new string('0', 32) })
            {
                using var db = Open(); using var command = db.CreateCommand();
                command.CommandText = "PRAGMA ignore_check_constraints=ON; UPDATE character_raid_runs SET instance_id=$bad WHERE character_id=$id AND instance_id=$instance";
                command.Parameters.AddWithValue("$bad", corrupt); command.Parameters.AddWithValue("$id", role.Id);
                command.Parameters.AddWithValue("$instance", second.ToString("N")); command.ExecuteNonQuery();
                try { Reject<InvalidDataException>(() => reopened.ListLukeRewardRecovery(owner, role), "非规范实例主键拒绝，不生成错误游标或静默漏记录"); }
                finally
                {
                    command.CommandText = "UPDATE character_raid_runs SET instance_id=$instance WHERE character_id=$id AND instance_id=$bad";
                    command.ExecuteNonQuery();
                }
            }
            // WAL中其它writer持有事务时，发现仍是纯读，不申请writer或创建缺省行。
            using (var db = Open()) using (var tx = db.BeginTransaction())
            {
                using var command = db.CreateCommand(); command.Transaction = tx;
                command.CommandText = "UPDATE characters SET revision=revision WHERE id=$id"; command.Parameters.AddWithValue("$id", other.Id); command.ExecuteNonQuery();
                That(reopened.ListLukeRewardRecovery(owner, role).Entries.Length == 3, "其它账号writer期间只读发现可完成");
                tx.Rollback();
            }
            owner.Dispose();
            using var replacement = owners.Acquire("discovery-owner", () => { });
            Reject<InvalidOperationException>(() => reopened.ListLukeRewardRecovery(owner, role), "旧owner不能查询新连接的资格");
            That(reopened.ListLukeRewardRecovery(replacement, role).Entries.Length == 3 && Facts() == stable, "新owner可重开查询，所有读取保持全表内容不变");
            passed = true;
            Console.WriteLine($"LukeRewardDiscoveryTests core/SQLite: {Count - before} passed");

            SqliteConnection Open() { var db = new SqliteConnection($"Data Source={path};Pooling=False"); db.Open(); return db; }
            void Sql(string sql, params (string Key, object Value)[] values)
            { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; foreach (var value in values) command.Parameters.AddWithValue(value.Key, value.Value); command.ExecuteNonQuery(); }
            void Set(string json) => Sql("UPDATE character_raid_runs SET phase0_receipt=$json WHERE character_id=$id AND instance_id=$instance", ("$json", json), ("$id", role.Id), ("$instance", second.ToString("N")));
            void Seed(Character character, string account, RaidPhaseEligibility a, RaidPhaseEligibility? b) => Sql(
                "INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state,phase0_event,phase0_anchor,phase0_receipt,phase1_event,phase1_anchor,phase1_receipt,clear_week) VALUES($id,$account,$instance,1,'synthetic/admission',0,$state,$event,$anchor,$json,$event1,$anchor1,$json1,$week)",
                ("$id", character.Id), ("$account", account), ("$instance", a.Instance.ToString("N")), ("$state", b is null ? 1 : 2),
                ("$event", a.Event.ToString("N")), ("$anchor", a.Anchor), ("$json", JsonSerializer.Serialize(a)),
                ("$event1", (object?)b?.Event.ToString("N") ?? DBNull.Value), ("$anchor1", (object?)b?.Anchor ?? DBNull.Value),
                ("$json1", b is null ? DBNull.Value : JsonSerializer.Serialize(b)), ("$week", (object?)b?.Week ?? DBNull.Value));
            string Facts()
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
                var names = new List<string>(); using (var rows = command.ExecuteReader()) while (rows.Read()) names.Add(rows.GetString(0));
                var result = new List<string>();
                foreach (var name in names)
                {
                    command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\"";
                    using var rows = command.ExecuteReader(); var data = new List<string>();
                    while (rows.Read()) { var values = new object[rows.FieldCount]; rows.GetValues(values); data.Add(JsonSerializer.Serialize(values)); }
                    data.Sort(StringComparer.Ordinal); result.Add(name + JsonSerializer.Serialize(data));
                }
                return string.Join("\n", result);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (passed) Directory.Delete(root, true); }
    }
}
