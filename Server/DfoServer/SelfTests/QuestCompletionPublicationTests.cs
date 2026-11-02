using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer;

internal static partial class QuestCompletionPublicationTests
{
    public static async Task RunStageAsync(TcpTestResources r)
    {
        int checks=TestCheck.Count;
        var growth=SelfTestCatalogs.Growth(r.Archive,r.Learning);
        var parameters=SelfTestCatalogs.QuestRewardParameters(r.Archive);
        var a=QuestEligibilityTests.Quest(100) with {SubType=1,Difficulty="G",Attributes=[],Rewards=[],Definition=new([]){Job="[all]"}};
        var c=a with {Id=101,Attributes=["not give exp quest"],Definition=a.Definition! with {PrerequisiteGroups=[[100]]}};
        var n=a with {Id=102,MinimumLevel=13,Definition=a.Definition! with {PrerequisiteGroups=[[100]]}};
        var catalog=new DfoServer.GameWorld.Quests.QuestCatalog(new[] {a,c,n}.ToDictionary(row=>row.Id));
        var graph=DfoServer.GameWorld.Quests.QuestStageGraph.Build(catalog);
        foreach (string mode in new[] {"normal","missing-growth","sql","lost","send","reselect","stale","owner"})
        {
            using var f=new QuestOrdinaryCompletionTests.Fixture(r.Towns,r.Characters,r.Learning,growth,parameters,r.ItemResources,a,
                materialCount:0,definitions:[a,c,n],active:[],loseResult:mode is "lost" or "stale",stages:graph,
                configure:candidate=>candidate with {Level=11,InitialLearning=DfoServer.Game.Skills.SkillPointLedger.Synchronize(
                    0,0,11,candidate.InitialLearning with {Experience=growth.GetLevelStartExperience(11)},r.Learning)});
            if(f.Lost is not null) f.Lost.CompletionLossCall=2;
            Check(f.Change(100,QuestLifecycleAction.Accept) is not null && f.Finish(ushort.MaxValue).Receipt is not null &&
                f.Change(101,QuestLifecycleAction.Accept) is not null,"阶段发布准备真实A延期/C接取");
            if(mode=="missing-growth")
            {
                // 仅在临时库模拟成长附属行缺失，重选后以真实载入状态交付延期阶段。
                f.Sql("DELETE FROM character_dungeon_state; UPDATE characters SET revision=revision+1");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            }
            var selected=f.Read();
            using var log=new ServerLog(Path.Combine(Path.GetDirectoryName(f.OpenPath)!,"log"));
            var projection=new QuestProjection(catalog);
            var registry=new GameCommandRegistry();
            new QuestCompletionHandler(catalog,parameters,r.ItemResources.Items,r.ItemResources.GoldLimits,r.ItemResources.EpicPieces,growth,
                log,new CharacterLevelProjection(new(r.Characters),projection),projection).Register(registry);
            registry.Seal();
            using var stop=new CancellationTokenSource();
            using var stream=new ControlledOutput {FailOn=mode=="send"?2:0,HoldOn=mode=="reselect"?1:0};
            await using var sender=new GameConnectionSender(f.Owner,stream,stop,0,(_,_,_,_,_)=>{});
            var context=new GameConnectionContext(new(88,IPAddress.Loopback,stream,stop),f.Owner,f.Session,sender);
            var packet=new Packet(1,34,Convert.FromHexString("22006500FFFF0100FFFF"));
            if(mode=="missing-growth")
            {
                var state=f.Repository.LoadQuestStageState(f.Owner,selected);
                var ordinary=QuestCompletionPlan.Prepare(selected,c,ushort.MaxValue,catalog,parameters,r.ItemResources.Items,
                    r.ItemResources.GoldLimits,r.ItemResources.EpicPieces,growth,DateTimeOffset.UtcNow).Plan!;
                Check(selected.DungeonState is null && state.Witnesses is [{Status:QuestStageStatus.Deferred,SourceQuestId:100,TargetLevel:13}] &&
                    selected.CompletedQuests.SequenceEqual([100]) && selected.ActiveMainQuests.Single().QuestId==101 &&
                    ordinary.TotalRequestedExperience==0 && selected.Learning.Experience<growth.GetLevelStartExperience(13),
                    "缺成长附属状态的C普通0，但真实A延期仍有正额阶段资格");
                string stored=StageStorageSnapshot(f);
                Check(await registry.DispatchAsync(context,packet)==CommandResult.Handled,
                    "普通0阶段正额缺成长附属状态时Handler正常拒绝，不在提交后抛错");
                ValidateRejection(stream.Frames,19);
                var rejected=f.Finish(ushort.MaxValue,101);
                Check(rejected.Outcome==QuestFinishOutcome.Rejected && rejected.Reason=="任务经验缺少现有成长附属状态" &&
                    rejected.Receipt is null && !stop.IsCancellationRequested,
                    "阶段正额复用普通成长前提，在规划阶段拒绝并保留原诊断");
                Check(QuestCompletionRepositoryTests.Same(selected,f.Read()) && QuestCompletionRepositoryTests.Same(selected,f.Session.Selected!) &&
                    StageStorageSnapshot(f)==stored && f.Receipts==1 &&
                    f.Repository.LoadQuestStageState(f.Owner,f.Read()).Witnesses.Single().Status==QuestStageStatus.Deferred,
                    "拒绝后完整角色/Selected/XP/revision/完成位/receipt/witness/claim不变，A延期不消费且无成功ACK或EXP");
                continue;
            }
            if(mode=="sql")
            {
                f.Sql("CREATE TRIGGER abort_stage_publication AFTER INSERT ON character_quest_stage_claims BEGIN SELECT RAISE(ABORT,'stage publication rollback'); END");
                await RejectAsync(typeof(SqliteException),()=>registry.DispatchAsync(context,packet),"阶段claim SQL失败不提前成功");
                Check(stream.Frames.Count==0 && QuestCompletionRepositoryTests.Same(selected,f.Read()),"阶段SQL失败双树资产完整回滚且无ACK");
                f.Sql("DROP TRIGGER abort_stage_publication");
            }
            if(mode is "lost" or "stale")
            {
                if(mode=="stale")
                {
                    await RejectAsync(typeof(IOException),()=>registry.DispatchAsync(context,packet),"阶段旧revision准备已提交未知结果");
                    f.Sql("UPDATE characters SET revision=revision+1");
                    var stale=f.Finish(ushort.MaxValue,101).Receipt!;
                    Check(!stale.Commit.ProjectionCurrent && !f.Session.CanPublishQuestCompletion(stale) && stream.Frames.Count==0,
                        "旧revision阶段pending重放不能发布或覆盖Selected");
                    continue;
                }
                await RejectAsync(typeof(IOException),()=>registry.DispatchAsync(context,packet),"阶段COMMIT返回丢失");
                Check(stream.Frames.Count==0 && QuestCompletionRepositoryTests.Same(selected,f.Session.Selected!) && f.Read().Level==13,"阶段丢返回无提前ACK，持久成长已提交");
            }
            if(mode=="owner")
            {
                var receipt=f.Session.FinishQuest(101,ushort.MaxValue,catalog,parameters,r.ItemResources.Items,r.ItemResources.GoldLimits,r.ItemResources.EpicPieces,growth).Receipt!;
                using var replacement=f.Owners.Acquire("ordinary-quest",()=>{});
                TestCheck.Reject<InvalidOperationException>(()=>f.Session.CanPublishQuestCompletion(receipt),"旧owner阶段receipt不能发布新会话");
                continue;
            }
            if(mode=="send")
            {
                await RejectAsync(typeof(IOException),()=>registry.DispatchAsync(context,packet),"阶段通知发送失败");
                Check(stream.Frames.Count==1 && f.Read().Level==13 && stop.IsCancellationRequested,"ACK后发送失败不回滚已提交阶段成长");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                Check(QuestCompletionRepositoryTests.Same(f.Session.Selected!,f.Read()),"发送失败重选恢复完整阶段双树资产");
                continue;
            }
            if(mode=="reselect")
            {
                var sending=registry.DispatchAsync(context,packet);
                try
                {
                    await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                }
                finally { stream.Release.TrySetResult(); }
                Check(await sending.WaitAsync(TimeSpan.FromSeconds(5)) == CommandResult.Handled && stream.Frames.Count==1 && !stop.IsCancellationRequested && sender.PendingBytes==0,
                    "阶段换角允许在途ACK完成并拦截旧epoch后续通知");
                Check(f.Read().Level==13 && QuestCompletionRepositoryTests.Same(f.Read(),f.Session.Selected!),"旧epoch不覆盖新选角已提交阶段状态");
                continue;
            }
            await registry.DispatchAsync(context,packet);
            var committed=f.Read();
            var notices=stream.Opcodes.ToArray();
            Check(notices.Contains((ushort)37),"普通XP为0、阶段正额仍必须发送NOTI37");
            var ack=stream.Frames.Single(frame=>frame[0]==1 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1))==34).AsSpan(16).ToArray();
            var exp=stream.Frames.Single(frame=>BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1))==37).AsSpan(16).ToArray();
            uint total=checked((uint)(committed.Learning.Experience-selected.Learning.Experience));
            Check(ack[0]==1 && BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4))==total && total>0 &&
                exp.Length==91 && exp[0]==13 && BinaryPrimitives.ReadUInt64LittleEndian(exp.AsSpan(1))==growth.GetLevelStartExperience(13) &&
                BinaryPrimitives.ReadUInt32LittleEndian(exp.AsSpan(9))==0 && BinaryPrimitives.ReadUInt32LittleEndian(exp.AsSpan(34))==0,
                "独立解码ACK34实际总额/NOTI37绝对XP，普通0阶段不伪造组队或契约分量");
            Check(Array.IndexOf(notices,(ushort)34)<Array.IndexOf(notices,(ushort)37) && Array.IndexOf(notices,(ushort)37)<Array.IndexOf(notices,(ushort)2) &&
                QuestCompletionRepositoryTests.Same(committed,f.Session.Selected!),"ACK→EXP→升级Player沿已提交双树状态发布");
            stream.Frames.Clear(); await registry.DispatchAsync(context,packet);
            ValidateRejection(stream.Frames,19);
            Check(QuestCompletionRepositoryTests.Same(committed,f.Read()) && f.Receipts==2,"阶段重复请求不二次成长或建receipt");
        }
        await UnknownStageOrdinaryAsync();
        Console.WriteLine($"PASS {TestCheck.Count-checks} 阶段Handler独立wire及发布故障恢复断言（非监听）");

        static string StageStorageSnapshot(QuestOrdinaryCompletionTests.Fixture fixture)
        {
            using var db=fixture.Open();
            var tables=new List<List<object[]>>();
            foreach(string table in new[] {"character_quest_completion_receipts","character_quest_stage_witnesses","character_quest_stage_claims"})
            {
                using var command=db.CreateCommand();
                command.CommandText=$"SELECT * FROM {table} ORDER BY rowid";
                using var reader=command.ExecuteReader();
                var rows=new List<object[]>();
                while(reader.Read())
                {
                    var values=new object[reader.FieldCount];
                    reader.GetValues(values); rows.Add(values);
                }
                tables.Add(rows);
            }
            return JsonSerializer.Serialize(tables);
        }

        async Task UnknownStageOrdinaryAsync()
        {
            var unknown=n with {Condition="[unknown]"};
            var unknownCatalog=new DfoServer.GameWorld.Quests.QuestCatalog(new[] {a,unknown}.ToDictionary(row=>row.Id));
            var unknownGraph=DfoServer.GameWorld.Quests.QuestStageGraph.Build(unknownCatalog);
            using var f=new QuestOrdinaryCompletionTests.Fixture(r.Towns,r.Characters,r.Learning,growth,parameters,r.ItemResources,a,
                materialCount:0,definitions:[a,unknown],active:[],stages:unknownGraph,
                configure:candidate=>candidate with {Level=11,InitialLearning=DfoServer.Game.Skills.SkillPointLedger.Synchronize(
                    0,0,11,candidate.InitialLearning with {Experience=growth.GetLevelStartExperience(11)},r.Learning)});
            Check(f.Change(100,QuestLifecycleAction.Accept)?.Commit is not null,"未知后继不阻止合法普通源真实接取");
            var before=f.Read();
            var ordinary=f.Prepare(ushort.MaxValue).Plan!;
            Check(ordinary.OrdinaryExperience>0 && ordinary.ContractExperience==0 && ordinary.TargetLevel==before.Level,
                "未知阶段对照为无契约、未跨级的合法普通奖励计划");
            using var log=new ServerLog(Path.Combine(Path.GetDirectoryName(f.OpenPath)!,"log"));
            var projection=new QuestProjection(unknownCatalog);
            var registry=new GameCommandRegistry();
            new QuestCompletionHandler(unknownCatalog,parameters,r.ItemResources.Items,r.ItemResources.GoldLimits,r.ItemResources.EpicPieces,growth,
                log,new CharacterLevelProjection(new(r.Characters),projection),projection).Register(registry);
            registry.Seal();
            using var stop=new CancellationTokenSource();
            using var stream=new ControlledOutput();
            await using var sender=new GameConnectionSender(f.Owner,stream,stop,0,(_,_,_,_,_)=>{});
            var context=new GameConnectionContext(new(89,IPAddress.Loopback,stream,stop),f.Owner,f.Session,sender);
            Check(await registry.DispatchAsync(context,new Packet(1,34,Convert.FromHexString("22006400FFFF0100FFFF")))==CommandResult.Handled,
                "相关frontier未知时真实Handler仍正常处理普通34");
            var saved=f.Read();
            var expected=before with {Revision=before.Revision+1,Learning=ordinary.TargetLearning,ActiveMainQuests=[],CompletedQuests=[100],
                Skills=ordinary.TargetLearning.Trees.Single(tree=>tree.Context==0).Skills
                    .Select(skill=>new CharacterSkill(skill.Id,skill.Level)).OrderBy(skill=>skill.Id).ToArray()};
            Check(QuestCompletionRepositoryTests.Same(expected,saved) && QuestCompletionRepositoryTests.Same(saved,f.Session.Selected!) && f.Receipts==1,
                "未知阶段普通奖励、完成位与单次revision完整持久化，Selected双树资产一致");
            using(var db=f.Open())
            using(var command=db.CreateCommand())
            {
                command.CommandText="SELECT acceptance_id,experience_amount,plan_snapshot FROM character_quest_completion_receipts WHERE quest_id=100";
                using(var reader=command.ExecuteReader())
                {
                    Check(reader.Read() && reader.GetString(0)==before.ActiveMainQuests.Single().AcceptanceId.ToString("N") &&
                        reader.GetInt64(1)==ordinary.OrdinaryExperience,"未知阶段普通receipt对应本次真实接取与实际普通到账");
                    using var snapshot=JsonDocument.Parse(reader.GetString(2));
                    var stage=snapshot.RootElement.GetProperty("Stage");
                    var plan=stage.GetProperty("StagePlan");
                    Check(stage.GetProperty("OrdinaryExperience").GetUInt32()==ordinary.OrdinaryExperience &&
                        stage.GetProperty("ContractExperience").GetUInt32()==0 && stage.GetProperty("StageExperience").GetUInt32()==0 &&
                        stage.GetProperty("TotalRequestedExperience").GetUInt32()==ordinary.OrdinaryExperience &&
                        stage.GetProperty("ActualExperience").GetUInt32()==ordinary.OrdinaryExperience &&
                        plan.GetProperty("Reason").GetString()=="stage-unknown-definition" && plan.GetProperty("Claim").ValueKind==JsonValueKind.Null &&
                        plan.GetProperty("WitnessesAfter").GetArrayLength()==0 && !reader.Read(),
                        "真实冻结receipt明确unknown，阶段0且不创建资格或claim，普通分项照旧");
                }
                command.CommandText="SELECT (SELECT count(*) FROM character_quest_stage_witnesses)+(SELECT count(*) FROM character_quest_stage_claims)";
                Check((long)command.ExecuteScalar()! == 0,"未知阶段普通成功提交后阶段两表均无额外行");
            }
            var ack=stream.Frames.Single(frame=>frame[0]==1 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1))==34).AsSpan(16).ToArray();
            var exp=stream.Frames.Single(frame=>frame[0]==0 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1))==37).AsSpan(16).ToArray();
            var completed=stream.Frames.Single(frame=>BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1))==356).AsSpan(16).ToArray();
            Check(ack.Length==10 && ack[0]==1 && BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(1))==100 && ack[3]==4 &&
                BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4))==ordinary.OrdinaryExperience && ack[8]==0 && ack[9]==0 &&
                exp.Length==91 && exp[0]==before.Level && BinaryPrimitives.ReadUInt64LittleEndian(exp.AsSpan(1))==before.Learning.Experience+ordinary.OrdinaryExperience &&
                BinaryPrimitives.ReadUInt32LittleEndian(exp.AsSpan(9))==0 && BinaryPrimitives.ReadUInt32LittleEndian(exp.AsSpan(34))==0 &&
                completed.Length==30004 && completed[104]==1 && stream.Opcodes.SequenceEqual(new ushort[] {34,37,21,356}),
                "未知阶段独立解码普通ACK/EXP及356完成位，保留34→37→21→356原通知序列");
        }
    }

    public static async Task RunAsync(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items)
    {
        await RunRewardSlotsAsync(towns, pvf, characters, items);
        int before = TestCheck.Count;
        var computer = new CharacterStatComputer(characters);
        var resources = new QuestCompletionRepositoryTests.Resources(pvf, items);
        foreach (var identity in new (byte Job, byte Grow, int Weapon)[] { (15, 0, 116000005), (14, 0, 108000198), (0, 0x21, 101010438) })
            await ClassClearAndAwardAsync(identity.Job, identity.Grow, identity.Weapon);
        await StorageFailureAsync(false);
        await StorageFailureAsync(true);
        foreach (int packet in new[] { 1, 2, 3, 4, 5, 6 })
            await SendFailureAsync(packet);
        foreach (int packet in new[] { 1, 2, 3, 4, 5, 6 })
            await ReselectDuringSendAsync(packet);
        await PendingReselectAsync();
        await FullBagAsync();
        Console.WriteLine($"PASS {TestCheck.Count - before} 3145真实Handler完整包序/结果不明/失败拒绝/重选隔离断言（合成战斗，非实机）");

        GameCommandRegistry Registry(QuestCompletionRepositoryTests.Fixture fixture)
        {
            var commands = new GameCommandRegistry();
            var quests = new QuestProjection(resources.Quests);
            var levels = new CharacterLevelProjection(computer, quests);
            new QuestConditionHandler(fixture.Log).Register(commands);
            new QuestCompletionHandler(resources.Quests, resources.Parameters, items.Items, items.GoldLimits,
                items.EpicPieces, resources.Growth, fixture.Log, levels, quests).Register(commands);
            new DungeonHandler(fixture.Log, resources.Dungeons, resources.Rooms, resources.Growth, resources.Kills,
                new TownProjection(towns, computer, null, items.EpicPieces, quests: quests), levels).Register(commands);
            commands.Seal();
            return commands;
        }

        async Task ClassClearAndAwardAsync(byte job, byte growType, int weapon)
        {
            using var fixture = new QuestCompletionRepositoryTests.Fixture(towns, characters, resources, job: job, growType: growType);
            var selected = fixture.Session.Selected!;
            var commands = Registry(fixture);
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput();
            await using var sender = new GameConnectionSender(fixture.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = Context(fixture, stream, sender, stop);
            Check(selected.Job == job && selected.GrowType == growType && selected.ActiveMainQuests.Single().ConditionState == 0 &&
                fixture.Scalar("SELECT count(*) FROM character_quest_clear_receipts") == 1,
                "全职业/转职真实六房清图事务生成唯一任务凭据，条件由1归0");
            long beforeTrigger = fixture.Scalar("SELECT revision FROM characters");
            Check(await commands.DispatchAsync(context, new Packet(1, 33, Convert.FromHexString("2100490C0000"))) == CommandResult.Handled &&
                stream.Frames.Count == 1 && stream.Frames[0].AsSpan(16).SequenceEqual(Convert.FromHexString("01490C00000000")) &&
                fixture.Scalar("SELECT revision FROM characters") == beforeTrigger && fixture.Count() == 0,
                "清图请求只回复完整ACK33，不再次写条件或发任务奖励");
            stream.Frames.Clear();
            Check(await commands.DispatchAsync(context, FinishPacket()) == CommandResult.Handled,
                "全职业/转职完成请求沿真实Handler提交全部任务奖励");
            var committed = fixture.Read();
            ValidateSuccess(stream.Frames, committed, selected, weapon, computer.Compute(committed));
            Check(fixture.Count() == 1 && committed.CompletedQuests.Contains(3145) && committed.ActiveMainQuests.Length == 0 &&
                committed.Inventory.Items.Any(item => item.Core.ItemId == weapon) &&
                committed.Learning.Experience == selected.Learning.Experience + 1780 && committed.Revision == selected.Revision + 1,
                "实际职业武器、七项奖励、成长、清槽与完成位只提交一次");
            long revision = committed.Revision;
            int itemCount = committed.Inventory.Items.Length;
            stream.Frames.Clear();
            Check(await commands.DispatchAsync(context, FinishPacket()) == CommandResult.Handled &&
                stream.Frames.Count == 1 && stream.Frames[0].AsSpan(16).SequenceEqual(Convert.FromHexString("001300")) &&
                fixture.Read().Revision == revision && fixture.Read().Inventory.Items.Length == itemCount && fixture.Count() == 1,
                "全职业重复领奖返回静默拒绝，不重发经验/物品或改变revision");
            stream.Frames.Clear();
            Check(await commands.DispatchAsync(context, new Packet(1, 42, [])) == CommandResult.Handled &&
                stream.Opcodes.SequenceEqual(new ushort[] { 42, 3, 24, 21, 356 }) && fixture.Session.Phase == SessionPhase.Town &&
                stream.Frames[^1][20 + 3145] == 1,
                "副本领奖回城后按21→356恢复实际完成历史");
            var available = QuestLifecycleTcpTests.List(stream.Frames[3][16..], committed.Level);
            Check(available.Contains(4873) && !available.Contains(3145) && fixture.Read().Revision == revision,
                "同一选角和连接领奖回城后直接看见后续4873，刷新不重复提交或发奖");
        }

        async Task StorageFailureAsync(bool lostResult)
        {
            using var fixture = new QuestCompletionRepositoryTests.Fixture(towns, characters, resources, lostResult);
            var selected = fixture.Session.Selected!;
            var commands = Registry(fixture);
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput();
            await using var sender = new GameConnectionSender(fixture.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = Context(fixture, stream, sender, stop);
            if (!lostResult)
                fixture.Sql("CREATE TRIGGER publication_quest_fail AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'quest publication rollback'); END");
            await RejectAsync(lostResult ? typeof(IOException) : typeof(SqliteException),
                () => commands.DispatchAsync(context, FinishPacket()), "真实领奖存储故障不发通用成功");
            Check(stream.Frames.Count == 0 && ReferenceEquals(fixture.Session.Selected, selected) && !stop.IsCancellationRequested,
                "领奖存储结果返回前保持原角色与客户端库存，不提前ACK");
            Check(fixture.Session.ReturnFromFormalDungeon() is null, "未知/待提交领奖不能退出原副本丢失冻结计划");
            if (lostResult)
                Check(fixture.Count() == 1 && fixture.Read().CompletedQuests.Contains(3145) &&
                    fixture.Read().Learning.Experience == selected.Learning.Experience + 1780 && fixture.LostResult!.Calls == 1,
                    "真实SQL已提交但返回值丢失保留receipt及全部资产成长");
            else
            {
                Check(fixture.Count() == 0 && QuestCompletionRepositoryTests.Same(selected, fixture.Read()),
                    "晚期SQL回滚清槽/完成/成长/七件资产");
                fixture.Sql("DROP TRIGGER publication_quest_fail");
            }
            Check(await commands.DispatchAsync(context, FinishPacket()) == CommandResult.Handled, "原冻结领奖计划精确恢复或回滚重试");
            var committed = fixture.Read();
            Check(fixture.Count() == 1 && committed.Revision == selected.Revision + 1 &&
                QuestCompletionRepositoryTests.Same(committed, fixture.Session.Selected!) &&
                committed.Learning.Experience == selected.Learning.Experience + 1780,
                "恢复只安装实际新角色，不重复EXP或revision");
            ValidateSuccess(stream.Frames, committed, selected);
            if (lostResult)
                Check(fixture.LostResult!.Calls == 2 && fixture.LostResult.SamePlan && fixture.LostResult.LastDuplicate,
                    "返回值丢失在原未安装selected下复用同一plan并命中receipt");
            int beforeRepeat = stream.Frames.Count;
            Check(await commands.DispatchAsync(context, FinishPacket()) == CommandResult.Handled && fixture.Count() == 1,
                "已安装后重复完成有匹配拒绝且连接可继续处理");
            ValidateRejection(stream.Frames.Skip(beforeRepeat).ToArray(), 19);
            fixture.Session.ReturnToRoster();
            fixture.Session.Select(fixture.Created.Slot);
            fixture.Session.MarkReady();
            Check(QuestCompletionRepositoryTests.Same(fixture.Read(), fixture.Session.Selected!), "重选已从库恢复完整七项及完成表");
            int beforeReselectRepeat = stream.Frames.Count;
            await commands.DispatchAsync(context, FinishPacket());
            ValidateRejection(stream.Frames.Skip(beforeReselectRepeat).ToArray(), 19);
            Check(fixture.Count() == 1 && fixture.Read().Learning.Experience == committed.Learning.Experience && !stop.IsCancellationRequested,
                "恢复完整库存后的重复不重放增量ACK或重复给EXP");
            Check(await commands.DispatchAsync(context, new Packet(1, 34, new byte[9])) == CommandResult.Unhandled,
                "畸形未支持请求保持未处理，不伪成功或中断连接");
        }

        async Task SendFailureAsync(int packet)
        {
            using var fixture = new QuestCompletionRepositoryTests.Fixture(towns, characters, resources);
            var selected = fixture.Session.Selected!;
            var commands = Registry(fixture);
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput { FailOn = packet };
            await using var sender = new GameConnectionSender(fixture.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = Context(fixture, stream, sender, stop);
            await RejectAsync(typeof(IOException), () => commands.DispatchAsync(context, FinishPacket()),
                "ACK34/NOTI14/NOTI37写入故障传播 " + packet);
            var committed = fixture.Read();
            Check(stop.IsCancellationRequested && fixture.Count() == 1 && stream.Frames.Count == packet - 1 &&
                sender.PendingBytes == 0 && committed.Learning.Experience == selected.Learning.Experience + 1780 &&
                QuestCompletionRepositoryTests.Same(committed, fixture.Session.Selected!),
                "通知失败保留全部已提交奖励，不回滚/重复或留发送预算 " + packet);
            fixture.Session.ReturnToRoster();
            fixture.Session.Select(fixture.Created.Slot);
            fixture.Session.MarkReady();
            var restored = fixture.Read();
            Check(QuestCompletionRepositoryTests.Same(committed with { Revision = restored.Revision,
                Position = restored.Position, Return = restored.Return }, restored),
                "发送故障后重选仅允许正常落点保存，保留全部领奖成长、资产和任务 " + packet);
            using var newStop = new CancellationTokenSource();
            using var reconnected = new ControlledOutput();
            await using var newSender = new GameConnectionSender(fixture.Owner, reconnected, newStop, 0, (_, _, _, _, _) => { });
            await commands.DispatchAsync(Context(fixture, reconnected, newSender, newStop), FinishPacket());
            ValidateRejection(reconnected.Frames, 19);
            Check(fixture.Count() == 1 && QuestCompletionRepositoryTests.Same(restored, fixture.Read()),
                "新投影已含完整库存，不能重发失败时的incremental成功 " + packet);
        }

        async Task ReselectDuringSendAsync(int packet)
        {
            using var fixture = new QuestCompletionRepositoryTests.Fixture(towns, characters, resources);
            var selected = fixture.Session.Selected!;
            var commands = Registry(fixture);
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput { HoldOn = packet };
            await using var sender = new GameConnectionSender(fixture.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = Context(fixture, stream, sender, stop);
            var publication = commands.DispatchAsync(context, FinishPacket());
            try
            {
                await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check(!publication.IsCompleted && fixture.Count() == 1 && fixture.Read().Learning.Experience == selected.Learning.Experience + 1780,
                    "网络等待前奖励已完整提交且owner锁可供重选 " + packet);
                fixture.Session.ReturnToRoster();
                fixture.Session.Select(fixture.Created.Slot);
                fixture.Session.MarkReady();
            }
            finally
            {
                stream.Release.TrySetResult();
            }
            Check(await publication.WaitAsync(TimeSpan.FromSeconds(5)) == CommandResult.Handled && stream.Frames.Count == packet &&
                stream.Opcodes.SequenceEqual(new ushort[] { 34, 14, 37, 2, 21, 356 }.Take(packet)) && !stop.IsCancellationRequested && sender.PendingBytes == 0,
                "允许已在途帧完成，拦截旧epoch后续奖励通知 " + packet);
            Check(fixture.Session.Phase == SessionPhase.Town && fixture.Count() == 1 &&
                QuestCompletionRepositoryTests.Same(fixture.Session.Selected!, fixture.Read()),
                "迟到领奖发布不覆盖新选角或再次授奖 " + packet);
        }

        async Task PendingReselectAsync()
        {
            using var fixture = new QuestCompletionRepositoryTests.Fixture(towns, characters, resources, true);
            QuestCompletionRepositoryTests.Reject<IOException>(() => fixture.Finish(), "已提交未知返回用于pending选角失效测试");
            var persisted = fixture.Read();
            fixture.Session.ReturnToRoster();
            fixture.Session.Select(fixture.Created.Slot);
            fixture.Session.MarkReady();
            Check(fixture.Finish().Outcome == QuestFinishOutcome.AlreadyCompleted && fixture.LostResult!.Calls == 1 && fixture.Count() == 1 &&
                QuestCompletionRepositoryTests.Same(persisted with { Revision = fixture.Session.Selected!.Revision,
                    Position = fixture.Session.Selected.Position, Return = fixture.Session.Selected.Return }, fixture.Session.Selected),
                "新epoch清理旧pending而不复制旧plan或重跑incremental提交");
            await Task.CompletedTask;
        }

        async Task FullBagAsync()
        {
            using var fixture = new QuestCompletionRepositoryTests.Fixture(towns, characters, resources);
            var expected = fixture.Session.Selected!;
            var range = ItemSlotBoundService.Range(DfoServer.GameWorld.Items.ItemKind.Equipment, expected.Inventory.Expansion);
            var reward = Enumerable.Repeat(new InventoryReward(101010438, 1), range.End - range.Start + 1).ToArray();
            var grant = InventoryRewardGrantService.TryGrantBatch(expected, reward, items.Items, items.GoldLimits, items.EpicPieces);
            Check(grant.Accepted && grant.Plan is not null, "满包fixture通过真实共享grant填满合法装备页");
            var full = fixture.Repository.CommitInventory(fixture.Owner, expected, grant.Plan!);
            fixture.Session.ReturnToRoster();
            fixture.Session.Select(fixture.Created.Slot);
            fixture.Session.MarkReady();
            var restoredFull = fixture.Read();
            Check(QuestCompletionRepositoryTests.Same(full with { Revision = restoredFull.Revision,
                Position = restoredFull.Position, Return = restoredFull.Return }, restoredFull), "满页重选只归一落点，不改变资产与任务");
            var commands = Registry(fixture);
            using var stop = new CancellationTokenSource();
            using var stream = new ControlledOutput();
            await using var sender = new GameConnectionSender(fixture.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            Check(await commands.DispatchAsync(Context(fixture, stream, sender, stop), FinishPacket()) == CommandResult.Handled,
                "满包业务拒绝由已证ACK34失败处理而非关闭连接");
            ValidateRejection(stream.Frames, 4);
            Check(!stop.IsCancellationRequested && fixture.Count() == 0 && QuestCompletionRepositoryTests.Same(restoredFull, fixture.Read()) &&
                fixture.Session.Selected!.ActiveMainQuests.Single(active => active.QuestId == 3145).ConditionState == 0,
                "满包4保持完整原资产/EXP/活动任务，不给前半奖励或完成位");
        }
    }

    static Packet FinishPacket() => new(1, 34, Convert.FromHexString("2200490CFFFF0100FFFF"));
    static GameConnectionContext Context(QuestCompletionRepositoryTests.Fixture fixture, Stream stream,
        GameConnectionSender sender, CancellationTokenSource stop) =>
        new(new(88, IPAddress.Loopback, stream, stop), fixture.Owner, fixture.Session, sender);
    static void Check(bool value, string name) => TestCheck.That(value, name);
    static void ValidateSuccess(IReadOnlyList<byte[]> frames, Character committed, Character before, int weapon = 101010438,
        CharacterStats? expectedStats = null)
    {
        Check(frames.Count == 6 && frames.Select(frame => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)))
            .SequenceEqual(new ushort[] { 34, 14, 37, 2, 21, 356 }) && frames[0][0] == 1 && frames.Skip(1).All(frame => frame[0] == 0),
            "结算阶段成功完整包序ACK34→14最终Core→37绝对成长→实际升级Player→任务列表→356完成记录");
        Check(frames[5].AsSpan(16).SequenceEqual(TownPackets.CompletedQuests(committed)) && frames[5][20 + 3145] != 0,
            "普通交付也发布实际完成记录，任务面板无须重登刷新");
        var available = QuestLifecycleTcpTests.List(frames[4][16..], committed.Level);
        Check(available.Contains(4873) && !available.Contains(3145), "结算领奖后立即发布后继，不等回城或重新选角");
        using var ack = new BinaryReader(new MemoryStream(frames[0], 16, frames[0].Length - 16));
        Check(frames[0].Length == 159 && ack.ReadByte() == 1 && ack.ReadUInt16() == 3145 && ack.ReadByte() == 3 &&
            ack.ReadUInt32() == 1780 && ack.ReadByte() == 0 && ack.ReadByte() == 7, "真实Handler发送精确143B已提交EXP/模式头");
        int[] ids = [10165055, weapon, 10403, 12403, 14403, 16403, 18403];
        foreach (int id in ids)
        {
            ushort slot = ack.ReadUInt16();
            uint template = ack.ReadUInt32();
            uint value = ack.ReadUInt32();
            byte amplifyType = ack.ReadByte();
            ushort amplifyValue = ack.ReadUInt16();
            uint expires = ack.ReadUInt32();
            byte sortLock = ack.ReadByte();
            byte remainingUses = ack.ReadByte();
            var actual = committed.Inventory.Find(new(InventoryListType.Main, checked((short)slot)))!;
            Check(template == id && actual.Uid > 0 && actual.Core.ItemId == id &&
                value == (uint)(actual.Core.IsStackable ? 1 : actual.Core.Value) && amplifyType == actual.Core.AmplifyType &&
                amplifyValue == actual.Core.AmplifyValue && expires == unchecked((uint)actual.Core.ExpireTime) &&
                sortLock == actual.Core.SortLockFlag && remainingUses == actual.Core.RemainUseCount,
                "ACK34每项实际UID目的槽/增量/Core原序 " + id);
        }
        Check(ack.BaseStream.Position == ack.BaseStream.Length, "143B没有额外虚构奖励尾部");
        var awarded = committed.Inventory.Items.Where(item => before.Inventory.Find(item.Position) is null).Select(item => item.Position).ToArray();
        Check(frames[1].AsSpan(16).SequenceEqual(InventoryPackets.Delta(committed.Inventory, InventoryListType.Main, awarded)),
            "NOTI14完整最终Core来自实际提交后的七项槽");
        using var growth = new BinaryReader(new MemoryStream(frames[2], 16, frames[2].Length - 16));
        var first = committed.Learning.Trees.Single(tree => tree.Context == 0);
        var second = committed.Learning.Trees.Single(tree => tree.Context == 1);
        var auxiliary = committed.DungeonState!;
        Check(frames[2].Length == 107 && growth.ReadByte() == committed.Level && growth.ReadUInt64() == committed.Learning.Experience &&
            growth.ReadUInt32() == 0 && growth.ReadUInt16() == first.Sp && growth.ReadUInt16() == second.Sp &&
            growth.ReadUInt16() == first.Tp && growth.ReadUInt16() == second.Tp &&
            growth.ReadUInt32() == committed.Inventory.WinPoints && growth.ReadUInt32() == 0 && growth.ReadByte() == 0,
            "独立NOTI37读器核对91B等级/绝对EXP/普通事件无队伍加成/两树SP和TP");
        Check(growth.ReadUInt32() == 0 && growth.ReadUInt32() == 0 && growth.ReadUInt32() == auxiliary.GrowthCapsuleExperience &&
            growth.ReadUInt32() == 0 && growth.ReadUInt32() == 0 && growth.ReadByte() == 0 &&
            growth.ReadUInt32() == 0 && growth.ReadUInt32() == 0 && growth.ReadUInt32() == auxiliary.ProficiencyLevel &&
            growth.ReadUInt64() == auxiliary.ProficiencyExperience,
            "任务EXP通知保留实际附属owner，不用金额填未知分量");
        Check(Enumerable.Range(0, 5).All(_ => growth.ReadUInt32() == 0) && growth.BaseStream.Position == growth.BaseStream.Length &&
            committed.Learning.Experience == before.Learning.Experience + 1780,
            "完整91B尾部不截断，不追加第二份经验奖励");
        var player = frames[3].AsSpan(16);
        Check(player[0] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(player[22..]) == committed.Id &&
            BinaryPrimitives.ReadUInt64LittleEndian(player[24..]) == committed.Learning.Experience &&
            BinaryPrimitives.ReadUInt32LittleEndian(player[32..]) == 89 &&
            (expectedStats is null || BinaryPrimitives.ReadUInt32LittleEndian(player[36..]) == expectedStats.MaxHp) &&
            BinaryPrimitives.ReadUInt32LittleEndian(player[40..]) == (expectedStats?.MaxMp ?? 2150) &&
            BinaryPrimitives.ReadInt16LittleEndian(player[44..]) == (expectedStats?.PhysicalAttack ?? 219),
            "Player当前Lv4 CHR基础/完整技能装备刷新，不用旧Lv1或叠加最终装备属性");
    }
    static void ValidateRejection(IReadOnlyList<byte[]> frames, ushort error) =>
        Check(frames.Count == 1 && frames[0][0] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(frames[0].AsSpan(1)) == 34 &&
            frames[0].Length == 19 && frames[0][16] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(frames[0].AsSpan(17)) == error,
            "匹配ACK34公共0+u16拒绝且无重复增量/尾部 " + error);
    static async Task RejectAsync(Type expected, Func<Task> action, string name)
    {
        try
        {
            await action().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) when (expected.IsInstanceOfType(error))
        {
            Check(true, name);
            return;
        }
        throw new InvalidOperationException("FAIL 未遇到预期领奖发布故障: " + name);
    }
    sealed class ControlledOutput : MemoryStream
    {
        int writes;
        public int FailOn { get; init; }
        public int HoldOn { get; init; }
        public List<byte[]> Frames { get; } = [];
        public IEnumerable<ushort> Opcodes => Frames.Select(frame => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)));
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int number = ++writes;
            if (number == HoldOn)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            if (number == FailOn)
                throw new IOException("合成领奖通知发送故障");
            Frames.Add(buffer.ToArray());
        }
    }
}
