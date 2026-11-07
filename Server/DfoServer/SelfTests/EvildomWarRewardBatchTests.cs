using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 只验证真实SQL提交与投影前屏障；接收探针不构造1673，也不代表原生奖励网络写帧。
internal static class EvildomWarRewardBatchTests
{
    internal static void Run(TcpTestResources resources, EvildomWarCatalog catalog, string root)
    {
        int before = Count;
        string path = Path.Combine(root, "war-reward-batch.db");
        var repository = new CharacterRepository(path, resources.Quests);
        var owners = new AccountOwners();
        var leases = new List<AccountOwners.Lease>();
        var sessions = new List<GameSession>();
        var clock = new ConsumableUseSessionTests.FakeClock();
        var parties = new PartyService(repository, clock);
        var service = new PartyDungeonService(parties, repository, repository, hiddenDraw: maximum => maximum - 1);
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var kills = SelfTestCatalogs.KillExperience(resources.Archive);
        var clearExperience = SelfTestCatalogs.ClearExperience(resources.Archive);
        var policy = new EvildomWarRewardPolicy(catalog, resources.ItemResources.Items, resources.ItemResources.Boosters);
        try
        {
            for (int index = 0; index < 3; index++)
            {
                var owner = owners.Acquire($"warrewardbatch{index}", () => { });
                leases.Add(owner);
                var candidate = CharacterCreation.Prepare($"WarBatch{index}", resources.Characters.Get(0),
                    catalog.WaitingArea, catalog.WaitingArea, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95, InitialLearning = learning });
                var session = new GameSession(repository, resources.Towns, owner, questCatalog: resources.Quests,
                    entry: new(new(2, 41), 54, "[evildom_war]"), timeProvider: clock,
                    incounts: DungeonIncountCatalog.Load(resources.Archive));
                sessions.Add(session);
                session.Select(0, resumeTown: true);
                session.MarkReady();
                session.CurrentParty = () => parties.Current(session);
            }
            That(parties.SetInformation(sessions[0], 0, PartySettings.Default with { Kind = 5, KindArgument = 0 }).Accepted,
                "批次夹具创建真实普通魔界队伍");
            foreach (var member in sessions.Skip(1))
                That(parties.Invite(sessions[0], member, 0).Accepted &&
                    parties.Respond(member, sessions[0].Selected!.Id, 0, true).Accepted,
                    "批次夹具真实邀请两名队员");
            That(service.StartEvildomWar(sessions[0], catalog) is null, "真实三人普通战役开战");
            var battle = sessions[0].ReadEvildomWar()!.Battle;
            PartyDungeonNotice[] completed = [];
            for (int stage = 0; stage < 5; stage++)
            {
                var next = battle.View.Dungeons.FirstOrDefault(row => row.Kind == 0 && row.State == 0)
                    ?? battle.View.Dungeons.Single(row => row.Kind == 1 && row.State == 0);
                uint dungeon = checked((uint)next.Id);
                That(service.BeginSelection(sessions[0], dungeon).Length == 3, "原队伍真实选择下一开放图");
                var entered = service.Enter(sessions[0], dungeon,
                    catalog.Dungeons.Single(row => row.Id == dungeon).Difficulty, dungeons, rooms, null, null, null, null);
                That(entered.Count(notice => notice.Kind == PartyDungeonNoticeKind.Entry) == 3,
                    "每张图均经真实全员费用与Run安装");
                var instance = sessions[0].PartyDungeon!;
                var entry = sessions[0].CurrentFormalDungeonScene()!.Entry;
                var route = QuestStoryRouteTests.Path(entry.Maze.StaticTopology, entry.StartMap.Position, entry.BossMap.Position);
                for (int pointIndex = 0; pointIndex < route.Length; pointIndex++)
                {
                    foreach (var member in sessions) instance.CompleteLoading(member);
                    var scene = sessions[0].CurrentFormalDungeonScene()!;
                    foreach (var monster in scene.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly)
                        .OrderBy(row => row.IsBoss).ToArray())
                    {
                        var observation = FormalDungeonSessionTests.Observation(scene, monster);
                        var notices = instance.ObserveDeath(sessions[0], monster.ObjectKey,
                            member => member.ObserveFormalDungeonDeath(observation, kills, resources.Growth));
                        var rewardNotices = notices.Where(notice => notice.Kind == PartyDungeonNoticeKind.EvildomWarUpdated &&
                            notice.WarView is { Completed: true }).ToArray();
                        if (rewardNotices.Length != 0) completed = rewardNotices;
                    }
                    if (pointIndex + 1 == route.Length) break;
                    var destination = route[pointIndex + 1];
                    That(instance.Move(sessions[0], checked((byte)destination.X), checked((byte)destination.Y), 0).Length == 3,
                        "三人沿原迷宫连接真实过房");
                }
                That(sessions.All(member => member.CurrentFormalDungeonScene()!.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement),
                    "每人原Run均到达真实终点结算状态");
                if (stage == 4) break;
                var ids = sessions.Select(member => member.Selected!.Id).ToArray();
                byte[] report = DungeonSettlementProtocolTests.Report(ids.Select(id => (id, (byte)99)).ToArray());
                That(DungeonSettlementRequests.TryParsePlayResult(report, out var result), "前置图复用正式46报告");
                foreach (var member in sessions)
                {
                    var enabled = member.EnableFormalDungeonSettlement();
                    That(enabled is not null, "真实通关后才开放结算");
                    member.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
                    var settled = member.SettleFormalDungeon(member.Selected!.Id, result!.RankPoint, result.MaximumRankPoint,
                        report, dungeons, clearExperience, resources.Growth, reportMembers: ids);
                    That(settled.Outcome == DungeonRunOutcome.Accepted, "前置图逐人正式结算");
                }
                That(instance.ResultReturn(sessions[0]).Count(notice => notice.Kind == PartyDungeonNoticeKind.ResultReturn) == 3,
                    "前置图全员提交后返回原等待区");
            }
            That(completed.Length == 3 && battle.Completion is { Facts.Length: 3 } && battle.View.Completed,
                "只有三人真实王座Run完成才取得原奖励通知批次");
            var members = completed[0].Members.OrderBy(member => member.Slot).ToArray();
            That(members.Select(member => member.Slot).SequenceEqual(new byte[] { 0, 1, 2 }), "夹具使用真实席位0、1、2");
            var admissions = members.Select(member => member.Actor.Session.ReadEvildomWar()!).ToArray();
            var idsBySlot = members.Select(member => member.Actor.Character.Id).ToArray();
            var observed = Enumerable.Range(0, 3).Select(_ => new List<PartyDungeonNotice>()).ToArray();
            var prepareCalls = new int[3];
            var allAssetsBefore = idsBySlot.Select(Assets).ToArray();
            for (int slot = 0; slot < members.Length; slot++)
            {
                int capturedSlot = slot;
                var member = members[slot].Actor.Session;
                member.EvildomWarRewardPreparing = view =>
                {
                    prepareCalls[capturedSlot]++;
                    return member.CommitEvildomWarReward(policy, repository, view);
                };
                member.PartyDungeonNoticeArrived = notice =>
                {
                    observed[capturedSlot].Add(notice);
                    return Task.CompletedTask;
                };
            }
            Sql($"CREATE TRIGGER reject_war_batch_receipt BEFORE UPDATE OF delivered ON character_evildom_war_rewards WHEN NEW.character_id={idsBySlot[1]} AND NEW.delivered=1 BEGIN SELECT RAISE(ABORT,'war batch receipt failure'); END");
            SqliteException? failed = null;
            try { PartyDungeonProjection.PublishAsync(completed).GetAwaiter().GetResult(); }
            catch (SqliteException error) { failed = error; }
            That(failed is { SqliteErrorCode: 19, SqliteExtendedErrorCode: 1811 } && failed.Message == "SQLite Error 19: 'war batch receipt failure'.",
                "第二席位必须精确触发真实奖励回执SQL19故障");
            That(observed.All(rows => rows.Count == 0) && prepareCalls.SequenceEqual(new[] { 1, 1, 0 }),
                "先逐人提交；第二人失败时所有接收者投影尚未发生且第三人未开始");
            That(admissions[0].RewardCommit is not null && admissions[1].RewardCommit is null && admissions[2].RewardCommit is null &&
                Scalar("SELECT count(*) FROM character_evildom_war_rewards WHERE character_id=$id", idsBySlot[0]) == 1 &&
                Scalar("SELECT count(*) FROM character_evildom_war_rewards WHERE character_id=$id", idsBySlot[1]) == 1 &&
                Scalar("SELECT delivered FROM character_evildom_war_rewards WHERE character_id=$id", idsBySlot[0]) == 1 &&
                Scalar("SELECT delivered FROM character_evildom_war_rewards WHERE character_id=$id", idsBySlot[1]) == 0 &&
                Scalar("SELECT count(*) FROM character_evildom_war_rewards WHERE character_id=$id", idsBySlot[2]) == 0,
                "第一人已持久交付、第二人只冻结原计划、第三人仍无奖励回执");
            That(Assets(idsBySlot[1]) == allAssetsBefore[1] && Assets(idsBySlot[2]) == allAssetsBefore[2],
                "第二人失败回滚全部邮件与附件，不影响第三人的资产");
            var firstCommit = admissions[0].RewardCommit!;
            var firstPlan = admissions[0].RewardPlan!;
            var secondSelection = admissions[1].RewardSelection;
            var secondPlan = admissions[1].RewardPlan;
            That(secondSelection is not null && secondPlan is not null && admissions[2].RewardSelection is null,
                "第二人失败保留原抽样和完整核心计划，第三人尚未抽样");
            string firstSaved = RewardState(idsBySlot[0]);
            string firstAssets = Assets(idsBySlot[0]);
            string secondSaved = Snapshot("SELECT reward_json FROM character_evildom_war_rewards WHERE character_id=$id", idsBySlot[1]);
            Sql("DROP TRIGGER reject_war_batch_receipt");
            PartyDungeonProjection.PublishAsync(completed).GetAwaiter().GetResult();
            That(ReferenceEquals(admissions[0].RewardCommit, firstCommit) && ReferenceEquals(admissions[0].RewardPlan, firstPlan) &&
                RewardState(idsBySlot[0]) == firstSaved && Assets(idsBySlot[0]) == firstAssets,
                "同批重试第一人原Plan、JSON、MailIds及附件UID完整不变");
            That(ReferenceEquals(admissions[1].RewardSelection, secondSelection) && ReferenceEquals(admissions[1].RewardPlan, secondPlan) &&
                Snapshot("SELECT reward_json FROM character_evildom_war_rewards WHERE character_id=$id", idsBySlot[1]) == secondSaved,
                "同批重试第二人复用原RewardSelection、Plan及持久完整核心，不覆盖冻结产物");
            That(prepareCalls.SequenceEqual(new[] { 2, 2, 1 }) && observed.All(rows => rows.Count == 1) &&
                admissions.All(war => war.RewardCommit is not null), "重试全员交付完成后才逐接收者投影一次");
            var table = observed[0].Single().EvildomWarRewards;
            That(table.Length == 3 && observed.All(rows => rows.Single().EvildomWarRewards.Equals(table)) &&
                table.Select(row => row.Member.Slot).SequenceEqual(new byte[] { 0, 1, 2 }) &&
                table.All(row => row.IsFrom(battle.View) && row.Receipt.Commit.Plan.Completion.BattleId == battle.View.BattleId),
                "全员投影收到同一immutable底层表，均为原席位真实已提交回执");
            for (int slot = 0; slot < 3; slot++)
            {
                That(Scalar("SELECT delivered FROM character_evildom_war_rewards WHERE character_id=$id", idsBySlot[slot]) == 1 &&
                    table[slot].Receipt.Commit.MailIds.SequenceEqual(admissions[slot].RewardCommit!.MailIds),
                    "完整表不包含未交付权益且精确引用每人原邮件回执");
            }
            var statesAfter = idsBySlot.Select(RewardState).ToArray();
            var assetsAfter = idsBySlot.Select(Assets).ToArray();
            foreach (var rows in observed) rows.Clear();
            PartyDungeonProjection.PublishAsync(completed).GetAwaiter().GetResult();
            That(idsBySlot.Select(RewardState).SequenceEqual(statesAfter) && idsBySlot.Select(Assets).SequenceEqual(assetsAfter) &&
                ReferenceEquals(admissions[1].RewardPlan, secondPlan) && observed.All(rows => rows.Count == 1),
                "后续顺序重同步只再次投影原事实，不新增邮件、资产UID或奖励回执");
            byte[] finalReport = DungeonSettlementProtocolTests.Report(idsBySlot.Select(id => (id, (byte)99)).ToArray());
            That(DungeonSettlementRequests.TryParsePlayResult(finalReport, out var finalResult), "王座返城使用正式46报告");
            foreach (var member in sessions)
            {
                var enabled = member.EnableFormalDungeonSettlement();
                That(enabled is not null, "王座提交奖励后仍能正常结算");
                member.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
                That(member.SettleFormalDungeon(member.Selected!.Id, finalResult!.RankPoint, finalResult.MaximumRankPoint,
                    finalReport, dungeons, clearExperience, resources.Growth, reportMembers: idsBySlot).Outcome == DungeonRunOutcome.Accepted,
                    "王座各成员真实提交结算");
            }
            var returned = sessions[0].PartyDungeon!.ResultReturn(sessions[0]);
            That(returned.Count(notice => notice.Kind == PartyDungeonNoticeKind.ResultReturn) == 3 &&
                sessions.All(member => member.Phase == SessionPhase.Town) &&
                table.All(row => row.Receipt.Commit.Plan.Run.ClearFact is { } fact &&
                    row.Receipt.Commit.Plan.Run.Snapshot.Identity.Epoch == fact.Identity.Epoch + 1 && row.IsFrom(battle.View)),
                "真实返等待区推进Run代次，原完整通关事实与已提交奖励来源仍有效");
            foreach (var rows in observed) rows.Clear();
            PartyDungeonProjection.PublishAsync(returned).GetAwaiter().GetResult();
            That(observed.All(rows => rows.Count(notice => notice.Kind == PartyDungeonNoticeKind.EvildomWarUpdated &&
                    notice.EvildomWarRewards.Length == 3 && notice.EvildomWarRewards.All(row => row.IsFrom(battle.View))) == 1) &&
                idsBySlot.Select(RewardState).SequenceEqual(statesAfter) && idsBySlot.Select(Assets).SequenceEqual(assetsAfter),
                "返等待区通知真实重同步全员原奖励，不因活Run代次变化抛错或新增资产");
        }
        finally
        {
            foreach (var session in sessions)
            {
                session.EvildomWarRewardPreparing = null;
                session.PartyDungeonNoticeArrived = null;
                session.End();
            }
            foreach (var owner in leases) owner.Dispose();
        }
        Console.WriteLine($"PASS evildom war reward batch transactions {Count - before}");

        string RewardState(ushort id) => Snapshot("SELECT reward_json,mail_ids,delivered FROM character_evildom_war_rewards WHERE character_id=$id ORDER BY battle_id", id);
        string Assets(ushort id) => Snapshot("SELECT mail_id,delivery_key,state,expires_at FROM mailbox_messages WHERE recipient_id=$id ORDER BY mail_id", id) + ":" + Snapshot("""
            SELECT m.mail_id,m.delivery_key,m.expires_at,a.ordinal,a.item_uid,i.item_core
            FROM mailbox_messages m JOIN mailbox_attachments a ON a.mail_id=m.mail_id
            JOIN character_inventory_items i ON i.item_uid=a.item_uid
            WHERE m.recipient_id=$id ORDER BY m.mail_id,a.ordinal
            """, id);
        string Snapshot(string sql, ushort id)
        {
            using var db = Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
            return JsonSerializer.Serialize(rows);
        }
        long Scalar(string sql, ushort id)
        {
            using var db = Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$id", id);
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        void Sql(string sql)
        {
            using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        SqliteConnection Open()
        {
            var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); return db;
        }
    }
}
