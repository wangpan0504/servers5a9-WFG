using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;
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
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning); var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
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
                output.Opcodes.SequenceEqual(new ushort[] {31,21,356}) && !f.Session.CanPublishQuestHidden(opening),
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
        await VerifyClearedDialoguePackets(towns,characters,items,actual,learning,growth,parameters);
        Console.WriteLine($"PASS {Count-checks} 隐藏884/822、真实12071正式Handler包序及公共接取交付断言（内存发送器，非socket）");
    }

    static async Task VerifyClearedDialoguePackets(TownCatalog towns,CharacterCatalog characters,ItemResources items,
        QuestCatalog quests,SkillLearningCatalog learning,CharacterGrowthCatalog growth,QuestRewardParameterCatalog parameters)
    {
        const ushort id = 12071;
        var quest = quests.Get(id);
        var done = towns.AccessQuestIds.Concat(quest.Prerequisites).Where(value => value!=id).Distinct().ToArray();
        var bounds = towns.Areas[((byte)14,(byte)1)].Map(done.ToHashSet()).Bounds
            .First(row => row.Conditions.Length==0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(14,1,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        using var f = new QuestSceneTests.Fixture(towns,characters,learning,growth,quests,false,level:95,completed:done,position:position);
        string root=Path.Combine(Path.GetTempPath(),"S5A9-cleared-hidden-wire-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var log = new ServerLog(root);
            var projection=new QuestProjection(quests); var commands=new GameCommandRegistry();
            new QuestHiddenHandler(quests,log).Register(commands);
            new QuestLifecycleHandler(quests,items.Items,items.GoldLimits,items.EpicPieces,log,projection).Register(commands);
            new QuestConditionHandler(log,quests,items.Items,items.GoldLimits,items.EpicPieces,parameters:parameters,growth:growth).Register(commands);
            new QuestCompletionHandler(quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,log,
                new CharacterLevelProjection(new CharacterStatComputer(characters),projection),projection).Register(commands);
            commands.Seal(); using var stop=new CancellationTokenSource(); using var output=new Output();
            await using var sender=new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_)=>{});
            var context=new GameConnectionContext(new(92,IPAddress.Loopback,output,stop),f.Owner,f.Session,sender);
            Packet Accept()=>new(1,31,[31,0,(byte)(id&255),(byte)(id>>8)]);
            Packet Trigger()=>new(1,33,[33,0,(byte)(id&255),(byte)(id>>8),0,0]);
            Packet Finish()=>new(1,34,[34,0,(byte)(id&255),(byte)(id>>8),255,255,1,0,255,255]);
            var before=f.Read();
            That(await commands.DispatchAsync(context,Accept())==CommandResult.Handled && f.Read().Revision==before.Revision &&
                output.Opcodes.SequenceEqual(new ushort[]{31}),"12071正式31无884许可不得入库");
            output.Frames.Clear();
            That(await commands.DispatchAsync(context,Request(id,58))==CommandResult.Handled &&
                output.Opcodes.SequenceEqual(new ushort[]{884}) && f.Read().Revision==before.Revision,
                "12071错误对话NPC正式884只ACK不发送822");
            output.Frames.Clear();
            That(await commands.DispatchAsync(context,Request(id,76))==CommandResult.Handled &&
                output.Opcodes.SequenceEqual(new ushort[]{884,822}) && f.Read().Revision==before.Revision,
                "12071真实NPC76正式884/822只开放候选");
            output.Frames.Clear();
            That(await commands.DispatchAsync(context,Accept())==CommandResult.Handled &&
                output.Opcodes.SequenceEqual(new ushort[]{31,21,356}) &&
                f.Read().ActiveMainQuests.Single(row=>row.QuestId==id).ConditionState==1,
                "12071真实31包序保持初值1及历史同步");
            output.Frames.Clear();
            That(await commands.DispatchAsync(context,Trigger())==CommandResult.Handled &&
                output.Opcodes.SequenceEqual(new ushort[]{33}) && f.Read().ActiveMainQuests.Single(row=>row.QuestId==id).ConditionState==0,
                "12071原客户端6B自动33通过当前NPC事实完成，不需要伪造255通道");
            var revision=f.Read().Revision;
            output.Frames.Clear();
            That(await commands.DispatchAsync(context,Trigger())==CommandResult.Handled && f.Read().Revision==revision,
                "12071重复正式33不重复提交");
            output.Frames.Clear();
            var beforeReward=f.Read();
            That(quest.MinimumLevel==90 && beforeReward.Level==95 && quest.Grade=="[hidden]" &&
                quest.DeclaredIgnoreLevelForExperience is null or 0 && quest.DeclaredGoldMultiple is null,
                "12071原等级90、角色95、非史诗惩罚及缺省金币倍率资源合同");
            var green=parameters.Penalty(false,false); var grey=parameters.Penalty(false,true);
            int penalty=5>=grey.MinimumDifference ? grey.Percent :
                5>=green.MinimumDifference && 5<=green.MaximumDifference ? green.Percent : parameters.BasicPenalty;
            uint expectedGold=checked((uint)((ulong)parameters.Gold[90]*100/100*(ulong)penalty/100));
            That(expectedGold>0,"12071原0/0金币marker按真实G参数产生正金币，不是零奖励占位");
            That(await commands.DispatchAsync(context,Finish())==CommandResult.Handled && output.Opcodes.SequenceEqual(new ushort[]{34,14,21,356}) &&
                f.Read().CompletedQuests.Contains(id) && f.Read().ActiveMainQuests.All(row=>row.QuestId!=id),
                "12071正式34复用原奖励和完成历史：opcodes="+string.Join(',',output.Opcodes)+
                $"; completed={f.Read().CompletedQuests.Contains(id)}; revision={f.Read().Revision}");
            var rewarded=f.Read();
            That(rewarded.Inventory.Gold==checked(beforeReward.Inventory.Gold+expectedGold) &&
                rewarded.Inventory.Items.SequenceEqual(beforeReward.Inventory.Items),
                "12071原参数金币完整入账，无额外物品");
            Bytes(output.Frames[1].AsSpan(16),InventoryPackets.Delta(rewarded.Inventory,InventoryListType.Main,
                [new InventorySlot(InventoryListType.Main,0)]),"12071的14严格携带真实提交后的金币槽绝对值");
            revision=rewarded.Revision; output.Frames.Clear();
            That(await commands.DispatchAsync(context,Finish())==CommandResult.Handled && f.Read().Revision==revision &&
                f.Read().Inventory.Gold==rewarded.Inventory.Gold,
                "12071重复正式34不再次发奖或增加金币");
        }
        finally { Directory.Delete(root,true); }
    }

    static StoryQuest Hidden() => QuestEligibilityTests.Quest(100) with
    {
        Grade = "[hidden]",Condition = "[seeking]",IntData = [10165055,1],CompleteNpc = -1,Difficulty = "g",
        Rewards = [new(0,2058,null),new(0,2,null)],Definition = new QuestDefinition([]) { Job = "[all]",ExposedByNpc = false }
    };
    static QuestCatalog Catalog(StoryQuest quest) => new(new() { [quest.Id] = quest });
    static Packet Request(uint questId,uint npcDialog = uint.MaxValue)
    {
        byte[] body = new byte[28];
        uint[] fields = [questId,npcDialog,123,456,0x80000000,789,987];
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
