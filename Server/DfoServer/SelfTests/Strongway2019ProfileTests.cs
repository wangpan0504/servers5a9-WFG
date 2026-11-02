using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019ProfileTests
{
    public static async Task RunAccountResourcesAsync(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(pvf);
        using var f = new Strongway2019MatchingTests.Fixture(towns, activity);
        var first = f.Team(); var second = f.Team();
        var session = second[0];
        var actor = session.CaptureSocialActor()!;
        using var stop = new CancellationTokenSource();
        using var output = new MemoryStream();
        var frames = new List<(ushort Opcode, byte[] Body)>();
        await using var sender = new GameConnectionSender(actor.Owner, output, stop, 0,
            (_, opcode, _, body, _) => frames.Add((opcode, body.ToArray())));
        var context = new GameConnectionContext(new(1753, IPAddress.Loopback, output, stop), actor.Owner, session, sender);
        var profiles = new Strongway2019ProfileProjection(f.Repository, activity.DungeonStartDelay, f.Repository);
        await profiles.InformationAsync(context);
        await sender.DrainAsync();
        That(frames.Count(row => row.Opcode == 1491) == 1 && frames.Count(row => row.Opcode == 1753) == 1 &&
            frames.Single(row => row.Opcode == 1753).Body.SequenceEqual(new byte[24]),
            "type55实际连接初次账号信息同时发送1491与完整1753零历史快照");
        frames.Clear();
        await new Strongway2019ProfileProjection(f.Repository, activity.DungeonStartDelay).InformationAsync(context);
        await sender.DrainAsync();
        That(frames.Count == 1 && frames[0].Opcode == 1491, "未装配账号奖励存储时保持1491，不发送伪造1753");

        var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock);
        var ticket = matching.Queue(first[0], first.Select(member => member.PrepareStrongway2019Admission(5360, true)!).ToArray())!;
        matching.Queue(second[0], second.Select(member => member.PrepareStrongway2019Admission(5360, true)!).ToArray());
        var pair = matching.ReserveNext(ticket)!;
        var entered = new Strongway2019MatchingEntryService(matching, f.Parties, f.Repository, f.Repository, f.Clock)
            .Enter(pair, DungeonCatalog.Load(pvf), DungeonRoomCatalog.Load(pvf), DungeonMonsterDropCatalog.Load(pvf), items.Items, items.EpicPieces);
        That(entered is not null, "账号刷新回归沿原八人正式匹配入场事务");
        foreach (var team in new[] { first, second })
            foreach (var member in team) team[0].PartyDungeon!.CompleteLoading(member);
        var competition = entered!.Match.Competition;
        var losing = first[0].CaptureStrongway2019Run(first[0].PartyDungeon!)!;
        var winning = session.CaptureStrongway2019Run(session.PartyDungeon!)!;
        foreach (int phase in new[] { 3, 4 })
            That(competition.ConfirmOpening(losing, losing.Scene.Identity, phase) == DungeonRunOutcome.Accepted &&
                competition.ConfirmOpening(winning, winning.Scene.Identity, phase) == DungeonRunOutcome.Accepted,
                "1753回归完成真实双方开场屏障：" + phase);
        That(competition.Exit(losing, losing.Scene.Identity) == DungeonRunOutcome.Accepted,
            "原比赛对手退出冻结真实胜队，不伪造个人终点");
        using var log = new ServerLog(Path.Combine(Path.GetTempPath(), "strongway-account-profile-" + Guid.NewGuid().ToString("N")));
        var rewards = new Strongway2019RewardProjection(items.Items, f.Repository, log, profiles);
        var receipt = rewards.Commit(context);
        That(receipt is { Commit.Granted: true }, "1753刷新前由正式奖励仓储提交唯一737邮件和账号额度");
        frames.Clear();
        await rewards.CommittedAsync(context, receipt!);
        await sender.DrainAsync();
        byte[] used = Convert.FromHexString("000000000000000000000000000000000000000001000000");
        That(frames.Single(row => row.Opcode == 1753).Body.SequenceEqual(used),
            "真实匹配奖励提交后沿原Profile路径刷新1753，只增加本账号匹配周已用次数");
        frames.Clear();
        await rewards.CommittedAsync(context, receipt!);
        await sender.DrainAsync();
        That(frames.Single(row => row.Opcode == 1753).Body.SequenceEqual(used) &&
            f.Repository.ReadStrongway2019RewardState(actor.Owner, Strongway2019RewardPeriod.At(f.Clock.GetUtcNow()), Strongway2019RewardBucket.Matching) == new Strongway2019RewardState(1, 1),
            "同一奖励回执重放不重复消费账号日周额度");

        var alternate = f.Repository.Create(actor.Owner, new NewCharacter("SWProfileAlt", new(65, 1, 450, 234), null) { Level = 95 });
        session.ReturnToRoster(); session.Select(alternate.Slot, resumeTown: true); session.MarkReady();
        frames.Clear();
        await profiles.InformationAsync(context);
        await sender.DrainAsync();
        That(frames.Single(row => row.Opcode == 1753).Body.SequenceEqual(used) &&
            frames.Single(row => row.Opcode == 1491).Body.SequenceEqual(new byte[16]),
            "同账号换到另一真实角色仍读账号奖励，个人1491保持该角色空历史");
        frames.Clear();
        await rewards.CommittedAsync(context, receipt!);
        await profiles.InformationAsync(context, () => false);
        await sender.DrainAsync();
        That(frames.Count == 0, "旧角色奖励回执与失效发布谓词不能向新选角发送1491或1753");

        AccountOwners.Lease replacement;
        using (var heldStop = new CancellationTokenSource())
        using (var heldOutput = new GameSenderBudgetTests.HeldStream())
        {
            await using var heldSender = new GameConnectionSender(actor.Owner, heldOutput, heldStop, 0, (_, _, _, _, _) => { });
            var heldContext = new GameConnectionContext(new(1755, IPAddress.Loopback, heldOutput, heldStop), actor.Owner, session, heldSender);
            Task publication = profiles.InformationAsync(heldContext);
            try
            {
                await heldOutput.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                replacement = f.Takeover(actor.Owner);
            }
            finally { heldOutput.Release.TrySetResult(); }
            bool stale = false;
            try { await publication.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (InvalidOperationException error) when (error.Message == "Stale account generation") { stale = true; }
            That(stale && !actor.Owner.IsCurrent && replacement.IsCurrent && heldOutput.Frames.Count == 1 &&
                heldOutput.Frames[0].SequenceEqual(GameFrame.Build(0, 1491, 0, new byte[16])),
                "账号快照读取后在锁外接管，已开始的1491写后复验拒绝旧owner，不继续写1753");
        }
        var reopened = new CharacterRepository(f.DatabasePath);
        var restored = new GameSession(reopened, towns, replacement, entry: session.ChannelEntry, timeProvider: f.Clock);
        using var newStop = new CancellationTokenSource(); using var newOutput = new MemoryStream();
        var newFrames = new List<(ushort Opcode, byte[] Body)>();
        await using var newSender = new GameConnectionSender(replacement, newOutput, newStop, 0,
            (_, opcode, _, body, _) => newFrames.Add((opcode, body.ToArray())));
        try
        {
            restored.Select(alternate.Slot, resumeTown: true); restored.MarkReady();
            var restoredContext = new GameConnectionContext(new(1754, IPAddress.Loopback, newOutput, newStop), replacement, restored, newSender);
            var restoredProfiles = new Strongway2019ProfileProjection(reopened, activity.DungeonStartDelay, reopened);
            await restoredProfiles.InformationAsync(restoredContext);
            await newSender.DrainAsync();
            That(newFrames.Single(row => row.Opcode == 1753).Body.SequenceEqual(used),
                "新仓储、新owner及新Session重连恢复同账号已提交周奖励次数");
            f.Clock.Advance(TimeSpan.FromDays(1));
            newFrames.Clear(); await restoredProfiles.InformationAsync(restoredContext); await newSender.DrainAsync();
            That(newFrames.Single(row => row.Opcode == 1753).Body.SequenceEqual(used), "日切清日次数仍保留本周1753奖励计数");
            f.Clock.Advance(TimeSpan.FromDays(2));
            newFrames.Clear(); await restoredProfiles.InformationAsync(restoredContext); await newSender.DrainAsync();
            That(newFrames.Single(row => row.Opcode == 1753).Body.SequenceEqual(new byte[24]), "周三新周期1753重读真实账本，不沿用旧周缓存");
        }
        finally { restored.End(); }
        Console.WriteLine($"PASS {Count - before} 首期1753真实账号奖励、跨角色、重连及周期刷新");
    }

    public static void RunCore()
    {
        int before = Count;
        That(Strongway2019ProfilePackets.CharacterData(new(7, 12_345, 2, 45_678), 5).SequenceEqual(
            Convert.FromHexString("07000000C1430000020000006EB20000")),
            "1491原生四u32为角色胜场、匹配raw毫秒、普通mode、普通毫秒，匹配只在wire补五秒");
        That(Strongway2019ProfilePackets.CharacterData(new(0, 0, 0, 0), 5).SequenceEqual(new byte[16]),
            "无匹配记录保留零，不把五秒补偿变成一条伪纪录");
        Reject<InvalidDataException>(() => Strongway2019ProfilePackets.CharacterData(new(0, 1, 3, 1), 5),
            "未开放普通mode3不能冒首期难度记录");
        Reject<OverflowException>(() => Strongway2019ProfilePackets.CharacterData(new(1, uint.MaxValue, 0, 0), 5),
            "匹配raw补偿溢出不能截断为较快时间");
        Console.WriteLine($"PASS {Count - before} 首期1491角色历史原生包体");
    }
}
