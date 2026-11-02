using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Characters;
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
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestPlayOnlyTests
{
    public static void RunCore()
    {
        int checks = Count;
        var quest = PlayQuest();
        That(QuestStateProgress.Type(quest) == 49 && QuestStateProgress.Type(quest with { SubType = 0 }) == 49 &&
            QuestStateProgress.Type(quest with { SubType = 1 }) is null && QuestStateProgress.Type(quest with { IntData = [1] }) is null,
            "play only接受原生默认0和旧显式-1，不忽略未知子型或附加整数");
        var catalog = new QuestCatalog(new() { [quest.Id] = quest });
        var accepted = new ActiveMainQuest(3,100,1) { AcceptanceId = Guid.NewGuid() };
        for (byte job = 0; job < 16; job++)
        {
            var character = new Character(1,0,"Play",1,new(38,0,450,250),null) { Job = job };
            That(QuestStateProgress.Scalar(character,quest) == 1 &&
                QuestStateProgress.Scalar(character with { CompletedQuests = [100] },quest) == 1 &&
                QuestStateProgress.Scalar(character with { ActiveMainQuests = [accepted with { AcceptanceId = Guid.Empty }] },quest) == 1,
                "未接取、仅完成位或历史无身份槽不能代替当前实际接取：job"+job);
            var activeCharacter = character with { ActiveMainQuests = [accepted] };
            var refreshed = QuestStateProgress.Refresh([accepted],activeCharacter,catalog).Single();
            That(QuestStateProgress.Scalar(activeCharacter,quest) == 0 && refreshed is { Slot: 3, ConditionState: 0 } &&
                refreshed.AcceptanceId == accepted.AcceptanceId &&
                QuestStateProgress.Scalar(character with { ActiveMainQuests = [accepted with { QuestId = 101 }] },quest) == 1,
                "归零绑定同qid真实接取，不按职业、槽或其他任务的身份放行：job"+job);
        }
        That(QuestStateProgress.Refresh([accepted],new(1,0,"NoAccept",1,new(38,0,450,250),null),catalog)
            .Single().ConditionState == 1,"单独传入活动数组不能凭空签发角色接取事实");
        Console.WriteLine($"PASS {Count-checks} play-only接取事实与type49纯领域断言");
    }

    public static async Task RunAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog? sharedQuests = null,SkillLearningCatalog? sharedSkills = null)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(),"S5A9-play-only-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var log = new ServerLog(root);
            var actual = sharedQuests ?? global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf); var learning = sharedSkills ?? global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
            var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning); var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
            var computer = new CharacterStatComputer(characters);
            var books = actual.All.Where(quest => quest.Condition == "[play only]").ToArray();
            That(books.Length == 50,"匹配PVF的50条有效play-only定义，不将其他阅读或物品入口计作闭环");
            foreach (var quest in books)
            {
                That(QuestStateProgress.Type(quest) == 49 && quest.Grade == "[side]" &&
                    quest.Definition is { AccountQuest: true, Diagnostics.Length: 0 } &&
                    QuestCoverage.HasOrdinaryDeclarations(quest) && quest.RewardType == "[item]" &&
                    quest.Rewards is [{ Type: 0, Value: 0 },{ Type: 0, Value: 0 }],
                    "故事书真实目标、账号声明与item0保留为普通交付资源："+quest.Id);
                var declaredLevels = quest.Definition!.Fields.Single(field => field.Name == "level").Values;
                That(declaredLevels is [{ Type: 0,Text: null },{ Type: 0,Text: null }] &&
                    quest.MinimumLevel == declaredLevels[0].Value && quest.MaximumLevel == declaredLevels[1].Value &&
                    quest.MinimumLevel is >= 1 and <= 95 && quest.MaximumLevel >= quest.MinimumLevel,
                    "各故事书保留自己的原始等级范围，不从单一样例推广："+quest.Id);
                That(Enumerable.Range(0,16).All(job => new byte[] {(byte)quest.MinimumLevel,(byte)Math.Min(95,quest.MaximumLevel)}.All(level =>
                    QuestEligibility.Evaluate(new(1,0,"Book",1,towns.Room,null) { Job = (byte)job,Level = level },quest,actual).Eligible)),
                    "故事书按原等级和all职业资源资格展示，不加专用职业白名单："+quest.Id);
                That(quest.MinimumLevel == 1 || Enumerable.Range(0,16).All(job =>
                    !QuestEligibility.Evaluate(new(1,0,"Book",1,towns.Room,null) { Job = (byte)job,Level = (byte)(quest.MinimumLevel-1) },quest,actual).Eligible),
                    "未达到该故事书真实最低等级时不能提前接取："+quest.Id);
                var experience = QuestExperiencePolicy.Prepare(new(1,0,"Book",1,towns.Room,null) { Level = (byte)quest.MinimumLevel },quest,parameters);
                That(quest.Difficulty == "g" && experience.DifficultyKey == "g" && experience.DifficultyWeight == parameters.Difficulty["G"] &&
                    experience.NativeIndex == quest.MinimumLevel,
                    "真实小写g按两端大小写合同命中原G表项，保留资源键与自身等级索引："+quest.Id);
            }
            await RoleAsync(false);
            await RoleAsync(true);
            await AccountRollbackAsync();
            LostReturn();
            await SendFailureAsync();

            QuestOrdinaryCompletionTests.Fixture Fixture(StoryQuest quest,bool loseResult = false) =>
                new(towns,characters,learning,growth,parameters,items,quest,materialCount:0,active:[],loseResult:loseResult);
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

            async Task RoleAsync(bool zeroExperience)
            {
                var quest = PlayQuest() with { Attributes = zeroExperience ? ["not give exp quest"] : [] };
                using var f = Fixture(quest); var commands = Registry(f); var original = f.Read();
                using var stop = new CancellationTokenSource(); using var output = new ControlledOutput();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop);
                That(await commands.DispatchAsync(context,Finish()) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {34}) && Same(original,f.Read()) && f.Receipts == 0,
                    "未接取不能因type49空参数直接完成或领奖");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Identity(31)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {31,21,356}),"真实31接取play-only侧边任务");
                var first = f.Read(); var firstActive = first.ActiveMainQuests.Single();
                That(firstActive.ConditionState == 0 && firstActive.AcceptanceId != Guid.Empty &&
                    first.CompletedQuests.Length == 0 && f.Receipts == 0 && first.Revision == original.Revision+1,
                    "接取事务产生独立身份并归零，不先写完成位或发奖");
                Bytes(output.Frames[0].AsSpan(16),[1,100,0,0,0,0,0,0],"ACK31真实scalar0与空接取赠品数组");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(Same(first,f.Session.Selected!) && f.Session.Selected!.ActiveMainQuests.Single().ConditionState == 0,
                    "未发送33也能重登恢复实际接取归零，不依赖客户端重放成功31");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Identity(32)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {32,21,356}) && f.Read().ActiveMainQuests.Length == 0,
                    "普通可放弃侧边任务走既有32规则清槽");
                output.Frames.Clear();
                await commands.DispatchAsync(context,Identity(31));
                var accepted = f.Read(); var active = accepted.ActiveMainQuests.Single();
                That(active.ConditionState == 0 && active.AcceptanceId != Guid.Empty && active.AcceptanceId != firstActive.AcceptanceId,
                    "放弃重接生成新的接取身份，旧接取事实不复用");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Trigger()) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {33}) && Same(accepted,f.Read()),
                    "原生FF分支的真实6B归零33只重算，无额外保存或完成");
                Bytes(output.Frames.Single().AsSpan(16),[1,100,0,0,0,0,0],"ACK33回真实scalar0，不添加FF选择符");
                foreach (var packet in new[] { new Packet(1,33,[33,0,100,0,0x10,0,0]),new Packet(1,33,[33,0,100,0,1,0]) })
                {
                    output.Frames.Clear();
                    That(await commands.DispatchAsync(context,packet) == CommandResult.Handled && Same(accepted,f.Read()) &&
                        output.Opcodes.SequenceEqual(new ushort[] {33}),"三通道或客户端非零值不能改写play-only状态");
                    Bytes(output.Frames.Single().AsSpan(16),[0,19,0],"非法play-only上报中立拒绝");
                }
                var plan = f.Prepare(ushort.MaxValue).Plan!;
                var expectedExperience = QuestExperiencePolicy.Prepare(accepted,quest,parameters);
                That(plan.ConditionType == 49 && plan.AuthoritativeClear is null && plan.Rewards.Length == 0 &&
                    plan.ExperienceReward == expectedExperience && (zeroExperience ? expectedExperience.Amount == 0 : expectedExperience.Amount > 0) &&
                    plan.GoldAmount == parameters.Gold[1],"item[0,0]只表示无物品，XP与金币仍使用普通参数规则及显式零XP属性");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Finish()) == CommandResult.Handled,"真实34提交type49普通交付");
                var after = f.Read(); var ack = output.Frames.First().AsSpan(16);
                That(output.Opcodes.First() == 34 && output.Opcodes.Last() == 356 && ack.Length == 29 &&
                    ack[0] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(ack[1..]) == 100 && ack[3] == 49 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(ack[4..]) == expectedExperience.Amount && ack[8] == 0 && ack[9] == 1 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(ack[10..]) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(ack[12..]) == 0 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(ack[16..]) == plan.GoldAmount,
                    "ACK34 type49普通mode0，XP与唯一真实Gold奖励行，无清图消耗或专用零奖励协议");
                That(after.CompletedQuests.SequenceEqual([100]) && after.ActiveMainQuests.Length == 0 && after.Revision == accepted.Revision+1 &&
                    after.Inventory.Gold == accepted.Inventory.Gold+plan.GoldAmount &&
                    after.Learning.Experience == accepted.Learning.Experience+expectedExperience.Amount &&
                    output.Opcodes.Contains((ushort)37) == !zeroExperience && f.Receipts == 1 && Same(after,f.Session.Selected!),
                    "正常XP/Gold、完成位和领奖回执一次提交，零XP不额外发布37");
                Bytes(output.Frames.Last().AsSpan(16),TownPackets.CompletedQuests(after),"最后356保留真实故事任务完成位");
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Finish()) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {34}) && Same(after,f.Read()) && f.Receipts == 1,
                    "普通重复34不重发奖励、成长或任务增量");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(Same(after,f.Session.Selected!) && f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.AlreadyCompleted,
                    "重登保留普通type49完成与资产，不重新接管旧接取");
            }

            async Task AccountRollbackAsync()
            {
                var quest = PlayQuest() with { Rewards = [new(0,2058,null),new(0,1,null)],DeclaredGoldMultiple = 100,
                    Definition = new([]) { Job = "[all]",AccountQuest = true } };
                using var f = Fixture(quest); var commands = Registry(f);
                var alternate = f.Repository.Create(f.Owner,CharacterCreation.Prepare("BookAlt",characters.Get(15),towns.Room,towns.InitialReturn,learning.Get));
                var third = f.Repository.Create(f.Owner,CharacterCreation.Prepare("BookThird",characters.Get(14),towns.Room,towns.InitialReturn,learning.Get));
                That(f.Change(100,QuestLifecycleAction.Accept)?.Commit is not null,"账号故事任务第一角色真实接取");
                f.Session.ReturnToRoster(); f.Session.Select(alternate.Slot); f.Session.MarkReady();
                That(f.Change(100,QuestLifecycleAction.Accept)?.Commit is not null,"同账号第二职业真实接取同任务");
                alternate = f.Repository.LoadCharacter(f.Owner,alternate.Slot);
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                var before = f.Read(); var plan = f.Prepare(ushort.MaxValue).Plan!;
                long uidSequence = Scalar(f,"SELECT coalesce(max(seq),0) FROM sqlite_sequence WHERE name='character_inventory_items'");
                f.Sql("CREATE TRIGGER play_only_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'play-only rollback'); END");
                using var stop = new CancellationTokenSource(); using var output = new ControlledOutput();
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                var context = Context(f,output,sender,stop);
                await RejectAsync<SqliteException>(() => commands.DispatchAsync(context,Finish()),"账号故事任务回执末段SQL失败");
                That(output.Frames.Count == 0 && Same(before,f.Read()) && Same(alternate,f.Repository.LoadCharacter(f.Owner,alternate.Slot)) &&
                    Same(third,f.Repository.LoadCharacter(f.Owner,third.Slot)) && f.Receipts == 0 &&
                    Scalar(f,"SELECT count(*) FROM account_completed_quests") == 0 &&
                    uidSequence == Scalar(f,"SELECT coalesce(max(seq),0) FROM sqlite_sequence WHERE name='character_inventory_items'"),
                    "库存UID、余额XP、个人/账号完成位、跨角色活动槽与回执整笔回滚，失败前不发包");
                f.Sql("DROP TRIGGER play_only_abort");
                That(await commands.DispatchAsync(context,Finish()) == CommandResult.Handled,"SQL回滚后沿冻结type49计划恢复正常提交");
                var completed = f.Read(); var other = f.Repository.LoadCharacter(f.Owner,alternate.Slot);
                That(completed.AccountCompletedQuests.SequenceEqual([100]) && completed.CompletedQuests.SequenceEqual([100]) &&
                    completed.Inventory.Gold == before.Inventory.Gold+plan.GoldAmount &&
                    completed.Inventory.Items.Sum(item => item.Core.ItemId == 2058 ? item.Core.Count : 0) ==
                        before.Inventory.Items.Sum(item => item.Core.ItemId == 2058 ? item.Core.Count : 0)+1 &&
                    other.AccountCompletedQuests.SequenceEqual([100]) && other.CompletedQuests.Length == 0 && other.ActiveMainQuests.Length == 0 &&
                    Same(alternate with { Revision = alternate.Revision+1,ActiveMainQuests = [],AccountCompletedQuests = [100] },other) && f.Receipts == 1,
                    "一次账号领奖只发给实际角色，原子清除其他角色同任务活动槽，不伪造其个人完成或奖励");
                f.Session.ReturnToRoster(); f.Session.Select(third.Slot); f.Session.MarkReady();
                var current = f.Session.Selected!; output.Frames.Clear();
                That(await commands.DispatchAsync(context,Identity(31)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {31}) && current.AccountCompletedQuests.SequenceEqual([100]) &&
                    Same(current,f.Repository.LoadCharacter(f.Owner,third.Slot)) && f.Receipts == 1,
                    "第三角色重选共享完成位，真实31不能重接已完成账号任务");
                Bytes(output.Frames.Single().AsSpan(16),[0,18,0],"账号故事任务重复接取中立拒绝");
                Bytes(TownPackets.CompletedQuests(current).AsSpan(104,1),[1],"账号完成位通过既有356投影回放");
            }

            void LostReturn()
            {
                using var f = Fixture(PlayQuest(),loseResult:true);
                f.Change(100,QuestLifecycleAction.Accept); var before = f.Session.Selected!;
                DfoServer.Testing.TestCheck.Reject<IOException>(() => f.Finish(ushort.MaxValue),"type49事务提交后返回丢失");
                var saved = f.Read();
                That(ReferenceEquals(before,f.Session.Selected) && saved.CompletedQuests.SequenceEqual([100]) && f.Receipts == 1,
                    "丢返回保留原在线快照与真实已提交type49事务");
                var recovered = f.Finish(ushort.MaxValue).Receipt!.Commit;
                That(recovered is { Duplicate: true,ProjectionCurrent: true,ConditionType: 49 } && f.Lost!.SamePlan &&
                    Same(saved,recovered.Character) && Same(saved,f.Session.Selected!) && f.Receipts == 1,
                    "相同34恢复冻结结果，不重新计算XP/Gold或第二次发奖");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(Same(saved,f.Session.Selected!) && f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.AlreadyCompleted && f.Receipts == 1,
                    "丢返回恢复后重登只读取持久完成位和唯一回执");
            }

            async Task SendFailureAsync()
            {
                using var f = Fixture(PlayQuest()); f.Change(100,QuestLifecycleAction.Accept);
                var commands = Registry(f); using var stop = new CancellationTokenSource();
                using var output = new ControlledOutput { FailOn = 2 };
                await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
                await RejectAsync<IOException>(() => commands.DispatchAsync(Context(f,output,sender,stop),Finish()),
                    "type49 ACK34成功后库存绝对投影发送失败");
                var saved = f.Read();
                That(output.Opcodes.SequenceEqual(new ushort[] {34}) && stop.IsCancellationRequested &&
                    saved.CompletedQuests.SequenceEqual([100]) && Same(saved,f.Session.Selected!) && f.Receipts == 1,
                    "发送失败不撤销已提交XP/Gold或重复发奖");
                f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
                That(Same(saved,f.Session.Selected!) && f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.AlreadyCompleted,
                    "发送失败后重选从完整持久快照恢复，不重发领奖增量");
            }
        }
        finally { TestFiles.DeleteDirectory(root,true); }
        Console.WriteLine($"PASS {Count-checks} 50条play-only资源及合成接取/交付/账号/回滚/恢复机制断言（非主线或客户端验收）");
    }

    static StoryQuest PlayQuest() => QuestEligibilityTests.Quest(100) with
    {
        Condition = "[play only]",IntData = [],Grade = "[side]",CompleteNpc = -1,Difficulty = "g",
        Rewards = [new(0,0,null),new(0,0,null)]
    };
    static ushort Opcode(byte[] frame) => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1));
    static Packet Identity(ushort opcode) => new(1,opcode,[(byte)opcode,(byte)(opcode>>8),100,0]);
    // FF为native producer的选择符；线上没有额外FF字节。
    static Packet Trigger() => new(1,33,[33,0,100,0,0,0]);
    static Packet Finish() => new(1,34,[34,0,100,0,255,255,1,0,255,255]);
    static GameConnectionContext Context(QuestOrdinaryCompletionTests.Fixture fixture,Stream output,
        GameConnectionSender sender,CancellationTokenSource stop) =>
        new(new(89,IPAddress.Loopback,output,stop),fixture.Owner,fixture.Session,sender);
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
            if (++writes == FailOn) throw new IOException("play-only发布故障");
            Frames.Add(buffer.ToArray()); return ValueTask.CompletedTask;
        }
    }
}
