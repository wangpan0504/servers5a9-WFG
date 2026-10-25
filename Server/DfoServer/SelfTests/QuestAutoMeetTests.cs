using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestAutoMeetTests
{
    public static void RunCore()
    {
        int checks = Count;
        var quest = AutoMeet();
        That(QuestStateProgress.Type(quest) == 4 && QuestStateProgress.Type(quest with { SubType = -1 }) is null,
            "会面sub1按接取事实处理，普通sub-1仍等待自身会面事件");
        foreach (var data in new int[][] { [],[0],[-1],[424242,1] })
            That(QuestStateProgress.Type(quest with { IntData = data }) is null,
                "未确认的空/零/负/额外参数保持未启用");
        var catalog = new QuestCatalog(new() { [100] = quest });
        var active = new ActiveMainQuest(7,100,1) { AcceptanceId = Guid.NewGuid() };
        for (byte job = 0; job < 16; job++)
        {
            var character = new Character(1,0,"AutoMeet",1,new(99,9,0,0),null) { Job = job };
            That(QuestStateProgress.Scalar(character,quest) == 1 &&
                QuestStateProgress.Scalar(character with { CompletedQuests = [100] },quest) == 1 &&
                QuestStateProgress.Scalar(character with { ActiveMainQuests = [active with { AcceptanceId = Guid.Empty }] },quest) == 1 &&
                QuestStateProgress.Scalar(character with { ActiveMainQuests = [active with { QuestId = 101 }] },quest) == 1,
                "当前同qid非空接取身份是唯一归零事实，不以完成位/旧槽/其它任务代替：job"+job);
            var accepted = character with { ActiveMainQuests = [active] };
            var refreshed = QuestStateProgress.Refresh([active],accepted,catalog).Single();
            That(QuestStateProgress.Scalar(accepted,quest) == 0 && refreshed.ConditionState == 0 &&
                refreshed.AcceptanceId == active.AcceptanceId && refreshed.Slot == active.Slot,
                "真实接取归零不读取当前NPC坐标、职业或点击次数：job"+job);
        }
        Console.WriteLine($"PASS {Count-checks} 自动会面type4/sub1形状与接取事实断言");
    }

    public static async Task RunAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog? sharedQuests = null,SkillLearningCatalog? sharedSkills = null)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(),"S5A9-auto-meet-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var log = new ServerLog(root);
            var actual = sharedQuests ?? global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf); var learning = sharedSkills ?? global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
            var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning); var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
            var computer = new CharacterStatComputer(characters);
            var ordinary = actual.All.Where(quest => quest.Condition == "[meet npc]" && quest.SubType == 1 &&
                QuestEligibility.InspectResource(quest).Entry == QuestResourceEntry.Ordinary).ToArray();
            int[] originalIds = [1683,2002,2637,2680,2888,2900,2912,4391,4427,4428,4429,4430,4431,4432,4433,4434,4435,
                4539,4728,5783,5835,6050,6080,6170,6880,6924,6927,7905,7912,7925,7938,7957,7959,7961,7963,7968,7971,
                8672,10104,10106,10108,10109,10110];
            That(ordinary.Where(quest => originalIds.Contains(quest.Id)).Select(quest => quest.Id).Order().SequenceEqual(originalIds),
                "原43条自动会面资源完整保留，不以总数掩盖遗漏或替换");
            var additions = ordinary.Where(quest => !originalIds.Contains(quest.Id)).ToArray();
            That(additions is [{ Id:8660, Grade:"[episode]", SubType:1, Npc:614, IntData:[469], MinimumLevel:94, MaximumLevel:99 } episode] &&
                QuestEpisodeEntry.Supports(episode), "唯一新增普通自动会面为完整已支持超时空剧情首步8660");
            var firstEpisode = actual.Get(8660);
            That(QuestEligibility.InspectResource(firstEpisode with { Id=8672 }).Entry == QuestResourceEntry.Dedicated &&
                QuestEligibility.InspectResource(firstEpisode with { Definition=firstEpisode.Definition! with { ExposedByNpc=false } }).Entry != QuestResourceEntry.Ordinary,
                "episode首步白名单不泛开其它专用或隐藏定义");
            foreach (var quest in ordinary)
            {
                That(quest.IntData is [> 0] && QuestStateProgress.Type(quest) == 4 &&
                    quest.Definition is { Diagnostics.Length: 0 } && QuestCoverage.HasRequiredReferences(quest,actual),
                    "真实单正整数、完整可用OR引用与当前目标类型："+quest.Id);
                var definition = quest.Definition!;
                var completed = definition.PrerequisiteGroups.Where(group => group.Length != 0).Take(1).SelectMany(group => group);
                var character = new Character(1,0,"Resource",1,towns.Room,null)
                {
                    Level = (byte)quest.MinimumLevel,
                    CompletedQuests = completed.Concat(definition.RequiredAnswers.Select(answer => answer.QuestId)).Distinct().ToArray(),
                    QuestAnswers = definition.RequiredAnswers.ToImmutableDictionary(answer => answer.QuestId,answer => (byte)answer.Answer)
                };
                var candidates = Enumerable.Range(0,16).SelectMany(job => Enumerable.Range(0,3).SelectMany(stage =>
                    Enumerable.Range(0,6).Select(grow => character with { Job = (byte)job,GrowType = (byte)(stage*16+grow) })));
                That(candidates.Any(candidate => QuestEligibility.Evaluate(candidate,quest,actual).Eligible),
                    "按自身最低等级、原前置与职业阶段查资格；不推进任何真实主线："+quest.Id);
                if (QuestEligibility.TransferredAutoMeet(quest))
                {
                    foreach (var target in definition.TargetCharacters)
                    {
                        byte job = (byte)Enumerable.Range(0,16).Single(value => QuestEligibility.MatchesJob((byte)value,target.Job));
                        var transferred = character with { Job = job,GrowType = (byte)target.GrowType };
                        That(QuestEligibility.Evaluate(transferred,quest,actual).Eligible,
                            "资源声明的每个首转分支都可接祝贺任务："+quest.Id+"/"+target.GrowType);
                        That(!QuestEligibility.Evaluate(transferred with { GrowType = 0 },quest,actual).Eligible &&
                            !QuestEligibility.Evaluate(transferred with { Job = (byte)((job+1)%16) },quest,actual).Eligible &&
                            !QuestEligibility.Evaluate(transferred with { CompletedQuests = [] },quest,actual).Eligible,
                            "祝贺任务不跳过未转职、错误职业或真实前置："+quest.Id);
                        That(!QuestEligibility.CharacterRequirements(transferred,quest with { RewardType = "[grow type]" },actual).Eligible &&
                            !QuestEligibility.CharacterRequirements(transferred,quest with { SubType = -1 },actual).Eligible &&
                            !QuestEligibility.CharacterRequirements(transferred,quest with { Definition = definition with
                                { TargetCharacters = definition.TargetCharacters.Add(new(target.Job,0,-1)) } },actual).Eligible,
                            "真实转职奖励、普通会面与混合目标不借用祝贺例外："+quest.Id);
                    }
                }
                That(quest.MinimumLevel == 1 || !QuestEligibility.Evaluate(character with { Level = (byte)(quest.MinimumLevel-1) },quest,actual).Eligible,
                    "接取型会面仍遵守资源原最低等级："+quest.Id);
            }

            That(ordinary.Where(QuestEligibility.TransferredAutoMeet).Select(q => q.Id).Order().SequenceEqual(
                new[] {2637,2912,4427,4428,4429,4430,4431,4432,4433,4434,4435,4728,6927}),
                "新版资源13条明确首转后的接取型祝贺会面，普通sub-1不扩展");

            using (var f = Fixture())
            {
                var commands = Registry(f); using var stop = new CancellationTokenSource(); using var output = new Output();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = new GameConnectionContext(new(90,IPAddress.Loopback,output,stop),f.Owner,f.Session,sender);
                var original = f.Read();
                foreach (var data in new int[][] { [],[0],[-1],[424242,1] })
                    TestCheck.Reject<NotSupportedException>(() => QuestLifecyclePlan.Accept(original,AutoMeet() with { IntData = data },
                        f.Catalog,items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow),
                        "未确认参数形状在接取规划中拒绝，不创建活动槽或接取身份");
                That(await commands.DispatchAsync(context,Finish()) == CommandResult.Handled && Same(original,f.Read()) && f.Receipts == 0,
                    "未接取不能用sub1或客户端完成包签发奖励");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Identity(31)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {31,21,356}),"真实31接取自动会面，不等待目标NPC点击");
                var first = f.Read(); var firstActive = first.ActiveMainQuests.Single();
                That(firstActive is { ConditionState: 0 } && firstActive.AcceptanceId != Guid.Empty &&
                    first.Revision == original.Revision+1 && first.CompletedQuests.Length == 0 && f.Receipts == 0,
                    "实际接取、独立身份与初始scalar0一次保存，不提前写完成或奖励");
                Bytes(output.Frames[0].AsSpan(16),[1,100,0,0,0,0,0,0],"ACK31返回真实零scalar与无赠品");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(Same(first,f.Session.Selected!) && f.Session.Selected!.ActiveMainQuests.Single().ConditionState == 0,
                    "未回声33也能重登恢复，不能因丢接取ACK滞留scalar1");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Identity(32)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {32,21,356}) && f.Read().ActiveMainQuests.Length == 0,
                    "可放弃侧边任务按既有32清槽");
                output.Frames.Clear(); await commands.DispatchAsync(context,Identity(31));
                var accepted = f.Read();
                That(accepted.ActiveMainQuests.Single().AcceptanceId != firstActive.AcceptanceId &&
                    accepted.ActiveMainQuests.Single().ConditionState == 0,"放弃重接独立身份，旧接取不复用");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Trigger()) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {33}) && Same(accepted,f.Read()),
                    "真实FF writer的6B零回声只读取接取事实，不增加revision");
                Bytes(output.Frames.Single().AsSpan(16),[1,100,0,0,0,0,0],"ACK33回同任务真实零scalar");
                foreach (var packet in new[] {new Packet(1,33,[33,0,100,0,1,0]),new Packet(1,33,[33,0,100,0,0x10,0,0])})
                {
                    output.Frames.Clear();
                    That(await commands.DispatchAsync(context,packet) == CommandResult.Handled && Same(accepted,f.Read()),
                        "客户端非零数或猎杀通道不能改写自动会面");
                    Bytes(output.Frames.Single().AsSpan(16),[0,19,0],"非法自动会面回声拒绝");
                }
                var preparation = f.Prepare(ushort.MaxValue);
                That(preparation.Plan is not null,"已达成的真实sub1接取可进入普通领奖规划");
                var plan = preparation.Plan!;
                That(plan.ConditionType == 4 && plan.ExperienceReward.Amount > 0 && plan.GoldAmount > 0 &&
                    plan.Rewards is [{ GrantedCount: 2 }] && plan.Consumed.Length == 0 && plan.AuthoritativeClear is null,
                    "自动会面沿正常XP/金币/固定奖励，不伪造清图或特殊零奖励");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Finish()) == CommandResult.Handled &&
                    output.Opcodes.First() == 34 && output.Opcodes.Last() == 356,"真实34提交后发布普通任务快照");
                var after = f.Read(); var ack = output.Frames.First().AsSpan(16);
                That(ack[0] == 1 && ack[3] == 4 && ack[8] == 0 &&
                    after.CompletedQuests.SequenceEqual([100]) && after.ActiveMainQuests.Length == 0 &&
                    after.Revision == accepted.Revision+1 && after.Inventory.Gold == accepted.Inventory.Gold+plan.GoldAmount &&
                    after.Learning.Experience == accepted.Learning.Experience+plan.ExperienceReward.Amount &&
                    after.Inventory.Items.Single(row => row.Core.ItemId == 2058).Core.Count == 2 &&
                    f.Receipts == 1 && Same(after,f.Session.Selected!),"type4普通mode0、资产/成长/清槽/回执原子提交");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Finish()) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {34}) && Same(after,f.Read()) && f.Receipts == 1,
                    "重复34不再次发奖励或完成增量");
            }
            using (var f = Fixture())
            {
                f.Change(100,QuestLifecycleAction.Accept); var before = f.Read(); var plan = f.Prepare(ushort.MaxValue).Plan!;
                f.Sql("CREATE TRIGGER auto_meet_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'auto meet rollback'); END");
                TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue),"普通奖励回执末段SQL失败");
                That(Same(before,f.Read()) && Same(before,f.Session.Selected!) && f.Receipts == 0,
                    "末段失败回滚库存UID、余额、成长、完成和清槽");
                f.Sql("DROP TRIGGER auto_meet_abort");
                That(f.Finish(ushort.MaxValue).Receipt?.Commit is { Duplicate: false,ConditionType: 4 } && f.Receipts == 1,
                    "SQL回滚后恢复原冻结普通奖励规划");
            }
            using (var f = Fixture(loseResult:true))
            {
                f.Change(100,QuestLifecycleAction.Accept); var before = f.Session.Selected!;
                TestCheck.Reject<IOException>(() => f.Finish(ushort.MaxValue),"自动会面已提交但返回丢失");
                var saved = f.Read();
                That(ReferenceEquals(before,f.Session.Selected) && f.Receipts == 1 && saved.CompletedQuests.SequenceEqual([100]),
                    "返回丢失保留真实已提交事务及原待恢复在线状态");
                var recovered = f.Finish(ushort.MaxValue).Receipt!.Commit;
                That(recovered is { Duplicate: true,ProjectionCurrent: true,ConditionType: 4 } && f.Lost!.SamePlan &&
                    Same(saved,recovered.Character) && Same(saved,f.Read()) && f.Receipts == 1,
                    "恢复相同规划与UID/奖励，不重新生成或重复发奖");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(Same(saved,f.Session.Selected!) && f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.AlreadyCompleted,
                    "重登保留唯一普通完成结果");
            }

            QuestOrdinaryCompletionTests.Fixture Fixture(bool loseResult = false) =>
                new(towns,characters,learning,growth,parameters,items,AutoMeet(),materialCount:0,active:[],loseResult:loseResult);
            GameCommandRegistry Registry(QuestOrdinaryCompletionTests.Fixture f)
            {
                var commands = new GameCommandRegistry(); var quests = new QuestProjection(f.Catalog);
                new QuestLifecycleHandler(f.Catalog,items.Items,items.GoldLimits,items.EpicPieces,log,quests).Register(commands);
                new QuestConditionHandler(log,f.Catalog,items.Items,items.GoldLimits,items.EpicPieces,parameters:parameters,growth:growth).Register(commands);
                new QuestCompletionHandler(f.Catalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,log,
                    new CharacterLevelProjection(computer,quests),quests).Register(commands);
                commands.Seal(); return commands;
            }
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine($"PASS {Count-checks} 原43条及新增8660自动会面资源与合成31/33/34、普通奖励/回滚/恢复断言（不执行主线）");
    }

    static StoryQuest AutoMeet() => QuestEligibilityTests.Quest(100) with
    {
        SubType = 1,IntData = [424242],Npc = 424242,CompleteNpc = -1,Grade = "[side]",Difficulty = "g",
        Rewards = [new(0,2058,null),new(0,2,null)],DeclaredGoldMultiple = 100
    };
    static Packet Identity(ushort opcode) => new(1,opcode,[(byte)opcode,(byte)(opcode>>8),100,0]);
    // FF是native writer选择符，正文仍是echo:u16/qid:u16/value:u8/reserved:u8。
    static Packet Trigger() => new(1,33,[33,0,100,0,0,0]);
    static Packet Finish() => new(1,34,[34,0,100,0,255,255,1,0,255,255]);
    sealed class Output : MemoryStream
    {
        public List<byte[]> Frames { get; } = [];
        public IEnumerable<ushort> Opcodes => Frames.Select(frame => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken = default)
        { Frames.Add(buffer.ToArray()); return ValueTask.CompletedTask; }
    }
}
