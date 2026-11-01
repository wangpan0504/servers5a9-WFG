using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestCorePublicationTests
{
    public static async Task RunAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog? sharedQuests = null,SkillLearningCatalog? sharedSkills = null)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(),"S5A9-quest-core-publication-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var log = new ServerLog(root);
            var actual = sharedQuests ?? global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf); var learning = sharedSkills ?? global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
            var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning); var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
            var computer = new CharacterStatComputer(characters);
            That(QuestCharacterReward.TryTp(actual.Get(2674),out ushort amount) && amount == 2,
                "发布专项使用已确认的本版空数组2TP合同，场景与任务条件合成");
            foreach (bool gold in new[] { false,true }) await TpAsync(gold);
            await TpSendFailureAsync();
            await ThresholdAsync();
            await DeliveryAfterSceneAsync();
            ulong entry = growth.GetLevelStartExperience(95);
            await CappedAwardAsync(94, entry - 5, entry, 5);
            await CappedAwardAsync(95, entry, entry, 0);
            ulong historical = growth.GetCumulativeExperienceBoundary(95) - 1;
            await CappedAwardAsync(95, historical, historical, 0);

            StoryQuest TpQuest(bool gold = false) => QuestEligibilityTests.Quest(100) with
            {
                IntData = [2], Npc = 2, CompleteNpc = 2, RewardType = "[tp]", Attributes = ["not give exp quest"],
                DeclaredGoldMultiple = gold ? 50 : null, DeclaredIgnoreLevelForExperience = 1
            };
            QuestOrdinaryCompletionTests.Fixture TpFixture(bool gold = false) =>
                new(towns,characters,learning,growth,parameters,items,TpQuest(gold),materialCount:0,active:[new(0,100,0)],
                    configure: candidate => candidate with { InitialLearning = candidate.InitialLearning with
                        { BonusTp = 11, Trees = candidate.InitialLearning.Trees.Select(tree => tree with { Tp = 11 }).ToArray() } });
            GameCommandRegistry Registry(QuestOrdinaryCompletionTests.Fixture fixture)
            {
                var commands = new GameCommandRegistry(); var projection = new QuestProjection(fixture.Catalog);
                new QuestLifecycleHandler(fixture.Catalog,items.Items,items.GoldLimits,items.EpicPieces,log,projection).Register(commands);
                new QuestConditionHandler(log,fixture.Catalog,items.Items,items.GoldLimits,items.EpicPieces,
                    parameters:parameters,growth:growth).Register(commands);
                new QuestCompletionHandler(fixture.Catalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,log,
                    new CharacterLevelProjection(computer,projection),projection).Register(commands);
                commands.Seal(); return commands;
            }

            async Task CappedAwardAsync(byte initialLevel, ulong initialExperience, ulong finalExperience, uint gained)
            {
                var quest = QuestEligibilityTests.Quest(100) with { DeclaredIgnoreLevelForExperience = 1,
                    IntData = [2], Npc = 2, CompleteNpc = 2, Rewards = [new(0,0,null),new(0,0,null)] };
                using var fixture = new QuestOrdinaryCompletionTests.Fixture(towns,characters,learning,growth,
                    parameters,items,quest,materialCount: 0,configure: candidate => candidate with { Level = initialLevel,
                        InitialLearning = candidate.InitialLearning with { Experience = initialExperience,
                            Trees = candidate.InitialLearning.Trees.Select(tree => tree with {
                                Sp = checked((ushort)(tree.Sp+growth.GetSpDelta(1,initialLevel))),
                                Tp = checked((ushort)(tree.Tp+growth.GetTpDelta(1,initialLevel))) }).ToArray() } });
                fixture.Change(100,QuestLifecycleAction.Condition,new(null,0,0));
                var expected = fixture.Read();
                var prepared = fixture.Prepare(ushort.MaxValue);
                That(prepared.Plan is { } planned && planned.ExperienceReward.Amount > gained,
                    "封顶发布用例保留正额度任务奖励："+prepared.Outcome);
                var commands = Registry(fixture);
                using var stop = new CancellationTokenSource();
                using var stream = new ControlledOutput();
                await using var sender = new GameConnectionSender(fixture.Owner,stream,stop,0,(_,_,_,_,_) => { });
                var context = Context(fixture,stream,sender,stop);
                var request = Finish(100);
                That(await commands.DispatchAsync(context,request) == CommandResult.Handled && fixture.Receipts == 1 &&
                    fixture.Read().Level == 95 && fixture.Read().Learning.Experience == finalExperience,
                    "部分或零到账仍经真实Handler完成任务事务");
                var ack = stream.Frames.Single(frame => frame[0] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 34);
                var notices = stream.Frames.Where(frame => frame[0] == 0 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 37).ToArray();
                That(notices.Length == (initialLevel == 94 ? 1 : 0),
                    "跨入95仍通知，原本已满级的正额度任务不再发送NOTI37");
                if (initialLevel == 94)
                    That(notices[0].Length == 107 &&
                        BinaryPrimitives.ReadUInt64LittleEndian(notices[0].AsSpan(17)) == finalExperience &&
                        BinaryPrimitives.ReadUInt32LittleEndian(notices[0].AsSpan(25)) == 0,
                        "跨入满级NOTI37完整绝对经验与零队伍分量");
                That(BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(20)) == gained &&
                    fixture.Read().Learning.Trees.Select(tree => (tree.Context,tree.Sp,tree.Tp)).SequenceEqual(
                        expected.Learning.Trees.Select(tree => (tree.Context,
                            Sp: checked((ushort)(tree.Sp + growth.GetSpDelta(initialLevel,95))),
                            Tp: checked((ushort)(tree.Tp + growth.GetTpDelta(initialLevel,95)))))),
                    "ACK34显示实际到账，两树仅增加实际跨级贡献");
                That(await commands.DispatchAsync(context,request) == CommandResult.Handled && fixture.Receipts == 1 &&
                    fixture.Read().Level == 95 && fixture.Read().Learning.Experience == finalExperience,"满级重复请求不多发经验或任务奖励");
            }

            async Task DeliveryAfterSceneAsync()
            {
                var origin = towns.Room;
                var basic = QuestEligibilityTests.Quest(100) with
                {
                    IntData = [2], Npc = 2, CompleteNpc = 424242, Attributes = ["not give exp quest"],
                    Rewards = [new(0,2058,null),new(0,1,null)]
                };
                var question = basic with
                {
                    Condition = "[question]", Definition = new([new("question info",
                        [new(6,0,"回答"),new(6,0,"问题"),new(6,0,"选项0"),new(6,0,"选项1")],[])]) { Job = "[all]" }
                };
                var answer = basic with { Id = 101, Path = "fixture/101.qst", Definition = new([])
                    { Job = "[all]", PrerequisiteGroups = [[100]], RequiredAnswers = [new(100,1)] } };
                StoryQuest[] targets =
                [
                    basic with { Condition = "[reach the range]", SubType = 1,
                        IntData = [origin.Town,origin.Area,0,0,10000,10000] },
                    basic with { Condition = "[look cinematic]", IntData = [1], CinematicPath = "fixture/scene.cmt",
                        Definition = new([new("go guide",[new(0,origin.Town,null),new(0,origin.Area,null),
                            new(0,2,null),new(0,-1,null),new(6,0,"前往场景")],[])]) { Job = "[all]" } },
                    basic,
                    basic with { Condition = "[seeking]", IntData = [10165055,1], CompleteNpc = 0 },
                    basic with { Condition = "[seek n meet npc]", IntData = [10165055,1,2] },
                    basic with { Condition = "[hunt enemy]", IntData = [1,-1,1,1,1] },
                    basic with { Condition = "[hunt monster]", IntData = [1,-1,1,1] },
                    basic with { Condition = "[level up]", IntData = [1] },
                    question
                ];
                foreach (var quest in targets)
                foreach (byte job in quest.Condition == "[reach the range]" ? Enumerable.Range(0,16).Select(value => (byte)value) : new byte[] {0})
                {
                    bool hunt = quest.Condition is "[hunt enemy]" or "[hunt monster]";
                    using var f = new QuestOrdinaryCompletionTests.Fixture(towns,characters,learning,growth,parameters,items,quest,
                        materialCount:1,active:hunt ? [new(0,100,0)] : [],definitions:quest.Condition == "[question]" ? [quest,answer] : [quest],
                        configure: candidate => CharacterCreation.Prepare(candidate.Name,characters.Get(job),origin,towns.InitialReturn,learning.Get));
                    if (!hunt) That(f.Change(100,QuestLifecycleAction.Accept)?.Commit is not null,"独立接取合成交付目标："+quest.Condition);
                    var accepted = f.Read();
                    if (accepted.ActiveMainQuests.Single().ConditionState != 0)
                    {
                        That(f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.Rejected && Same(accepted,f.Read()) && f.Receipts == 0,
                            "离开场景规则修复不放行未达成的目标："+quest.Condition);
                        var condition = quest.Condition == "[question]" ? new QuestClientCondition(null,0,1) :
                            new QuestClientCondition(quest.Condition == "[seek n meet npc]" ? (byte)0x20 : null,0,0);
                        That(f.Change(100,QuestLifecycleAction.Condition,condition)?.Commit is not null,
                            "真实源场景通过原进度入口保存目标："+quest.Condition);
                    }
                    var achieved = f.Read();
                    That(achieved.ActiveMainQuests.Single().AcceptanceId != Guid.Empty &&
                        achieved.ActiveMainQuests.Single().ConditionState == (quest.Condition == "[question]" ? 1u : 0u),
                        "保存本次接取的完成进度或实际非零回答："+quest.Condition);
                    That(towns.IsPosition(towns.InitialReturn,QuestEligibility.Completed(achieved)) &&
                        !QuestClientSceneProgress.DialogueNpc(achieved,quest.CompleteNpc,quest,f.Catalog,towns),
                        "交付NPC不存在于源场景，不以常驻演员替代已达成事实："+quest.Condition);
                    f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                    That(f.Session.Selected!.ActiveMainQuests.Single() == achieved.ActiveMainQuests.Single() &&
                        f.Session.LeaveRoom(),"重选恢复原达成进度后，沿真实城镇入口离开原场景："+quest.Condition);
                    var reconnected = f.Read();
                    That(reconnected.Position != achieved.Position &&
                        reconnected.ActiveMainQuests.Single() == achieved.ActiveMainQuests.Single() &&
                        !QuestClientSceneProgress.Matches(reconnected,quest,towns),
                        "重选及跨地图保留原接取与进度，当前位置不再满足原场景："+quest.Condition+"/job"+job);
                    var commands = Registry(f); using var stop = new CancellationTokenSource(); using var output = new ControlledOutput();
                    await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                    var context = Context(f,output,sender,stop);
                    That(await commands.DispatchAsync(context,Finish(100)) == CommandResult.Handled &&
                        output.Opcodes.SequenceEqual(new ushort[] {34,14,21,356}) && output.Frames[0][16] == 1,
                        "跨场景真实34成功回复并发布奖励及完成列表："+quest.Condition+"/job"+job);
                    var saved = f.Read();
                    That(saved.ActiveMainQuests.Length == 0 && saved.CompletedQuests.Contains(100) &&
                        saved.Inventory.Items.Single(item => item.Core.ItemId == 2058).Core.Count ==
                            reconnected.Inventory.Items.Where(item => item.Core.ItemId == 2058).Sum(item => item.Core.Count)+1 &&
                        saved.Revision == reconnected.Revision+1 && f.Receipts == 1 && Same(saved,f.Session.Selected!),
                        "跨场景交付仍整笔保存真实奖励、清槽与一次回执："+quest.Condition);
                    if (quest.Condition == "[question]") That(saved.QuestAnswers[100] == 1,"跨场景交付保留实际回答，不统一解释为scalar0");
                    output.Frames.Clear();
                    That(await commands.DispatchAsync(context,Finish(100)) == CommandResult.Handled &&
                        output.Opcodes.SequenceEqual(new ushort[] {34}) && output.Frames[0][16] == 0 &&
                        Same(saved,f.Read()) && f.Receipts == 1,"跨场景完成后重复交付不再发奖："+quest.Condition);
                }
            }

            async Task TpAsync(bool gold)
            {
                using var f = TpFixture(gold); var before = f.Read(); var commands = Registry(f);
                using var stop = new CancellationTokenSource(); using var output = new ControlledOutput();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop);
                That(await commands.DispatchAsync(context,Finish(100)) == CommandResult.Handled,"真实Handler提交纯TP领奖");
                var after = f.Read();
                That(output.Opcodes.SequenceEqual(gold ? new ushort[] {34,14,19,21,356} : new ushort[] {34,19,21,356}) &&
                    output.Frames[0][0] == 1 && output.Frames.Skip(1).All(frame => frame[0] == 0),
                    "零XP TP完整包序ACK34→实际Gold14（有声明时）→绝对双树19→21→356");
                Bytes(output.Frames[0].AsSpan(16),[1,100,0,4,0,0,0,0,29],
                    "ACK34会面type4的EXP后仅mode29，没有赠点数量、rewardCount或虚构物品尾部");
                var skills = SkillPacketsTests.DecodeInitialized(output.Frames.Single(frame => Opcode(frame) == 19).AsSpan(16));
                That(skills.Zip(after.Learning.Trees.OrderBy(tree => tree.Context)).All(pair =>
                    pair.First.Tp == pair.Second.Tp && pair.First.Sp == pair.Second.Sp && pair.First.Skills.SequenceEqual(pair.Second.Skills)) &&
                    after.Learning.BonusTp == 13 && before.Learning.Trees.Zip(after.Learning.Trees).All(pair => pair.Second.Tp == pair.First.Tp+2) &&
                    after.Learning.Experience == before.Learning.Experience && after.Revision == before.Revision+1 &&
                    Scalar(f,"SELECT bonus_tp FROM character_tp_sources") == 13 && f.Receipts == 1 && Same(after,f.Session.Selected!),
                    "NOTI19两树完整余额及绑定来自同一已提交快照，额外TP来源已落库，不发零经验37");
                if (gold)
                {
                    uint expectedGold = (uint)((long)parameters.Gold[1]*50/100);
                    That(expectedGold > 0 && after.Inventory.Gold == before.Inventory.Gold+expectedGold,"TP奖励保留声明金币倍率");
                    Bytes(output.Frames[1].AsSpan(16),InventoryPackets.Delta(after.Inventory,InventoryListType.Main,[new(InventoryListType.Main,0)]),
                        "金币通过真实Main槽0绝对14校准，不增加mode29的尾部");
                }
                else That(after.Inventory.Gold == before.Inventory.Gold,"无金币声明不额外发金币");
                Bytes(output.Frames[^1].AsSpan(16),TownPackets.CompletedQuests(after),"最后356投影真实完成位");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Finish(100)) == CommandResult.Handled && output.Opcodes.SequenceEqual(new ushort[] {34}) &&
                    Same(after,f.Read()) && f.Receipts == 1,"重复34仅拒绝ACK，不重发19或赠点");
                Bytes(output.Frames.Single().AsSpan(16),[0,19,0],"已完成TP任务返回静默19拒绝");
            }

            async Task TpSendFailureAsync()
            {
                using var f = TpFixture(); var before = f.Read(); var commands = Registry(f);
                using var stop = new CancellationTokenSource(); using var output = new ControlledOutput { FailOn = 2 };
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                await RejectAsync<IOException>(() => commands.DispatchAsync(Context(f,output,sender,stop),Finish(100)),
                    "纯TP已提交后19发送失败传播");
                var saved = f.Read();
                That(stop.IsCancellationRequested && output.Opcodes.SequenceEqual(new ushort[] {34}) && sender.PendingBytes == 0 &&
                    saved.Learning.BonusTp == 13 && saved.Learning.Trees.All(tree => tree.Tp == 13) &&
                    saved.Revision == before.Revision+1 && Same(saved,f.Session.Selected!) && f.Receipts == 1,
                    "发送故障保留完整TP来源与双树，不回滚或重复赠点");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(Same(saved,f.Session.Selected!),"重选从持久库恢复发送失败时的完整赠点结果");
                using var reconnected = new ControlledOutput(); using var newStop = new CancellationTokenSource();
                await using var newSender = new GameConnectionSender(f.Owner,reconnected,newStop,0,(_,_,_,_,_) => { });
                That(await commands.DispatchAsync(Context(f,reconnected,newSender,newStop),Finish(100)) == CommandResult.Handled &&
                    reconnected.Opcodes.SequenceEqual(new ushort[] {34}) && Same(saved,f.Read()) && f.Receipts == 1,
                    "重新建立sender后重复34不补第二次TP或重发增量领奖");
                Bytes(reconnected.Frames.Single().AsSpan(16),[0,19,0],"重连TP领奖只返回已完成拒绝");
            }

            async Task ThresholdAsync()
            {
                var parent = QuestEligibilityTests.Quest(200) with
                {
                    Grade = "[hidden]", Condition = "[clear multi quest]", SubType = 4, IntData = [100,101], Npc = 2, CompleteNpc = -1,
                    Attributes = ["not give exp quest"], Definition = new([]) { Job = "[all]", MinimumClearCount = 1 }
                };
                var first = QuestEligibilityTests.Quest(100) with
                    { Grade = "[sub]", MainQuest = 200, IntData = [2], Npc = 2, CompleteNpc = 2, Attributes = ["not give exp quest"] };
                var second = first with { Id = 101, Path = "fixture/101.qst" };
                using var f = new QuestOrdinaryCompletionTests.Fixture(towns,characters,learning,growth,parameters,items,first,
                    materialCount:0,definitions:[parent,first,second],active:[]);
                var commands = Registry(f); using var stop = new CancellationTokenSource(); using var output = new ControlledOutput();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop);
                foreach (ushort id in new ushort[] {200,100,101})
                {
                    output.Frames.Clear();
                    That(await commands.DispatchAsync(context,Identity(31,id)) == CommandResult.Handled &&
                        output.Opcodes.SequenceEqual(new ushort[] {31,21,356}) && output.Frames[0][16] == 1,
                        "真实31独立接取隐藏父及全部子项 "+id);
                }
                var accepted = f.Read();
                That(accepted.ActiveMainQuests.Length == 3 && accepted.ActiveMainQuests.All(row =>
                    row.ConditionState == 1 && row.AcceptanceId != Guid.Empty) &&
                    accepted.ActiveMainQuests.Select(row => row.AcceptanceId).Distinct().Count() == 3,
                    "父初值1，全部子槽各有独立接取身份，不按顺序只接首子");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Trigger(100)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {33}) && f.Read().CompletedQuests.Length == 0,
                    "会面33只满足真实场景条件，物品空数组子仍等待普通34，不提前写完成");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Finish(100)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {34,574,21,356}),"实际完成一个子34后发布父阈值和真实完成表");
                var childCompleted = f.Read();
                That(childCompleted.CompletedQuests.SequenceEqual([100]) && childCompleted.ActiveMainQuests is
                    [{ QuestId:200,ConditionState:0 },{ QuestId:101,ConditionState:1 }] && f.Receipts == 1,
                    "仅已交付子100进入完成集，父归0而未选101保持活动");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Trigger(200)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {33}) && Same(childCompleted,f.Read()),
                    "父33按持久完成阈值重读，不信任客户端再加计数或写revision");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Finish(200)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {34,574,21,356}),"父70领奖完整ACK34→574关闭未选槽→21→356");
                Bytes(output.Frames[0].AsSpan(16),[1,200,0,70,0,0,0,0,0,0],
                    "父ACK34使用真实condition70、普通mode0和空物品count，无TP或伪造子奖励");
                var committed = f.Read();
                That(committed.Revision == childCompleted.Revision+1 && committed.ActiveMainQuests.Length == 0 &&
                    committed.CompletedQuests.SequenceEqual([100,200]) && !committed.CompletedQuests.Contains(101) &&
                    f.Receipts == 2 && Same(childCompleted with { Revision = committed.Revision,
                        ActiveMainQuests = [], CompletedQuests = [100,200] },committed),
                    "父奖励与未选子清槽一次提交，保留物品、余额、学习和实际完成分支");
                Bytes(output.Frames[1].AsSpan(16),[0,0,0,0],"574明确清空所有已关闭活动槽");
                Bytes(output.Frames[^1].AsSpan(16),TownPackets.CompletedQuests(committed),"父投影最后356恢复仅100和200完成位");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Identity(32,101)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {32}) && Same(committed,f.Read()) && f.Receipts == 2,
                    "客户端原生未选子32尾包只确认已提交关闭，无第二次回收、写库或revision");
                Bytes(output.Frames.Single().AsSpan(16),[1,101,0],"关闭子32返回真实成功ACK");
            }
        }
        finally { TestFiles.DeleteDirectory(root,true); }
        Console.WriteLine($"PASS {Count-checks} 合成TP、type70、经验封顶与跨场景交付的真实Handler、Sender包序及幂等发布断言（非主线实机）");
    }

    static ushort Opcode(byte[] frame) => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1));
    static Packet Identity(ushort opcode,ushort quest) => new(1,opcode,[(byte)opcode,(byte)(opcode>>8),(byte)quest,(byte)(quest>>8)]);
    static Packet Trigger(ushort quest) => new(1,33,[33,0,(byte)quest,(byte)(quest>>8),0,0]);
    static Packet Finish(ushort quest) => new(1,34,[34,0,(byte)quest,(byte)(quest>>8),255,255,1,0,255,255]);
    static GameConnectionContext Context(QuestOrdinaryCompletionTests.Fixture fixture,Stream output,
        GameConnectionSender sender,CancellationTokenSource stop) =>
        new(new(88,IPAddress.Loopback,output,stop),fixture.Owner,fixture.Session,sender);
    static long Scalar(QuestOrdinaryCompletionTests.Fixture fixture,string sql)
    {
        using var db = fixture.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
    static async Task RejectAsync<T>(Func<Task> action,string message) where T:Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T) { That(true,message); return; }
        throw new InvalidOperationException("FAIL: expected "+typeof(T).Name+": "+message);
    }
    sealed class ControlledOutput : MemoryStream
    {
        int writes;
        public int FailOn { get; init; }
        public List<byte[]> Frames { get; } = [];
        public IEnumerable<ushort> Opcodes => Frames.Select(Opcode);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken = default)
        {
            if (++writes == FailOn) throw new IOException("合成任务发布故障");
            Frames.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
