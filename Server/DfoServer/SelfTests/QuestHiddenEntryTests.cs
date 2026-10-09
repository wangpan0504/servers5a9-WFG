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
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestHiddenEntryTests
{
    public static void RunCore()
    {
        int checks = Count;
        var quest = Hidden(); var catalog = Catalog(quest);
        for (byte job = 0; job < 16; job++)
        {
            var character = new Character(1,0,"Hidden",1,new(99,9,0,0),null) { Job = job };
            That(QuestEligibility.InspectResource(quest).Entry == QuestResourceEntry.Hidden &&
                QuestEligibility.Visible(character,catalog).Length == 0 && QuestEligibility.Candidates(character,catalog).SequenceEqual([100]) &&
                QuestHiddenEntry.CanOpen(character,quest,catalog),"全职业隐藏候选不以不存在的首次NPC限制开启：job"+job);
            That(!QuestHiddenEntry.CanOpen(character with { CompletedQuests = [100] },quest,catalog) &&
                !QuestHiddenEntry.CanOpen(character with { AccountCompletedQuests = [100] },quest,catalog) &&
                QuestEligibility.Candidates(character with { ActiveMainQuests = [new(0,100,1)] },catalog).Length == 0,
                "完成集与活动槽均阻止再次开启未接候选：job"+job);
        }
        var original = new Character(1,0,"Hidden",1,new(99,9,0,0),null);
        foreach (string field in new[] {"area","npcdialog","standbyarea","itemusecheck",
            "npc dialog","stand by area","item use check","reach the range","hidden set"})
        {
            var queued = quest with { Definition = quest.Definition! with { Fields = [new(field,[],[])] } };
            That(!QuestHiddenEntry.ImmediateShape(queued) && QuestEligibility.Candidates(original,Catalog(queued)).Length == 0,
                "有本地条件队列的资源不提前开启："+field);
        }
        foreach (var unavailable in new[]
        {
            quest with { MinimumLevel = 96 }, quest with { MinimumLevel = 254,MaximumLevel = 255 },
            quest with { Definition = quest.Definition! with { FirstExposedByNpc = 1 } },
            quest with { Definition = quest.Definition! with { IsEvent = true } },
            quest with { Definition = quest.Definition! with { CreatureKind = 1 } },
            quest with { Definition = quest.Definition! with { ExpertJobLevel = [1] } },
            quest with { Definition = quest.Definition! with { HardcoreOnly = true } },
            quest with { Definition = quest.Definition! with { HideOnCompletedQuests = [1] } },
            quest with { Condition = "[use skill]" }, quest with { Definition = quest.Definition! with { Diagnostics = ["无效定义"] } },
            quest with { Grade = "[epic]",Definition = quest.Definition! with { ExposedByNpc = false } }
        })
            That(!QuestHiddenEntry.CanOpen(original with { CompletedQuests = [1] },unavailable,Catalog(unavailable)),
                "未实现入口、特殊等级、领域前提及显示隐藏条件不被884放行");
        var prerequisite = quest with { Definition = quest.Definition! with { PrerequisiteGroups = [[101,102],[103]] } };
        var related = new QuestCatalog(new[] {prerequisite,QuestEligibilityTests.Quest(101),QuestEligibilityTests.Quest(102),
            QuestEligibilityTests.Quest(103)}.ToDictionary(row => row.Id));
        That(!QuestHiddenEntry.CanOpen(original with { CompletedQuests = [101] },prerequisite,related) &&
            QuestHiddenEntry.CanOpen(original with { CompletedQuests = [101,102] },prerequisite,related) &&
            QuestHiddenEntry.CanOpen(original with { AccountCompletedQuests = [103] },prerequisite,related),
            "884保持完整OR组和账号完成语义，客户端字段不能代替前置");
        Console.WriteLine($"PASS {Count-checks} 隐藏候选、开启资格与条件队列边界断言");
    }

    public static async Task RunAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog actual,SkillLearningCatalog learning)
    {
        int checks = Count;
        var growth = CharacterGrowthCatalog.Load(pvf,learning); var parameters = QuestRewardParameterCatalog.Load(pvf);
        var direct = actual.All.Where(quest => QuestHiddenEntry.Direct(quest,actual)).ToArray();
        That(direct.Length > 0 && direct.All(quest => quest.Grade == "[hidden]" && quest.MainQuest is null &&
            QuestCoverage.HasOrdinaryDeclarations(quest) && QuestCoverage.HasSupportedTarget(quest,actual)),
            "只读实际隐藏资源保留完整声明与目标，不推进主线或把条件队列猜成普通开启");
        Console.WriteLine("Direct hidden candidates: "+string.Join(',',direct.Select(quest => quest.Id)));
        string root = Path.Combine(Path.GetTempPath(),"S5A9-hidden-entry-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var log = new ServerLog(root);
            using var f = new QuestOrdinaryCompletionTests.Fixture(towns,characters,learning,growth,parameters,items,
                Hidden(),active:[],materialCount:5);
            var commands = new GameCommandRegistry(); var projection = new QuestProjection(f.Catalog);
            new QuestHiddenHandler(f.Catalog,log).Register(commands);
            new QuestLifecycleHandler(f.Catalog,items.Items,items.GoldLimits,items.EpicPieces,log,projection).Register(commands);
            commands.Seal(); using var stop = new CancellationTokenSource(); using var output = new Output();
            await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
            var context = new GameConnectionContext(new(91,IPAddress.Loopback,output,stop),f.Owner,f.Session,sender);
            var before = f.Read(); var opening = f.Session.ReadHiddenQuest(100,f.Catalog)!;
            That(opening.Open && f.Session.CanPublishQuestHidden(opening),"当前选角得到无状态隐藏开启回执");
            for (int repeat = 0; repeat < 2; repeat++)
            {
                output.Frames.Clear();
                That(await commands.DispatchAsync(context,Request(100)) == CommandResult.Handled &&
                    output.Opcodes.SequenceEqual(new ushort[] {884,822}) && Same(before,f.Read()) && f.Receipts == 0,
                    "重复884只发送开启，非零客户端上下文不产生进度、接取、奖励或数据库写入");
                Bytes(output.Frames[0].AsSpan(16),[],"ACK884没有伪造成功正文");
                Bytes(output.Frames[1].AsSpan(16),[1,0,0,0,100,0,0,0],"822只开启此次合格qid");
            }
            output.Frames.Clear();
            That(await commands.DispatchAsync(context,Request(101)) == CommandResult.Handled &&
                output.Opcodes.SequenceEqual(new ushort[] {884}) && Same(before,f.Read()),"缺失qid不生成822或修改数据库");
            output.Frames.Clear();
            That(await commands.DispatchAsync(context,new(1,31,[31,0,100,0])) == CommandResult.Handled &&
                output.Opcodes.SequenceEqual(new ushort[] {31,21}) && !f.Session.CanPublishQuestHidden(opening),
                "实际31仍用公共接取事务，旧开启回执随revision快照失效");
            var accepted = f.Read();
            That(accepted.ActiveMainQuests.Single() is { ConditionState: 0,AcceptanceId: var identity } && identity != Guid.Empty &&
                accepted.Revision == before.Revision+1 && f.Session.ReadHiddenQuest(100,f.Catalog) is { Open: false },
                "寻物初值来自库存，开启请求不改写新接取身份");
            var pending = f.Session.ReadHiddenQuest(101,f.Catalog)!;
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(!f.Session.CanPublishQuestHidden(pending) && Same(accepted,f.Session.Selected!),
                "重选相同角色也不能发送上次选角的开启回执");
            var finished = f.Finish(ushort.MaxValue);
            That(finished.Outcome == QuestFinishOutcome.Committed && f.Receipts == 1 &&
                f.Read().CompletedQuests.SequenceEqual([100]) && f.Read().ActiveMainQuests.Length == 0 &&
                f.Read().Inventory.Items.Single(row => row.Core.ItemId == 2058).Core.Count == 2,
                "隐藏任务复用普通材料扣除、奖励与唯一完成回执，不建第二套状态");
            output.Frames.Clear();
            That(await commands.DispatchAsync(context,Request(100)) == CommandResult.Handled &&
                output.Opcodes.SequenceEqual(new ushort[] {884}) && f.Receipts == 1,
                "完成后884不重新开放任务或重复发奖");
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine($"PASS {Count-checks} 隐藏884/822、公共接取交付与选角失效合成机制断言（不执行主线）");
    }

    static StoryQuest Hidden() => QuestEligibilityTests.Quest(100) with
    {
        Grade = "[hidden]",Condition = "[seeking]",IntData = [10165055,1],CompleteNpc = -1,Difficulty = "g",
        Rewards = [new(0,2058,null),new(0,2,null)],Definition = new QuestDefinition([]) { Job = "[all]",ExposedByNpc = false }
    };
    static QuestCatalog Catalog(StoryQuest quest) => new(new() { [quest.Id] = quest });
    static Packet Request(uint questId)
    {
        byte[] body = new byte[28];
        uint[] fields = [questId,uint.MaxValue,123,456,0x80000000,789,987];
        for (int at = 0; at < fields.Length; at++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at*4),fields[at]);
        return new(1,884,body);
    }
    sealed class Output : MemoryStream
    {
        public List<byte[]> Frames { get; } = [];
        public IEnumerable<ushort> Opcodes => Frames.Select(frame => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken = default)
        { Frames.Add(buffer.ToArray()); return ValueTask.CompletedTask; }
    }
}
