using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Protocol;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestCinematicSettlementTests
{
    // 真实3599整条叠层路线；敌对死亡和终末33明确合成，不代表客户端播放或实发顺序。
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = CharacterGrowthCatalog.Load(pvf,skills);
        var kills = DungeonKillExperienceCatalog.Load(pvf);
        var clears = DungeonClearExperienceCatalog.Load(pvf);
        var parameters = QuestRewardParameterCatalog.Load(pvf);
        var connections = QuestConnectionCatalog.Load(pvf);
        var dungeons = DungeonCatalog.Load(pvf); var rooms = DungeonRoomCatalog.Load(pvf);
        var area = towns.Areas.Values.First(row => row.Level <= 74 && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 96 &&
                (option.QuestId == -1 || option.InProgress && option.QuestId == 3599 || !option.InProgress && option.QuestId != 3599)));
        var done = new HashSet<int>(area.Quests) { 3598 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var option = towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.First(row => row.DungeonId == 96 &&
            (row.QuestId == -1 || row.InProgress && row.QuestId == 3599 || !row.InProgress && row.QuestId != 3599));
        if (!option.InProgress && option.QuestId > 0) done.Add(option.QuestId);
        done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0 && !gate.Quests.Contains(3599)).Quests);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        foreach (var scenario in new[] { (Request:3599u,Finish:false,Replay:false),(Request:0u,Finish:false,Replay:false),
            (Request:3599u,Finish:true,Replay:false),(Request:3599u,Finish:false,Replay:true) })
        {
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,74,
                scenario.Replay ? done.Append(3599).ToArray() : done.ToArray(),position:position);
            FormalDungeonReceipt opening;
            if (scenario.Replay)
            {
                That(connections.TryGetReplayDungeon(3599,out uint route) && route == 96 &&
                    f.Session.PrepareQuestReplay(3599,connections,dungeons,rooms) is not null &&
                    f.Session.BeginQuestReplayDungeonSelection(96).Receipt is not null,"真实关联目录允许已完成3599回放");
                opening = f.Session.EnterQuestReplay(96,3599,0)!;
                That(opening.Entry.IsQuestReplay && opening.Entry.RouteQuestId == 3599 && opening.Entry.QuestId == 0 &&
                    f.Read().ActiveMainQuests.All(row => row.QuestId != 3599),"回放保留原身份，不伪造活动接取");
            }
            else
            {
                That(f.Session.ChangeQuest(3599,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                    "完整演出结算沿真实前置接取3599");
                opening = f.Session.EnterFormalDungeon(96,scenario.Request,0,dungeons,rooms)!;
            }
            var loaded = f.Session.CompleteFormalDungeonLoading()!;
            That(loaded.Snapshot.CurrentMap.Id == 76112 && loaded.Monsters.Monsters.All(row => !row.IsBlocking),
                "真实76112只有非战斗演员，不虚构前序死亡");
            That(f.Session.MoveFormalDungeon(1,0) is not null,"按真实基础门墙进入76114");
            loaded = f.Session.CompleteFormalDungeonLoading()!;
            That(loaded.Snapshot.CurrentMap.Id == 76114 && f.Session.Phase == SessionPhase.Dungeon &&
                f.Session.EnableFormalDungeonSettlement() is null,"零死亡基础路线不能仅凭友方终点许可结算");
            That(f.Session.MoveFormalDungeon(1,0,1) is not null,"沿实际叠层身份进入76113");
            loaded = f.Session.CompleteFormalDungeonLoading()!;
            var enemies = loaded.Monsters.Monsters.Where(row => row.IsBlocking).ToArray();
            That(loaded.Snapshot.CurrentMap.Id == 76113 && enemies.Length == 9 &&
                f.Session.EnableFormalDungeonSettlement() is null,"76113保留九个真实敌对演员，层内未清不能结算");
            foreach (var enemy in enemies)
                That(f.Session.ObserveFormalDungeonScriptedDeath(new(enemy.ObjectKey,0x22,true,new string('A',64)),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                    "合成脚本39只对真实层内敌人提交原死亡事务");
            That(f.Session.Phase == SessionPhase.Dungeon && f.Session.MoveFormalDungeon(1,0,1) is not null,
                "清完76113后返回原基础76114而非替换地图");
            That(f.Session.EnableFormalDungeonSettlement() is null,"返回加载中仍不能发布结算");
            loaded = f.Session.CompleteFormalDungeonLoading()!;
            That(loaded.Snapshot.CurrentMap.Id == 76114 && f.Session.Phase == SessionPhase.Settlement &&
                loaded.Monsters.Monsters.All(row => !row.CanReportDeath),
                "真实叠层死亡后加载全非战斗终点产生合法ClearFact，不把62959当死亡Boss");
            var before = f.Read();
            That(f.Session.EnableFormalDungeonSettlement() is null,"本次接取演出scalar1时不能提前发31");
            byte[] report = DungeonSettlementProtocolTests.Report(before.Id,99);
            That(DungeonSettlementRequests.TryParsePlayResult(report,out var parsed),"完整46正文沿原Parser");
            if (scenario.Replay)
            {
                That(f.Session.SettleFormalDungeon(parsed!.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth).Outcome == DungeonRunOutcome.Accepted &&
                    f.Session.FormalDungeonResultFocus() is not null && f.Read().ActiveMainQuests.All(row => row.QuestId != 3599),
                    "已完成演出回放无接取scalar，不被新许可门永久拒绝46");
                That(f.Session.ReturnFromFormalDungeon() is not null,"3599回放结算保留正式返回城镇");
                continue;
            }
            That(f.Session.SettleFormalDungeon(parsed!.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth).Outcome == DungeonRunOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(before,f.Read()),"已有合法ClearFact仍须等演出许可，提前46不提交结算");
            bool publication = scenario.Request == 3599 && !scenario.Finish;
            if (publication) Publish(f,quests,items,rooms,parameters,growth,fail:true).GetAwaiter().GetResult();
            var progress = f.Session.ChangeQuest(3599,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0));
            That(progress?.Outcome is QuestLifecycleOutcome.Committed or QuestLifecycleOutcome.Unchanged &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 3599).ConditionState == 0,
                "原接取终末33只保存真实演出达成条件");
            var enabled = f.Session.EnableFormalDungeonSettlement();
            That(enabled is { BossKey: null } && enabled.Value.Receipt.Snapshot.Identity == f.Session.DungeonSnapshot!.Identity,
                "原ClearFact及演出达成只许可空31，不编造115死亡key");
            if (publication) Publish(f,quests,items,rooms,parameters,growth,fail:false).GetAwaiter().GetResult();
            f.Session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
            That(f.Session.EnableFormalDungeonSettlement() is null,"成功发布确认后不重复许可31");
            if (scenario.Finish)
                That(f.Session.FinishQuest(3599,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Receipt is not null,
                    "允许33和31后先沿正常34清任务槽，再完成46");
            var settled = f.Session.SettleFormalDungeon(parsed.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth);
            That(settled.Outcome == DungeonRunOutcome.Accepted && settled.Receipt is not null && f.Session.FormalDungeonResultFocus() is not null,
                "演出许可后沿原结算事务完成真实奖励与结果页");
            var committed = f.Read();
            That(f.Session.SettleFormalDungeon(parsed.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth).Outcome == DungeonRunOutcome.Duplicate &&
                QuestCompletionRepositoryTests.Same(committed,f.Read()),"同一46精确重试不重复奖励");
            That(f.Session.ReturnFromFormalDungeon() is not null && !f.Session.CanPublishDungeonSettlement(enabled.Value.Receipt),
                "正式返回释放原结算实例，旧许可回执不可发布");
            That(opening.Entry.Maze.LayeredMaps.Single().Id == 76113,"完整路线保留真实叠层资源");
        }
        Console.WriteLine($"PASS {Count-checks} 3599真实叠层战斗、演出许可与结算回归（非实机）");
    }

    // 合成发送故障后保留同Session检查发布边界；不是TCP断线重连恢复声明。
    static async Task Publish(QuestSceneTests.Fixture f,QuestCatalog quests,ItemResources items,DungeonRoomCatalog rooms,
        QuestRewardParameterCatalog parameters,CharacterGrowthCatalog growth,bool fail)
    {
        string directory = Path.Combine(Path.GetTempPath(),"S5A9-movie-publication-"+Guid.NewGuid().ToString("N"));
        var before = f.Read();
        using (var log = new ServerLog(directory))
        using (var stop = new CancellationTokenSource())
        using (var output = new MovieOutput(fail))
        {
            await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
            var context = new GameConnectionContext(new(176,IPAddress.Loopback,output,stop),f.Owner,f.Session,sender);
            var commands = new GameCommandRegistry();
            new QuestConditionHandler(log,quests,items.Items,items.GoldLimits,items.EpicPieces,rooms,parameters,growth).Register(commands);
            commands.Seal();
            var packet = new Packet(1,33,[33,0,15,14,0,0]);
            if (fail)
            {
                bool failed = false;
                try { await commands.DispatchAsync(context,packet); }
                catch (IOException) { failed = true; }
                That(failed && stop.IsCancellationRequested && sender.PendingBytes == 0 && output.Frames.Count == 1 &&
                    output.Frames[0].AsSpan(16).SequenceEqual(new byte[] {1,15,14,0,0,0,0}) &&
                    f.Read().Revision == before.Revision+1 && f.Read().ActiveMainQuests.Single(row => row.QuestId == 3599).ConditionState == 0 &&
                    f.Session.EnableFormalDungeonSettlement() is not null,
                    "31发送失败传播取消且释放预算，已提交演出条件不回滚、许可尚未确认");
            }
            else
            {
                That(await commands.DispatchAsync(context,packet) == CommandResult.Handled && !stop.IsCancellationRequested &&
                    output.Frames.Select(frame => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1))).SequenceEqual(new ushort[] {33,31}) &&
                    output.Frames[1].Length == 16 && QuestCompletionRepositoryTests.Same(before,f.Read()) &&
                    f.Session.EnableFormalDungeonSettlement() is null,
                    "同Session健康发布者的Unchanged33补发空31，不重复提交演出进度");
                That(await commands.DispatchAsync(context,packet) == CommandResult.Handled && output.Frames.Count == 3 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(output.Frames[2].AsSpan(1)) == 33 &&
                    QuestCompletionRepositoryTests.Same(before,f.Read()),"成功确认31后重复33只回复ACK");
            }
        }
        Directory.Delete(directory,true);
    }

    sealed class MovieOutput(bool fail) : MemoryStream
    {
        public List<byte[]> Frames { get; } = [];
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken = default)
        {
            if (fail && BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[1..]) == 31)
                throw new IOException("合成演出31发送故障");
            Frames.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
