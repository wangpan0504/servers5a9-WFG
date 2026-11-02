using System.Net;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class Strongway2019HandlerTests
{
    public static async Task RunNormalTimeoutResourcesAsync(PvfArchive archive, TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(archive);
        var growth = CharacterGrowthCatalog.Load(archive);
        var dungeons = DungeonCatalog.Load(archive);
        var rooms = DungeonRoomCatalog.Load(archive);
        var drops = DungeonMonsterDropCatalog.Load(archive);
        var kills = DungeonKillExperienceCatalog.Load(archive);
        var stats = new CharacterStatComputer(CharacterCatalog.Load(archive, resources.Items));
        foreach (var (dungeonId, memberCount) in new[] { (5359u, 1), (5359u, 2), (5363u, 4) })
        {
            using var f = new Strongway2019MatchingTests.Fixture(towns, activity, growth);
            var wholeTeam = f.Team();
            foreach (var unused in wholeTeam.Skip(memberCount)) f.Parties.Detach(unused);
            if (memberCount == 1) f.Parties.Detach(wholeTeam[0]);
            var sessions = wholeTeam.Take(memberCount).ToArray();
            var online = new OnlineSessionDirectory();
            foreach (var session in sessions) online.Register(session.CaptureSocialActor()!.Owner, session);
            using var log = new ServerLog(Path.Combine(Path.GetTempPath(), "strongway-normal-timeout-" + Guid.NewGuid().ToString("N")));
            var town = new TownProjection(towns, stats, null, resources.EpicPieces);
            var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock);
            var profiles = new Strongway2019ProfileProjection(f.Repository, activity.DungeonStartDelay);
            var rewards = new Strongway2019RewardProjection(resources.Items, f.Repository, log, profiles);
            var projection = new Strongway2019Projection(rewards, matching, f.Clock, activity);
            var partyDungeons = new PartyDungeonService(f.Parties, f.Repository);
            var dungeonHandler = new DungeonHandler(log, dungeons, rooms, growth, kills, town,
                new CharacterLevelProjection(stats), drops, resources.Items, resources.EpicPieces,
                partyDungeons: partyDungeons, partyStats: stats, parties: f.Parties,
                strongwayMatching: matching, strongwayProjection: projection, strongwayRewards: rewards);
            var handler = new Strongway2019Handler(matching, projection, catalog: activity, clock: f.Clock, online: online);
            var stops = sessions.Select(_ => new CancellationTokenSource()).ToArray();
            var outputs = sessions.Select(_ => new MemoryStream()).ToArray();
            var frames = sessions.Select(_ => new List<ushort>()).ToArray();
            var senders = sessions.Select((session, i) => new GameConnectionSender(session.CaptureSocialActor()!.Owner,
                outputs[i], stops[i], 0, (_, opcode, _, _, _) => frames[i].Add(opcode))).ToArray();
            var contexts = sessions.Select((session, i) => new GameConnectionContext(new(i, IPAddress.Loopback, outputs[i], stops[i]),
                session.CaptureSocialActor()!.Owner, session, senders[i])).ToArray();
            try
            {
                foreach (var context in contexts) dungeonHandler.PreparePartyContext(context);
                if (memberCount == 1)
                {
                    That(sessions[0].EnterFormalDungeon(dungeonId, 0, 2, dungeons, rooms, drops,
                        items: resources.Items, pieces: resources.EpicPieces) is not null &&
                        sessions[0].CompleteFormalDungeonLoading() is not null, "单人简单超时使用真实准入与原Run加载");
                }
                else
                {
                    That(partyDungeons.BeginSelection(sessions[0]).Length == memberCount &&
                        partyDungeons.Enter(sessions[0], dungeonId, 2, dungeons, rooms, drops,
                            resources.Items, resources.EpicPieces, null).Length == memberCount,
                        "双人简单和四人英雄通过原队伍正式入场");
                    var instance = sessions[0].PartyDungeon!;
                    foreach (var session in sessions) instance.CompleteLoading(session);
                }
                That(sessions.All(session => session.CurrentFormalDungeonScene() is
                    { Entry.Strongway2019TimeLimitSeconds: 3600, Snapshot.Phase: SinglePlayerDungeonPhase.Dungeon }),
                    "正常加载后各原Run冻结真实3600秒期限");
                f.Clock.Advance(TimeSpan.FromSeconds(3599));
                await handler.MaintainAsync(CancellationToken.None);
                That(sessions.All(session => session.Phase == SessionPhase.Dungeon), "期限前无人发包不提前返城");
                f.Clock.Advance(TimeSpan.FromSeconds(1));
                var expired = sessions.Select(session => session.CaptureStrongway2019NormalTimeout()).ToArray();
                That(expired.All(timeout => timeout is not null), "到期回执捕获原Run和选角代数");
                sessions[^1].CommandGate.Wait();
                try
                {
                    await handler.MaintainAsync(CancellationToken.None);
                    That(sessions.All(session => session.Phase == SessionPhase.Dungeon) &&
                        sessions.Select((session, i) => session.IsCurrentStrongway2019NormalTimeout(expired[i]!)).All(current => current),
                        "任一成员连接门忙时全队不退出且保留原Run供重试");
                }
                finally { sessions[^1].CommandGate.Release(); }
                if (sessions[0].CurrentParty?.Invoke() is { } originalParty)
                {
                    originalParty.Commands.Wait();
                    try
                    {
                        await handler.MaintainAsync(CancellationToken.None);
                        That(sessions.All(session => session.Phase == SessionPhase.Dungeon), "原队伍门忙时全员不误退出");
                    }
                    finally { originalParty.Commands.Release(); }
                }
                await handler.MaintainAsync(CancellationToken.None);
                await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
                That(sessions.All(session => session.Phase == SessionPhase.Town && session.PartyDungeon is null &&
                        session.CurrentFormalDungeonScene() is null),
                    "首期普通到3600秒只调用生产维护也必须全员自动返城：" + dungeonId + "/" + memberCount);
                var returns = frames.Select(rows => rows.Count(opcode => opcode == 24)).ToArray();
                That(returns.All(count => count == 1), "每名到期成员只发布一次原返城NOTI24区域场景");
                await handler.MaintainAsync(CancellationToken.None);
                await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
                That(frames.Select(rows => rows.Count(opcode => opcode == 24)).SequenceEqual(returns) &&
                    sessions.All(session => f.Repository.LoadMailbox(session.CaptureSocialActor()!.Owner,
                        session.Selected!.Id, f.Clock.GetUtcNow().ToUnixTimeSeconds()).Entries.Length == 0 &&
                        f.Repository.ReadStrongway2019Statistics(session.CaptureSocialActor()!.Owner, session.Selected.Id) ==
                            new Strongway2019Statistics(0, 0, 0, 0)),
                    "重复维护不重复发布返城NOTI24，简单和英雄超时均无奖励、胜场或通关成绩");
                if (memberCount == 1)
                {
                    var session = sessions[0];
                    session.ReturnToRoster();
                    session.Select(0, resumeTown: true); session.MarkReady();
                    That(session.EnterFormalDungeon(dungeonId, 0, 2, dungeons, rooms, drops,
                        items: resources.Items, pieces: resources.EpicPieces) is not null &&
                        session.CompleteFormalDungeonLoading() is not null, "旧epoch回归重新沿原正式入口进图");
                    f.Clock.Advance(TimeSpan.FromSeconds(3600));
                    await session.Strongway2019NormalTimeoutArrived!(expired[0]!);
                    That(session.Phase == SessionPhase.Dungeon && session.CaptureStrongway2019NormalTimeout() is not null &&
                        !session.IsCurrentStrongway2019NormalTimeout(expired[0]!),
                        "旧Run和旧选角epoch的到期回调不能退出新入口");
                    var oldOwnerTimeout = session.CaptureStrongway2019NormalTimeout()!;
                    var replacement = f.Takeover(session);
                    await session.Strongway2019NormalTimeoutArrived!(oldOwnerTimeout);
                    await handler.MaintainAsync(CancellationToken.None);
                    await senders[0].DrainAsync();
                    That(replacement.IsCurrent && session.Phase == SessionPhase.Dungeon &&
                        session.CaptureStrongway2019NormalTimeout() is null &&
                        frames[0].Count(opcode => opcode == 24) == returns[0],
                        "旧owner超时回调和在线扫描均不修改接管后的身份或额外发布返城NOTI24");
                }
            }
            finally
            {
                foreach (var session in sessions) session.PartyDungeonNoticeArrived = null;
                foreach (var session in sessions) session.Strongway2019NormalTimeoutArrived = null;
                foreach (var sender in senders) await sender.DisposeAsync();
                foreach (var output in outputs) output.Dispose();
                foreach (var stop in stops) stop.Dispose();
            }
        }
        Console.WriteLine($"PASS {Count - before} 首期单人/双人简单与四人英雄无人发包到期全员返城");
    }
}
