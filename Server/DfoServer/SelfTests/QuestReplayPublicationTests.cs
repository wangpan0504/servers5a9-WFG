using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestReplayPublicationTests
{
    // 成功准备复用单房Prepared夹具；1459真实Handler测拒绝，15/16/37及取消调用真实Handler/仓储。
    public static async Task RunAsync(PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        SkillLearningCatalog? sharedSkills = null)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(),"S5A9-quest-replay-publication-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var log = new ServerLog(root);
            var computer = new CharacterStatComputer(characters);
            var growth = CharacterGrowthCatalog.Load(pvf,sharedSkills ?? global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf));
            var kills = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf);
            var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf); var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
            await LoadingAsync();
            await UnauthorizedAsync();
            await CancelAsync();
            await CancelLoadingAsync();
            await ReselectDuringSendAsync();
            await ReselectRejectedEntryAsync();
            await StaleOwnerAsync();
            await CancelContinuationAsync();

            GameCommandRegistry Registry(QuestCycleContinuationTests.Fixture fixture)
            {
                var projection = new TownProjection(fixture.Towns,computer,null,items.EpicPieces);
                var commands = new GameCommandRegistry();
                new QuestReplayHandler(QuestReplayTests.Connections((101,0)),dungeons,rooms,log).Register(commands);
                new DungeonSelectionHandler(projection,log).Register(commands);
                new DungeonHandler(log,dungeons,rooms,growth,kills,projection,new CharacterLevelProjection(computer)).Register(commands);
                commands.Seal(); return commands;
            }

            async Task LoadingAsync()
            {
                var now = DateTimeOffset.UtcNow; var after = NextCycle(now);
                using var f = new QuestCycleContinuationTests.Fixture(now,forReplay:true);
                var before = f.Read(); f.PrepareContinuation(forReplay:true); var commands = Registry(f);
                using var stop = new CancellationTokenSource(); using var output = new ControlledOutput();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop);
                That(await commands.DispatchAsync(context,Entry(2)) == CommandResult.Handled &&
                    Frames(output,(1,15)) && Body(output.Frames.Single()).SequenceEqual(DungeonPackets.SelectionUnavailable()) &&
                    f.Session.Phase == SessionPhase.Town && f.Session.CurrentDungeonSelection is null && Same(before,f.Read()),
                    "回放15错误副本只拒绝本次入口，不建立选图或预留身份");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Entry(1)) == CommandResult.Handled && Frames(output,(1,15),(0,3)) &&
                    Body(output.Frames[0]).SequenceEqual<byte>([1]) &&
                    Body(output.Frames[1]).SequenceEqual(DungeonPackets.DungeonPlayerState(before.Id)) &&
                    f.Session.Phase == SessionPhase.MapSelect && f.Session.CurrentDungeonSelection!.ReturnPosition == before.Position &&
                    Same(before,f.Read()),"真实回放15仅发布ACK15与state1，保留当前回城锚点且不写任务/副本身份");

                foreach (var request in new[] { Selection(dungeon:2),Selection(quest:102),Selection(difficulty:1),
                    Selection(unknown5:1),Selection(unknown11:1) })
                {
                    output.Frames.Clear();
                    That(await commands.DispatchAsync(context,request) == CommandResult.Handled && Frames(output,(1,16)) &&
                        Body(output.Frames.Single()).SequenceEqual(DungeonPackets.SelectionRejected()) &&
                        f.Session.Phase == SessionPhase.MapSelect && f.Session.DungeonSnapshot is null && Same(before,f.Read()),
                        "回放16复验准备的副本/qid/难度与已证字段，不成功投影或预留错误实例");
                }
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Selection()) == CommandResult.Handled && Frames(output,(0,28),(0,29)),
                    "真实回放16接受不作为资格的Scene附加值，按顺序发布副本与地图初始化");
                var entered = f.Session.BeginQuestReplayDungeonSelection(1).Replay!;
                var reserved = f.Read(); var identity = f.Session.DungeonSnapshot!.Identity;
                That(entered is { Entry.IsQuestReplay:true,Entry.QuestId:0,Entry.RouteQuestId:101 } &&
                    f.Session.Phase == SessionPhase.Loading && reserved.Revision == before.Revision+1 &&
                    reserved.DungeonState!.LastAttempt == before.DungeonState!.LastAttempt+1 &&
                    reserved.DungeonState!.NextObjectKey > before.DungeonState!.NextObjectKey &&
                    Same(before with { Revision=reserved.Revision,DungeonState=reserved.DungeonState },reserved) &&
                    Same(reserved,f.Session.Selected!),
                    "回放入场只提交一次真实attempt/key预留，保留原接取、完成、库存与成长快照");
                That(Body(output.Frames[0]).Length == 36 && BinaryPrimitives.ReadUInt32LittleEndian(Body(output.Frames[0])) == 1 &&
                    Body(output.Frames[0])[4] == 0 && Body(output.Frames[0])[7] == 0 &&
                    Body(output.Frames[1]).Length == 45 && Body(output.Frames[1])[18] == 1 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(Body(output.Frames[1])[14..]) == 1 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(Body(output.Frames[1])[25..]) == entered.Monsters.Monsters.Single().ObjectKey,
                    "28/29投影合成原剧情迷宫与实际已预留怪物key，不打开第二份选图界面");
                That(f.Session.AdvanceQuestCycle(after) is null && Same(reserved,f.Read()),
                    "回放等待真实37期间跨周期不替换安装快照");

                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Entry(1)) == CommandResult.Handled && Frames(output,(1,15)) &&
                    Body(output.Frames.Single()).SequenceEqual<byte>([1]) && f.Session.DungeonSnapshot!.Identity == identity &&
                    ReferenceEquals(f.Session.BeginQuestReplayDungeonSelection(1).Replay,entered) && Same(reserved,f.Read()),
                    "Loading重复15仅重发ACK，沿原初始化且不重复28/29或预留attempt/key");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Selection()) == CommandResult.Unhandled && output.Frames.Count == 0 &&
                    Same(reserved,f.Read()),"Loading重复16不创建第二个实例或重复地图初始化");
                foreach (byte[] body in new[] { new byte[7],new byte[] { 1,0,0,0,0,0,0,0 } })
                {
                    That(await commands.DispatchAsync(context,new(1,37,body)) == CommandResult.Unhandled && output.Frames.Count == 0 &&
                        f.Session.Phase == SessionPhase.Loading && f.Session.AdvanceQuestCycle(after) is null && Same(reserved,f.Read()),
                        "未知37形状不解除回放pending或推进周期");
                }
                That(await commands.DispatchAsync(context,new(1,37,new byte[8])) == CommandResult.Handled &&
                    Frames(output,(1,37),(0,30),(0,3)) && Body(output.Frames[0]).SequenceEqual<byte>([1]) &&
                    Body(output.Frames[1]).SequenceEqual(new byte[5]) &&
                    Body(output.Frames[2]).SequenceEqual(DungeonPackets.DungeonPlayerState(before.Id)) &&
                    f.Session.Phase == SessionPhase.Dungeon && !f.Session.BeginQuestReplayDungeonSelection(1).Pending && Same(reserved,f.Read()),
                    "真实37发布ACK/加载通知/state1，完成单房加载并清回放pending，不写任务或奖励");
                var loadedIdentity = f.Session.DungeonSnapshot!.Identity;
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,new(1,37,new byte[8])) == CommandResult.Unhandled &&
                    output.Frames.Count == 0 && f.Session.DungeonSnapshot!.Identity == loadedIdentity && Same(reserved,f.Read()),
                    "重复37不重做加载事实或副本身份预留");
                var rolled = f.Session.AdvanceQuestCycle(after);
                That(rolled is not null && rolled.Character.Revision == reserved.Revision+1 &&
                    rolled.Character.CompletedQuests.SequenceEqual([101]) &&
                    rolled.Character.ActiveMainQuests.SequenceEqual(reserved.ActiveMainQuests) &&
                    rolled.Character.DungeonState == reserved.DungeonState && f.Session.DungeonSnapshot!.Identity == loadedIdentity &&
                    f.Session.Phase == SessionPhase.Dungeon && Same(rolled.Character,f.Read()) && !stop.IsCancellationRequested,
                    "实际37之后正常推进周期，仅清每日完成并保持回放/其他接取及当前run身份");
            }

            async Task UnauthorizedAsync()
            {
                using var f = new QuestCycleContinuationTests.Fixture(DateTimeOffset.UtcNow,forReplay:true);
                var before = f.Read(); var commands = Registry(f);
                using var stop = new CancellationTokenSource(); using var output = new ControlledOutput();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop);
                foreach (uint quest in new uint[] { 101,999 })
                {
                    byte[] body = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(body,quest);
                    That(await commands.DispatchAsync(context,new(1,1459,body)) == CommandResult.Handled && Frames(output,(1,1459)) &&
                        Body(output.Frames.Single()).SequenceEqual<byte>([0,19,0]) && Same(before,f.Read()) &&
                        !f.Session.BeginQuestReplayDungeonSelection(1).Pending,
                        "实际1459无connection/未知任务只回中立失败，不准备副本或改完成记录");
                    output.Frames.Clear();
                }
                That(await commands.DispatchAsync(context,new(1,1459,new byte[3])) == CommandResult.Unhandled &&
                    output.Frames.Count == 0 && Same(before,f.Read()),"1459未知正文不猜任务编号或启动资源准备");
                That(await commands.DispatchAsync(context,Selection()) == CommandResult.Handled &&
                    Frames(output,(1,16)) && Body(output.Frames.Single()).SequenceEqual(DungeonPackets.SelectionRejected()) &&
                    f.Session.Phase == SessionPhase.Town && f.Session.DungeonSnapshot is null && Same(before,f.Read()),
                    "只有真实完成qid与replay flag不能代替1459准备身份而直接进入副本");
            }

            async Task CancelAsync()
            {
                using var f = new QuestCycleContinuationTests.Fixture(DateTimeOffset.UtcNow,forReplay:true);
                var before = f.Read(); f.PrepareContinuation(forReplay:true); var commands = Registry(f);
                using var stop = new CancellationTokenSource(); using var output = new ControlledOutput();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop);
                await commands.DispatchAsync(context,Entry(1)); output.Frames.Clear();
                That(await commands.DispatchAsync(context,new(1,(ushort)CmdPacketType.BACK_2_VILLAGE,[])) == CommandResult.Handled &&
                    output.Frames[0][0] == 1 && Opcode(output.Frames[0]) == (ushort)CmdPacketType.BACK_2_VILLAGE &&
                    Body(output.Frames[0]).SequenceEqual<byte>([1]) && f.Session.Phase == SessionPhase.Town &&
                    f.Session.CurrentDungeonSelection is null && !f.Session.BeginQuestReplayDungeonSelection(1).Pending && Same(before,f.Read()),
                    "实际取消选图清除待回放身份，保持原位置及全部任务状态");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Selection()) == CommandResult.Handled && Frames(output,(1,16)) &&
                    Body(output.Frames.Single()).SequenceEqual(DungeonPackets.SelectionRejected()) && Same(before,f.Read()),
                    "取消后的迟到16不恢复回放或预留新attempt");
            }

            async Task ReselectDuringSendAsync()
            {
                using var f = new QuestCycleContinuationTests.Fixture(DateTimeOffset.UtcNow,forReplay:true);
                f.PrepareContinuation(forReplay:true); var commands = Registry(f);
                using var stop = new CancellationTokenSource(); using var output = new ControlledOutput { HoldOn = 3 };
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop);
                await commands.DispatchAsync(context,Entry(1)); output.Frames.Clear();
                var publication = commands.DispatchAsync(context,Selection());
                Character reserved;
                try
                {
                    await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    reserved = f.Read();
                    That(f.Session.Phase == SessionPhase.Loading && Same(reserved,f.Session.Selected!) && !publication.IsCompleted,
                        "回放28网络等待前已提交入场预留，等待不占owner锁");
                    f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                }
                finally { output.Release.TrySetResult(); }
                That(await publication.WaitAsync(TimeSpan.FromSeconds(5)) == CommandResult.Handled && Frames(output,(0,28)) &&
                    f.Session.Phase == SessionPhase.Town && f.Session.DungeonSnapshot is null &&
                    !f.Session.BeginQuestReplayDungeonSelection(1).Pending && Same(reserved,f.Read()) &&
                    Same(reserved,f.Session.Selected!) && !stop.IsCancellationRequested && sender.PendingBytes == 0,
                    "重选允许在途28结束，阻止旧29投影或恢复旧回放；已预留key/attempt保持");
            }

            async Task CancelLoadingAsync()
            {
                var now = DateTimeOffset.UtcNow;
                using var f = new QuestCycleContinuationTests.Fixture(now,forReplay:true);
                f.PrepareContinuation(forReplay:true); var commands = Registry(f);
                using var stop = new CancellationTokenSource(); using var output = new ControlledOutput();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop);
                await commands.DispatchAsync(context,Entry(1)); await commands.DispatchAsync(context,Selection());
                var reserved = f.Read(); output.Frames.Clear();
                That(await commands.DispatchAsync(context,new(1,42,[])) == CommandResult.Handled && Frames(output,(1,42),(0,3),(0,24)) &&
                    Body(output.Frames[0]).SequenceEqual<byte>([1]) && f.Session.Phase == SessionPhase.Town &&
                    f.Session.DungeonSnapshot is null && !f.Session.BeginQuestReplayDungeonSelection(1).Pending && Same(reserved,f.Read()),
                    "Loading期间真实42回城清安装后的回放pending，只退场且保持已预留身份/任务");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,new(1,37,new byte[8])) == CommandResult.Unhandled &&
                    output.Frames.Count == 0 && Same(reserved,f.Read()),"已取消回放的迟到37不恢复旧run或加载状态");
                That(await commands.DispatchAsync(context,Selection()) == CommandResult.Handled && Frames(output,(1,16)) &&
                    Body(output.Frames.Single()).SequenceEqual(DungeonPackets.SelectionRejected()) && Same(reserved,f.Read()),
                    "Loading退场后的迟到16不能复用已安装回放缓存");
                var rolled = f.Session.AdvanceQuestCycle(NextCycle(now));
                That(rolled is not null && rolled.Character.Revision == reserved.Revision+1 &&
                    rolled.Character.CompletedQuests.SequenceEqual([101]) &&
                    rolled.Character.ActiveMainQuests.SequenceEqual(reserved.ActiveMainQuests) &&
                    rolled.Character.DungeonState == reserved.DungeonState && Same(rolled.Character,f.Read()) &&
                    f.Session.Phase == SessionPhase.Town,"取消Loading回放后06:00正常切日，不被旧installed缓存阻塞");
            }

            async Task ReselectRejectedEntryAsync()
            {
                using var f = new QuestCycleContinuationTests.Fixture(DateTimeOffset.UtcNow,forReplay:true);
                var before = f.Read(); f.PrepareContinuation(forReplay:true); var commands = Registry(f);
                using var stop = new CancellationTokenSource(); using var output = new ControlledOutput { HoldOn = 1 };
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop);
                // 只占住真实Sender写泵，令失败ACK确实排队；不制造另一套角色/副本状态。
                var barrier = sender.Send(0,3,DungeonPackets.DungeonPlayerState(before.Id),"replay_test_send_barrier");
                await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var publication = commands.DispatchAsync(context,Entry(2));
                try
                {
                    That(!publication.IsCompleted && Same(before,f.Read()),"错误回放15失败ACK已排队而未出队，不持久预留身份");
                    f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                }
                finally { output.Release.TrySetResult(); }
                await barrier.WaitAsync(TimeSpan.FromSeconds(5));
                That(await publication.WaitAsync(TimeSpan.FromSeconds(5)) == CommandResult.Handled && Frames(output,(0,3)) &&
                    f.Session.Phase == SessionPhase.Town && !f.Session.BeginQuestReplayDungeonSelection(1).Pending &&
                    Same(before,f.Read()) && !stop.IsCancellationRequested && sender.PendingBytes == 0,
                    "失败ACK15出队时复验原选角，重选后仅保留在途屏障帧，不干扰新场景");
            }

            async Task StaleOwnerAsync()
            {
                using var f = new QuestCycleContinuationTests.Fixture(DateTimeOffset.UtcNow,forReplay:true);
                var before = f.Read(); f.PrepareContinuation(forReplay:true); var commands = Registry(f);
                using var stop = new CancellationTokenSource(); using var output = new ControlledOutput();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop); f.Owner.Dispose();
                try { await commands.DispatchAsync(context,Entry(1)); }
                catch (InvalidOperationException error) when (error.Message == "Stale account generation")
                {
                    That(output.Frames.Count == 0 && Same(before,f.Session.Selected!) && f.Session.Phase == SessionPhase.Town,
                        "失效owner不能发布回放15或预留副本身份");
                    return;
                }
                throw new InvalidOperationException("FAIL: stale replay owner was accepted");
            }

            async Task CancelContinuationAsync()
            {
                using var f = new QuestCycleContinuationTests.Fixture(DateTimeOffset.UtcNow,forReplay:true);
                var before = f.Read(); f.PrepareContinuation(); var commands = Registry(f);
                using var stop = new CancellationTokenSource(); using var output = new ControlledOutput();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop);
                That(await commands.DispatchAsync(context,Entry(0)) == CommandResult.Handled && Frames(output,(1,15),(0,3),(0,27)) &&
                    f.Session.Phase == SessionPhase.MapSelect && Same(before,f.Read()),
                    "连图15按ACK、state1、27打开MapSelect，不混入普通入口1536，16前无地图通知或持久预留");
                output.Frames.Clear();
                foreach (int offset in new[] { 0,4,5,7,8,9,11,15,16,20,21 })
                {
                    byte[] body = Convert.FromHexString("010000000000000000FFFF0000000000650000000000000000");
                    body[offset] ^= 1;
                    That(await commands.DispatchAsync(context,new(1,16,body)) == CommandResult.Handled && Frames(output,(1,16)) &&
                        Body(output.Frames.Single()).SequenceEqual<byte>([0,9,0,0]) && Same(before,f.Read()) && f.Session.DungeonSnapshot is null,
                        "连图16拒绝错误目标、qid、难度及全部普通模式字段，不fallback其他入口");
                    output.Frames.Clear();
                }
                That(await commands.DispatchAsync(context,new(1,(ushort)CmdPacketType.BACK_2_VILLAGE,[])) == CommandResult.Handled &&
                    f.Session.Phase == SessionPhase.Town && f.Session.HasDungeonContinuationRequest() && Same(before,f.Read()),
                    "真实取消连图选图保留有界终态，无持久预留");
                output.Frames.Clear();
                byte[] late = Convert.FromHexString("010000000000000000FFFF0000000000000000000000000000");
                That(await commands.DispatchAsync(context,new(1,16,late)) == CommandResult.Handled && Frames(output,(1,16)) &&
                    Body(output.Frames.Single()).SequenceEqual<byte>([0,9,0,0]) && Same(before,f.Read()),
                    "取消后普通目标的迟到16也不能fallback普通准入");
                output.Frames.Clear();
                // 已授权回放准备复用现有夹具，不重载资源；真正15必须消费旧连图取消终态。
                f.PrepareContinuation(forReplay:true);
                That(await commands.DispatchAsync(context,Entry(1)) == CommandResult.Handled && Frames(output,(1,15),(0,3)) &&
                    !f.Session.HasDungeonContinuationRequest(),"合法回放15在原选择身份复验后消费连图取消终态");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Selection()) == CommandResult.Handled && Frames(output,(0,28),(0,29)) &&
                    f.Read().DungeonState!.LastAttempt == before.DungeonState!.LastAttempt+1,
                    "取消连图后新回放真实16仍可安装且只预留一次attempt");
            }
        }
        finally { TestFiles.DeleteDirectory(root,true); }
        Console.WriteLine($"PASS {Count-checks} 合成回放真实15/16/37 Handler发布、拒绝、重复预留及选角隔离断言（非主线/实机）");
    }

    static GameConnectionContext Context(QuestCycleContinuationTests.Fixture fixture,Stream output,
        GameConnectionSender sender,CancellationTokenSource stop) =>
        new(new(89,IPAddress.Loopback,output,stop),fixture.Owner,fixture.Session,sender);
    static Packet Entry(uint dungeon)
    {
        byte[] body = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(body,dungeon);
        return new(1,15,body);
    }
    static Packet Selection(uint dungeon = 1,uint quest = 101,byte difficulty = 0,ushort unknown5 = 0,uint unknown11 = 0)
    {
        byte[] body = new byte[25]; BinaryPrimitives.WriteUInt32LittleEndian(body,dungeon); body[4] = difficulty;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(5),unknown5); body[7] = 3; body[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(9),123);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(11),unknown11); body[15] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16),quest); body[20] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(21),0x12345678);
        return new(1,16,body);
    }
    static ushort Opcode(byte[] frame) => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1));
    static DateTimeOffset NextCycle(DateTimeOffset now)
    {
        var local = now.ToOffset(TimeSpan.FromHours(8));
        var boundary = new DateTimeOffset(local.Year,local.Month,local.Day,6,0,0,local.Offset);
        return local < boundary ? boundary : boundary.AddDays(1);
    }
    static ReadOnlySpan<byte> Body(byte[] frame) => frame.AsSpan(16);
    static bool Frames(ControlledOutput output,params (byte Kind,ushort Opcode)[] expected) =>
        output.Frames.Select(frame => (frame[0],Opcode(frame))).SequenceEqual(expected);
    sealed class ControlledOutput : MemoryStream
    {
        int writes;
        public int HoldOn { get; init; }
        public List<byte[]> Frames { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken = default)
        {
            if (++writes == HoldOn)
            {
                Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            }
            Frames.Add(buffer.ToArray());
        }
    }
}
