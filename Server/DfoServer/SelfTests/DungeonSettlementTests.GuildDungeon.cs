using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Guild;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    public static void RunGuildDungeonStorage(PvfArchive pvf)
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "guild-dungeon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var catalog = new DfoServer.GameWorld.Guilds.GuildDungeonCatalog(pvf);
            var items = new DfoServer.GameWorld.Items.ItemCatalog(pvf, new Dictionary<int, string>(),
                PvfScript.List(pvf.ReadText("stackable/stackable.lst")), 0);
            var rewardPlan = GuildDungeonClearReward.Prepare(catalog.Find(8500)!, items, DateTimeOffset.UtcNow, catalog);
            That(rewardPlan.Select(0).Choice.Core is { ItemId: 490011649, Count: 1 } &&
                rewardPlan.Select(99).Choice.Definition.Ratio == 99 && rewardPlan.Select(100).Choice.Definition.Ratio == 100 &&
                rewardPlan.Select(100).Choice.Definition.Contribution == 20,
                "正式礼袋完整核心、99/100比例上界和奖励贡献20独立读取，不误作公会币");
            var growth = Testing.SelfTestCatalogs.Growth(pvf);
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("guild-dungeon", static () => { });
            var initial = repository.Create(owner, Fixture("GuildDungeon"));
            void Sql(string sql) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            long Number(string sql) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
            SqliteConnection Open() { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); return db; }
            That(repository.PrepareGuildDungeonEntry(owner, initial.Id, 8500, catalog) is null,
                "无会籍不能进入公会副本，查询不产生进度");
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(1,'净化','purification',{initial.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{initial.Id},1,1000);
                """);
            var admission = repository.PrepareGuildDungeonEntry(owner, initial.Id, 8500, catalog)!;
            That(admission is not null && repository.PrepareGuildDungeonEntry(owner, initial.Id, 8504, catalog) is null,
                "根图开放且前置净化不足拒绝后续图");
            var (entryRun, _) = CreateRun(initial, row => row with { DungeonId = 8500 });
            var identity = entryRun.Snapshot.Identity with { TaskAttempt = initial.DungeonState!.LastAttempt + 1 };
            var charge = new DungeonEntryCharge(null, 0, identity, entryRun.Snapshot.CurrentMap.Id, null)
                { DungeonId = 8500, GuildDungeon = admission };
            Sql($"UPDATE guild_members SET joined_at_unix=1001 WHERE character_id={initial.Id}");
            Reject<GuildDungeonAdmissionRejectedException>(() => repository.CommitDungeonEntry(owner, initial, initial.DungeonState.NextObjectKey, charge),
                "零费用公会入场在预留事务内复验会籍代次");
            That(repository.LoadCharacter(owner, initial.Slot)!.Revision == initial.Revision,
                "会籍冲突不预留尝试号、不更改角色版本");
            Sql($"UPDATE guild_members SET joined_at_unix=1000 WHERE character_id={initial.Id}");
            var entered = repository.CommitDungeonEntry(owner, initial, initial.DungeonState.NextObjectKey, charge);
            That(entered.GuildMembership == admission!.Membership && entered.Character.DungeonState!.LastAttempt == identity.TaskAttempt,
                "无费用公会准入与真实对象预留、会籍冻结同事务");

            (SinglePlayerDungeonRun Run, PreparedDungeonSettlement Pending) Prepare(Character character, Guid? instance = null)
            {
                var (run, authority) = CreateRun(character, row => row with { DungeonId = 8500 }, instance);
                run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
                var pending = run.PrepareSettlement(run.ClearFact!, [1, 2, 3], Reward).Pending!;
                pending.FreezeGuildContribution([new(character.Id, 1, 1000)], GuildContributionSource.GuildDungeon, catalog, rewardPlan);
                return (run, pending);
            }
            var first = Prepare(entered.Character);
            Sql("CREATE TRIGGER guild_progress_fail AFTER INSERT ON guild_dungeon_clear_receipts BEGIN SELECT RAISE(ABORT,'rollback'); END");
            Reject<SqliteException>(() => repository.CommitDungeonSettlement(owner, entered.Character, first.Run, first.Pending, growth),
                "通关末端公会净化回执故障真实注入");
            That(Number("SELECT count(*) FROM guild_dungeon_progress") == 0 &&
                Number("SELECT count(*) FROM character_dungeon_settlement_receipts") == 0 &&
                Number("SELECT count(*) FROM guild_dungeon_reward_receipts") == 0 &&
                Number("SELECT count(*) FROM mailbox_messages") == 0 &&
                repository.LoadCharacter(owner, initial.Slot)!.Revision == entered.Character.Revision,
                "净化故障使角色经验、贡献、进度及原结算回执一起回滚");
            Sql("DROP TRIGGER guild_progress_fail");
            var committed = repository.CommitDungeonSettlement(owner, entered.Character, first.Run, first.Pending, growth);
            That(committed.MailId is > 0 && committed.GuildContribution?.Points == 80 &&
                Number("SELECT json_extract(reward_json,'$.Ratio') FROM guild_dungeon_reward_receipts") == 0 &&
                Number("SELECT count(*) FROM mailbox_messages WHERE expires_at IS NULL") == 1,
                "通关前0净化发一份永久邮件礼袋，自然贡献60与资源奖励20相加，不重复乘人数");
            That(committed.GuildDungeon is { GuildId: 1, Counts: [{ DungeonId: 8500, Clears: 1 }] } &&
                Number("SELECT count(*) FROM guild_dungeon_clear_receipts") == 1,
                "真实清图提交一次通关次数，不把净化百分比写进计数列");
            var reopened = new CharacterRepository(path);
            var replay = reopened.CommitDungeonSettlement(owner, entered.Character, first.Run, first.Pending, growth);
            That(replay.Duplicate && replay.GuildDungeon?.Counts[0].Clears == 1 &&
                Number("SELECT clear_count FROM guild_dungeon_progress") == 1 && replay.MailId == committed.MailId &&
                Number("SELECT count(*) FROM guild_dungeon_reward_receipts") == 1 && Number("SELECT count(*) FROM mailbox_messages") == 1,
                "重开仓储恢复计数与原回执，不重复增加净化");
            Sql("UPDATE guild_dungeon_reward_receipts SET reward_json=json_remove(reward_json,'$.Version','$.RepeatCore')");
            That(reopened.CommitDungeonSettlement(owner, entered.Character, first.Run, first.Pending, growth).MailId == committed.MailId,
                "旧v1基础奖回执兼容重放，升级不补发重复奖或替换原邮件");
            var peer = repository.Create(owner, Fixture("GuildPeer"));
            Sql($"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{peer.Id},4,1000)");
            GuildDungeonMembership[] buffMembers = [new(initial.Id, 1, 1000), new(peer.Id, 1, 1000)];
            That(repository.ReadGuildDungeonPartyCount(owner, initial.Id, buffMembers, [initial.Id, peer.Id]) == 2 &&
                repository.ReadGuildDungeonPartyCount(owner, initial.Id, buffMembers, [initial.Id]) == 0,
                "组队增益只计原入场名册中仍在当前实例的同会成员，离队者不能保留两人增益");
            Sql($"UPDATE guild_members SET joined_at_unix=1001 WHERE character_id={peer.Id}");
            That(repository.ReadGuildDungeonPartyCount(owner, initial.Id, buffMembers, [initial.Id, peer.Id]) == 1 &&
                repository.ReadGuildDungeonPartyCount(owner, peer.Id, buffMembers, [initial.Id, peer.Id]) == 0,
                "同一角色退会重入不能沿用原会籍增益，当前成员也不能借其凑人数");
            Sql($"UPDATE guild_members SET joined_at_unix=1000 WHERE character_id={peer.Id}");
            var peerClear = Prepare(peer, first.Run.Snapshot.Identity.InstanceId);
            var peerCommitted = repository.CommitDungeonSettlement(owner, peer, peerClear.Run, peerClear.Pending, growth);
            That(peerCommitted.GuildDungeon?.Counts[0].Clears == 1 &&
                Number("SELECT count(*) FROM guild_dungeon_clear_receipts") == 1 &&
                Number("SELECT count(*) FROM character_dungeon_settlement_receipts") == 2,
                "同会两成员同一真实实例各自结算资产，公会净化仅计一次");
            That(peerCommitted.MailId is > 0 && peerCommitted.MailId != committed.MailId &&
                Number("SELECT count(*) FROM guild_dungeon_reward_receipts WHERE json_extract(reward_json,'$.Ratio')=0") == 2,
                "同会同实例每个成员各有奖励，两人采用相同累计前净化比例");
            Sql($"UPDATE guild_members SET joined_at_unix=1001 WHERE character_id={initial.Id}");
            That(reopened.CommitDungeonSettlement(owner, entered.Character, first.Run, first.Pending, growth).GuildDungeon is null,
                "退会重入不把旧清图回执发布给新会籍");
            var next = Prepare(committed.Character);
            var excluded = reopened.CommitDungeonSettlement(owner, committed.Character, next.Run, next.Pending, growth);
            That(excluded.GuildDungeon is null && excluded.MailId is null && Number("SELECT clear_count FROM guild_dungeon_progress") == 1 &&
                Number("SELECT count(*) FROM guild_dungeon_reward_receipts") == 2,
                "通关前会籍改变时保留正常结算但不累计旧会净化");
            Sql($"UPDATE guild_members SET joined_at_unix=1000 WHERE character_id={initial.Id}");
            Sql("UPDATE guild_dungeon_progress SET clear_count=34; UPDATE guild_dungeon_effective_progress SET effective_clear_units=34");
            That(reopened.PrepareGuildDungeonEntry(owner, initial.Id, 8504, catalog) is not null,
                "后续准入实时读取已持久的前置次数并采用客户端净化阈值");
            using var otherOwner = owners.Acquire("guild-dungeon-party", static () => { });
            var other = repository.Create(otherOwner, Fixture("GuildParty"));
            Sql($"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{other.Id},4,1000)");
            var current = excluded.Character;
            var partyInstance = Guid.NewGuid();
            DungeonEntryCharge PartyCharge(AccountOwners.Lease lease, Character member) => charge with
            {
                Identity = identity with { CharacterId = member.Id, InstanceId = partyInstance,
                    TaskAttempt = member.DungeonState!.LastAttempt + 1 },
                GuildDungeon = repository.PrepareGuildDungeonEntry(lease, member.Id, 8500, catalog)
            };
            var partyCharges = new PartyDungeonReservation[]
            {
                new(owner, current, current.DungeonState!.NextObjectKey, PartyCharge(owner, current)),
                new(otherOwner, other, other.DungeonState!.NextObjectKey, PartyCharge(otherOwner, other))
            };
            Sql($"DELETE FROM guild_members WHERE character_id={other.Id}");
            Reject<GuildDungeonAdmissionRejectedException>(() => repository.CommitPartyDungeonEntry(partyCharges),
                "组队准备后第二成员被踢时以专门业务拒绝回滚");
            That(repository.LoadCharacter(owner, current.Slot)!.Revision == current.Revision &&
                repository.LoadCharacter(otherOwner, other.Slot)!.Revision == other.Revision,
                "后续成员公会准入失效不留下首成员已预留的尝试或资产变动");
            Sql($"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{other.Id},4,1000)");
            (SinglePlayerDungeonRun Run, PreparedDungeonSettlement Pending) RankingClear(Character member, Guid instance)
            {
                var (run, authority) = CreateRun(member, row => row with { DungeonId = 8500 }, instance);
                run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
                var pending = run.PrepareSettlement(run.ClearFact!, [1, 2, 3], Reward).Pending!;
                pending.FreezeGuildContribution([new(current.Id, 1, 1000), new(other.Id, 1, 1000)], GuildContributionSource.GuildDungeon, catalog, rewardPlan);
                return (run, pending);
            }
            var dungeonDefinition = catalog.Find(8500)!;
            int fullClears = checked((dungeonDefinition.GaugeLimit + dungeonDefinition.GaugeOnClear - 1) / dungeonDefinition.GaugeOnClear);
            Sql($"UPDATE guild_dungeon_progress SET clear_count={fullClears}; UPDATE guild_dungeon_effective_progress SET effective_clear_units={fullClears}");
            That(dungeonDefinition.Purification((uint)fullClears) == 100, "满净化夹具按正式上限与单次增量准备，不把解锁次数当成100%");
            var rankingRun = RankingClear(current, Guid.NewGuid());
            Sql("CREATE TRIGGER ranking_abort AFTER INSERT ON guild_ranking_party_clears BEGIN SELECT RAISE(ABORT,'ranking rollback'); END");
            Reject<SqliteException>(() => repository.CommitDungeonSettlement(owner, current, rankingRun.Run, rankingRun.Pending, growth),
                "组队排行记账故障参与真实结算外层事务");
            That(Number("SELECT count(*) FROM guild_ranking_party_clears") == 0 &&
                Number("SELECT count(*) FROM guild_dungeon_reward_receipts") == 2 &&
                repository.LoadCharacter(owner, current.Slot)!.Revision == current.Revision,
                "排行末段失败不留下经验、净化或首个排行事实");
            Sql("DROP TRIGGER ranking_abort");
            var ranked = repository.CommitDungeonSettlement(owner, current, rankingRun.Run, rankingRun.Pending, growth);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), week = catalog.InactiveDays * 86400L;
            Sql($"UPDATE guild_dungeon_effective_progress SET decay_anchor_unix={now - week}");
            var rankingPeer = RankingClear(other, rankingRun.Run.Snapshot.Identity.InstanceId);
            _ = repository.CommitDungeonSettlement(otherOwner, other, rankingPeer.Run, rankingPeer.Pending, growth);
            _ = new CharacterRepository(path).CommitDungeonSettlement(owner, current, rankingRun.Run, rankingRun.Pending, growth);
            That(Number("SELECT count(*) FROM guild_ranking_party_clears") == 1 &&
                Number("SELECT members FROM guild_ranking_party_clears") == 2,
                "同会不同账号真实通关共享实例只累计一条排行，第二人及重开重放不重复记分");
            That(Number("SELECT count(*) FROM guild_dungeon_reward_receipts WHERE json_extract(reward_json,'$.Ratio')=100") == 2 &&
                Number("SELECT count(*) FROM mailbox_messages") == 4,
                "满100净化后真实通关仍各发一份礼袋，第二人及重开重报不重复发奖");
            That(Number("SELECT count(*) FROM guild_dungeon_reward_receipts WHERE json_extract(reward_json,'$.RepeatCore.ItemId')=10156320") == 2 &&
                Number("SELECT decay_anchor_unix FROM guild_dungeon_effective_progress") == now - week,
                "满净化两名成员各有额外礼盒；迟到成员共享原100%且不重置闲置锚点，重放不重复发奖");
            That(Number("""
                SELECT count(*) FROM mailbox_attachments a JOIN character_inventory_items i USING(item_uid)
                WHERE json_extract(i.item_core,'$.ItemId')=10156320 AND json_extract(i.item_core,'$.Value')=1
                """) == 2 && Number("SELECT count(*) FROM mailbox_attachments") == 6,
                "重复礼盒确实成为两名成员各自永久邮件的第二件真实物品，并非仅记录奖励JSON");
            Sql($"UPDATE guild_dungeon_effective_progress SET effective_clear_units={fullClears - 1},decay_anchor_unix={now}");
            var crossing = Prepare(ranked.Character);
            var crossed = repository.CommitDungeonSettlement(owner, ranked.Character, crossing.Run, crossing.Pending, growth);
            That(Number("SELECT effective_clear_units FROM guild_dungeon_effective_progress") == fullClears &&
                Number("SELECT count(*) FROM guild_dungeon_reward_receipts WHERE json_extract(reward_json,'$.RepeatCore.ItemId')=10156320") == 2,
                "首次达到100%的通关只发基础奖，不提前发重复奖");
            Sql($"UPDATE guild_dungeon_effective_progress SET effective_clear_units=34,decay_anchor_unix={now - week}");
            That(repository.PrepareGuildDungeonEntry(owner, initial.Id, 8504, catalog) is null,
                "历史累计仍满值时，闲置衰减后的有效净化不足会重新关闭后续图");
            var idleProgress = crossed.GuildDungeon! with { Counts = [new(8500, (uint)fullClears, 34, now - week)] };
            That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                Network.Builders.GuildDungeonPackets.Progress(catalog, idleProgress).AsSpan(21)) == 30,
                "744同准入使用有效次数30，不把历史满值或百分比发送给原生消费者");
            long historical = Number("SELECT clear_count FROM guild_dungeon_progress");
            var afterIdle = Prepare(crossed.Character);
            var resumed = repository.CommitDungeonSettlement(owner, crossed.Character, afterIdle.Run, afterIdle.Pending, growth);
            That(Number("SELECT effective_clear_units FROM guild_dungeon_effective_progress") == 31 &&
                Number("SELECT clear_count FROM guild_dungeon_progress") == historical + 1 &&
                Number("SELECT decay_anchor_unix FROM guild_dungeon_effective_progress") >= now,
                "新真实实例先物化衰减再加一次并重置周期，历史只增，奖励和进度仍同事务");
            using (var rewind = Open()) DatabaseSchemaTests.RemovePcRoomSchema(rewind);
            Sql("DROP TABLE guild_dungeon_effective_progress; PRAGMA user_version=142");
            _ = new CharacterRepository(path);
            That(Number("SELECT effective_clear_units FROM guild_dungeon_effective_progress") == historical + 1 &&
                Number("SELECT decay_anchor_unix FROM guild_dungeon_effective_progress") >= now &&
                Number("PRAGMA user_version") == SqliteMigrations.CurrentVersion,
                "真实142旧库升级复制历史并从启用时开始计时，不按旧last_clear追溯衰减，领域另夹资源上限");
            using (var rewind = Open()) DatabaseSchemaTests.RemovePcRoomSchema(rewind);
            Sql("DELETE FROM guild_dungeon_effective_progress; CREATE TRIGGER effective_upgrade_abort BEFORE INSERT ON guild_dungeon_effective_progress BEGIN SELECT RAISE(ABORT,'effective upgrade rollback'); END; PRAGMA user_version=142");
            Reject<SqliteException>(() => new CharacterRepository(path), "有效净化升级迁移故障注入");
            That(Number("PRAGMA user_version") == 142 && Number("SELECT count(*) FROM guild_dungeon_effective_progress") == 0 &&
                Number("SELECT clear_count FROM guild_dungeon_progress") == historical + 1,
                "升级故障不前移版本、不改历史通关或留下半份有效状态");
            Sql("DROP TRIGGER effective_upgrade_abort");
            _ = new CharacterRepository(path);
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS guild dungeon transaction assertions {Count - before}");
    }
}
