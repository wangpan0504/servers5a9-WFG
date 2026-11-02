using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class Strongway2019HandlerTests
{
    public static async Task RunCheckpointTakeoverResourcesAsync(PvfArchive archive, TownCatalog towns,
        ItemResources resources, CharacterCatalog characters)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(archive);
        var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(archive);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(archive, learning);
        var initialLearning = DfoServer.Game.Skills.SkillLearning.Initialize(0, characters.Get(0).Skills, learning).State!
            with { Experience = growth.GetLevelStartExperience(95) };
        using var f = new Strongway2019MatchingTests.Fixture(towns, activity, growth, initialLearning);
        var first = f.Team(); var second = f.Team(); var oldSessions = first.Concat(second).ToArray();
        var actors = oldSessions.Select(session => session.CaptureSocialActor()!).ToArray();
        var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock, completionRecovery: true);
        var ticket = matching.Queue(first[0], first.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray())!;
        matching.Queue(second[0], second.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray());
        var entry = new Strongway2019MatchingEntryService(matching, f.Parties, f.Repository, f.Repository, f.Clock)
            .Enter(matching.ReserveNext(ticket)!, DungeonCatalog.Load(archive), DungeonRoomCatalog.Load(archive),
                DungeonMonsterDropCatalog.Load(archive), resources.Items, resources.EpicPieces)!;
        That(entry is not null, "checkpoint接管胶水沿真实八人原匹配入场，不手工注入match索引");
        using var log = new ServerLog(Path.Combine(Path.GetTempPath(), "strongway-checkpoint-glue-" + Guid.NewGuid().ToString("N")));
        var online = new OnlineSessionDirectory();
        var profiles = new Strongway2019ProfileProjection(f.Repository, activity.DungeonStartDelay, f.Repository);
        var rewards = new Strongway2019RewardProjection(resources.Items, f.Repository, log, profiles, online);
        var projection = new Strongway2019Projection(rewards, matching, f.Clock, activity);
        var maintenance = new Strongway2019Handler(matching, projection, catalog: activity, clock: f.Clock, online: online);
        var oldStops = oldSessions.Select(_ => new CancellationTokenSource()).ToArray();
        var oldOutputs = oldSessions.Select(_ => new MemoryStream()).ToArray();
        var oldFrames = oldSessions.Select(_ => new List<ushort>()).ToArray();
        var oldSenders = oldSessions.Select((session, i) => new GameConnectionSender(actors[i].Owner, oldOutputs[i], oldStops[i], 0,
            (_, opcode, _, _, _) => oldFrames[i].Add(opcode))).ToArray();
        var oldContexts = oldSessions.Select((session, i) => new GameConnectionContext(new(7000 + i, IPAddress.Loopback, oldOutputs[i], oldStops[i]),
            actors[i].Owner, session, oldSenders[i])).ToArray();
        var restored = new GameSession[8]; var replacements = new AccountOwners.Lease[8];
        var allRestored = new List<GameSession>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                int index = i;
                oldSessions[i].Strongway2019NoticeArrived = notice => projection.DeliverAsync(oldContexts[index], notice);
            }
            var run = first[0].CaptureStrongway2019Run(first[0].PartyDungeon!)!;
            entry!.Match.Competition.Exit(run, run.Scene.Identity);
            That(entry.Match.Competition.Snapshot.Terminal is { Reason: Strongway2019FinishReason.Exit, WinnerSide: 1 },
                "真实Loading退出签发原四名胜者，尚未投影或checkpoint");
            for (int i = 0; i < 8; i++)
            {
                replacements[i] = f.Takeover(actors[i].Owner);
                restored[i] = CreateSession(i, replacements[i]);
                matching.Detach(oldSessions[i]);
            }
            That(actors.All(actor => !actor.Owner.IsCurrent) && matching.Snapshot().Matches.Length == 1,
                "八个旧owner全失效且原Session均Detach，未checkpoint终局仍在原matches索引");
            await maintenance.MaintainAsync(CancellationToken.None);
            That(Scalar("SELECT count(*) FROM character_strongway2019_match_facts") is 0L &&
                !entry.Match.CompletionCheckpointCommitted && oldFrames.All(frames => frames.Count == 0),
                "当前目录尚未登记新owner时真实维护扣住终局成功，不使用旧lease或临时Acquire");
            for (int i = 0; i < 8; i++) online.Register(replacements[i], restored[i]);
            var town = new TownProjection(towns, new CharacterStatComputer(characters), null, resources.EpicPieces,
                learning: learning) { Strongway2019Profile = profiles };
            var commands = new GameCommandRegistry();
            new CharacterHandler(characters, town, log, resources.GoldLimits, learning: learning, strongway2019: projection).Register(commands);
            commands.Seal();
            // 一名新胜者先自然Ready，后续维护才写出原终局；不能要求他再次选角才能领回。
            using var earlyStop = new CancellationTokenSource(); using var earlyOutput = new MemoryStream();
            var earlyFrames = new List<(ushort Opcode, byte[] Body)>();
            await using var earlySender = new GameConnectionSender(replacements[4], earlyOutput, earlyStop, 0,
                (_, opcode, _, body, _) => earlyFrames.Add((opcode, body.ToArray())));
            var earlySession = restored[4];
            var earlyContext = new GameConnectionContext(new(7090, IPAddress.Loopback, earlyOutput, earlyStop),
                replacements[4], earlySession, earlySender) { Strongway2019 = projection };
            earlySession.MailboxArrived = receipt => earlySender.Post(0, (ushort)NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(),
                "checkpoint_early_mail", () => earlySession.CanPublishMailboxAlarm(receipt));
            // 反射只使RED可编译；GREEN仍调用生产相同门禁和本人恢复入口。
            var callback = typeof(GameSession).GetProperty("Strongway2019RecoveryArrived",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var recoverReady = typeof(Strongway2019Projection).GetMethod("RecoverReadyRewardsAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (callback is not null && recoverReady is not null)
                callback.SetValue(earlySession, (Func<ushort, Task<bool>>)(character =>
                    (Task<bool>)recoverReady.Invoke(projection, [earlyContext, character])!));
            var earlyRoster = earlySession.PrepareRoster();
            await earlyContext.Notify(NotiPacketType.USERINFO,
                CharacterPackets.Roster(earlyRoster.Entries.ToArray(), earlyRoster.Capacity, resources.Items),
                "checkpoint_early_roster", () => earlySession.CanPublishRoster(earlyRoster));
            earlySession.MarkRosterPublished(earlyRoster);
            That(await commands.DispatchAsync(earlyContext, new Packet(1, (ushort)CmdPacketType.SELECT_CHARACTER, [0, 0])) == CommandResult.Handled,
                "新胜者在checkpoint前经真实CharacterHandler自然Ready");
            await earlySender.DrainAsync();
            That(earlySession.Ready && earlyFrames.Count(row => row.Opcode == 1491) == 1 &&
                earlyFrames.Count(row => row.Opcode == 1753) == 1 && Scalar("SELECT count(*) FROM account_strongway2019_rewards") is 0L,
                "checkpoint前无pending只发原1491/1753各一次，没有提前奖励");
            f.Sql("CREATE TRIGGER checkpoint_glue_fail BEFORE INSERT ON character_strongway2019_match_facts WHEN NEW.character_id=" + actors[7].Character.Id +
                " BEGIN SELECT RAISE(ABORT,'checkpoint glue rollback'); END");
            Exception? failure = null;
            try { await maintenance.MaintainAsync(CancellationToken.None); }
            catch (SqliteException error) { failure = error; }
            That(failure is not null && Scalar("SELECT count(*) FROM character_strongway2019_match_facts") is 0L &&
                !entry.Match.CompletionCheckpointCommitted && matching.Snapshot().Matches.Length == 1 && oldFrames.All(frames => frames.Count == 0),
                "真实维护晚期SQL失败全队回滚，未发终局，原match保留下一轮重试");
            f.Sql("DROP TRIGGER checkpoint_glue_fail");
            replacements[0] = f.Takeover(replacements[0]);
            restored[0] = CreateSession(0, replacements[0]);
            online.Register(replacements[0], restored[0]);
            earlyContext.CommandGate.Wait();
            try
            {
                await maintenance.MaintainAsync(CancellationToken.None);
                That(entry.Match.CompletionCheckpointCommitted && Scalar("SELECT count(*) FROM character_strongway2019_match_facts") is 4L &&
                    Scalar("SELECT count(*) FROM account_strongway2019_rewards") is 0L && matching.Snapshot().Matches.Length == 1,
                    "checkpoint已提交但当前Ready连接命令门忙时保留原match，下次维护重试本人恢复");
            }
            finally { earlyContext.CommandGate.Release(); }
            await maintenance.MaintainAsync(CancellationToken.None);
            await Task.WhenAll(oldSenders.Select(sender => sender.DrainAsync()));
            await earlySender.DrainAsync();
            That(entry.Match.CompletionCheckpointCommitted && Scalar("SELECT count(*) FROM character_strongway2019_match_facts") is 4L &&
                Scalar("SELECT count(*) FROM account_strongway2019_rewards") is 1L &&
                matching.Snapshot().Matches.Length == 0 && oldFrames.All(frames => frames.Count == 0),
                "失败后当前reporter落盘，已Ready新胜者无须再选角即自取原奖励，然后释放原索引；旧连接不发结果");
            var earlyHistory = f.Repository.ReadStrongway2019Statistics(replacements[4], actors[4].Character.Id);
            That(earlyHistory.MatchingWins == 1 && earlyFrames.Count(row => row.Opcode == 1491) == 2 &&
                earlyFrames.Count(row => row.Opcode == 1753) == 2 &&
                earlyFrames.Count(row => row.Opcode == (ushort)NotiPacketType.MAILBOX_ALARM) == 1 &&
                earlyFrames.Last(row => row.Opcode == 1491).Body.SequenceEqual(Strongway2019ProfilePackets.CharacterData(earlyHistory, activity.DungeonStartDelay)),
                "维护恢复只为真实变化追加一次1491/1753和邮件提示，原新Session立即看见一胜");
            earlySession.ReturnToRoster();
            callback?.SetValue(earlySession, null);

            for (int i = 0; i < 8; i++)
            {
                using var stop = new CancellationTokenSource(); using var output = new MemoryStream();
                var frames = new List<(ushort Opcode, byte[] Body)>();
                await using var sender = new GameConnectionSender(replacements[i], output, stop, 0,
                    (_, opcode, _, body, _) => frames.Add((opcode, body.ToArray())));
                var session = restored[i];
                var context = new GameConnectionContext(new(7100 + i, IPAddress.Loopback, output, stop), replacements[i], session, sender)
                    { Strongway2019 = projection };
                session.MailboxArrived = receipt => sender.Post(0, (ushort)NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(),
                    "checkpoint_ready_mail", () => session.CanPublishMailboxAlarm(receipt));
                for (int selection = 0; selection < 2; selection++)
                {
                    var roster = session.PrepareRoster();
                    await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(roster.Entries.ToArray(), roster.Capacity, resources.Items),
                        "checkpoint_selection_roster", () => session.CanPublishRoster(roster));
                    session.MarkRosterPublished(roster);
                    That(await commands.DispatchAsync(context, new Packet(1, (ushort)CmdPacketType.SELECT_CHARACTER, [0, 0])) == CommandResult.Handled,
                        "通过真实CharacterHandler选角进入MarkReady恢复胶水");
                    await sender.DrainAsync();
                    var history = f.Repository.ReadStrongway2019Statistics(replacements[i], actors[i].Character.Id);
                    var mails = f.Repository.LoadMailbox(replacements[i], actors[i].Character.Id, f.Clock.GetUtcNow().ToUnixTimeSeconds());
                    That(session.Ready && session.CurrentStrongway2019Match is null && session.PartyDungeon is null &&
                        history.MatchingWins == (i < 4 ? 0U : 1U) && mails.Entries.Length == (i < 4 ? 0 : 1),
                        "新Session自然Ready恢复原胜者一份统计和737，败方无奖，不复活旧Match");
                    That(frames.Last(row => row.Opcode == 1491).Body.SequenceEqual(Strongway2019ProfilePackets.CharacterData(history, activity.DungeonStartDelay)),
                        "Ready完成恢复后1491实际发送恢复后的持久胜场");
                    if (i >= 4) That(mails.Entries.Single().Attachments is [{ Item.Core: { ItemId: 490012737, Count: 1 } }],
                        "真实Ready邮箱保留完整737附件，重复选角不重新发奖");
                    int histories = (selection + 1) + (i < 4 || i == 4 ? 0 : 1);
                    That(frames.Count(row => row.Opcode == 1491) == histories && frames.Count(row => row.Opcode == 1753) == histories,
                        "无pending选角只发送原1491/1753各一次；仅首次真实恢复追加一次，重复Ready不多发");
                    if (selection == 0) session.ReturnToRoster();
                }
                That(frames.Count(row => row.Opcode == (ushort)NotiPacketType.MAILBOX_ALARM) == (i < 4 || i == 4 ? 0 : 1),
                    "两次真实Ready只对尚未恢复邮件通知一次，维护已通知者不重复");
            }
            That(Scalar("SELECT count(*) FROM account_strongway2019_rewards") is 4L &&
                Scalar("SELECT count(*) FROM character_strongway2019_statistics") is 4L,
                "生产Maintain→Online新reporter→两次Ready完整链最终只有四份奖励与统计");
        }
        finally
        {
            foreach (var session in oldSessions) session.Strongway2019NoticeArrived = null;
            foreach (var sender in oldSenders) await sender.DisposeAsync();
            foreach (var session in allRestored) session.End();
            foreach (var output in oldOutputs) output.Dispose();
            foreach (var stop in oldStops) stop.Dispose();
        }
        Console.WriteLine($"PASS {Count - before} 首期八旧owner接管、原维护重试、Online新reporter及真实Ready恢复");

        GameSession CreateSession(int i, AccountOwners.Lease owner)
        {
            var session = new GameSession(f.Repository, towns, owner, entry: oldSessions[i].ChannelEntry, timeProvider: f.Clock,
                items: resources.Items, pieces: resources.EpicPieces, strongway2019Catalog: activity, strongway2019AntiEvil: _ => 5400);
            allRestored.Add(session); return session;
        }
        object? Scalar(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + f.DatabasePath + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
        }
    }
}
