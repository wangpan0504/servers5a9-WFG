using System.Buffers.Binary;
using System.Collections.Frozen;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using LostResultRepository = DfoServer.QuestCompletionRepositoryTests.LostResultRepository;

namespace DfoServer;

internal static class ImageCommunicationTests
{
    static ImageCommunicationCatalog Catalog => new(10000,20000,[1274,140,0],[1274,210,0],
        [new(2000,3734,5,55),new(2001,3741,5,42)], new uint[] {78}.ToFrozenSet());

    public static void RunCore()
    {
        int checks = Count;
        var strings = new Dictionary<int,string>(); var tokens = new List<PvfToken>();
        void Tag(string text) { strings[strings.Count] = text; tokens.Add(new(3,strings.Count-1)); }
        void Field(string text, params int[] values) { Tag(text); tokens.AddRange(values.Select(value => new PvfToken(0,value))); }
        Tag("[image communication]"); Field("[charge]",10000); Field("[summon time]",20000);
        Field("[summon pos]",1274,140,0); Field("[effect pos]",1274,210,0);
        Tag("[npc]"); Field("[npc index]",2000); Field("[npc pos]",5,55); Field("[require quest]",3734);
        Tag("[/npc]"); Tag("[/image communication]");
        var parsed = ImageCommunicationCatalog.Parse(tokens,id => strings[id],[78]);
        That(parsed.Charge == 10000 && parsed.SummonTime == 20000 && parsed.Npcs is [{NpcId:2000,RequiredQuest:3734}],
            "通讯资源费用是金币，时长与NPC要求独立保留");
        var negative = tokens.ToArray(); negative[2] = new(0,-1);
        Reject<InvalidDataException>(() => ImageCommunicationCatalog.Parse(negative,id => strings[id],[78]),"负费用不能变成退款");
        Reject<InvalidDataException>(() => ImageCommunicationCatalog.Parse(tokens.Take(tokens.Count-1).ToArray(),id => strings[id],[78]),
            "未闭合通讯块不能授予资格");
        That(ImageCommunicationCatalog.IsSource([new(3,0),new(6,1),new(0,0)],id => id == 0 ? "[role]" : "[image communication]") &&
            !ImageCommunicationCatalog.IsSource([new(3,0),new(6,1),new(0,1)],id => id == 0 ? "[role]" : "[image communication]"),
            "通讯来源读取真实role及参数0，不把其它NPC当装置");
        var body = ImageCommunicationPackets.Used(87654);
        That(body.Length == 9 && body[0] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1)) == 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5)) == 87654,"475成功完整9B，未知首u32与绝对金币不混淆");
        Bytes(ImageCommunicationPackets.NeutralRejected(),[0,0,0],"475中立失败完整flag0+u16=0，没有成功后缀或其它命令的错误码");
        using (var f = new Fixture())
        {
            var before = f.Read();
            That(f.Condition()!.Outcome == QuestLifecycleOutcome.Rejected && QuestCompletionRepositoryTests.Same(before,f.Read()),"未付款不能凭临时NPC推进33");
            var paid = f.Use()!;
            That(paid.Accepted && paid.Commit is {ProjectionCurrent:true} && paid.Character.Inventory.Gold == 40000 &&
                paid.Character.Revision == before.Revision+1 && paid.Character.ActiveMainQuests[0].ConditionState == 1 &&
                paid.Character.ActiveMainQuests[1].ConditionState == 1 && f.Receipts == 1,
                "付款、金币材料寻物重算和receipt同事务，会面仍保持未达成");
            That(f.Session.CanPublishImageCommunication(paid),"实际付款快照允许发布");
            var duplicate = f.Use()!;
            That(duplicate.Accepted && duplicate.Commit is null && f.Read().Inventory.Gold == 40000 && f.Receipts == 1,
                "相同场景同接取重复475不再次收费");
            That(f.Session.Move(new(51,52,0,0,0,0)),"原场景正常走动成功");
            That(!f.Session.CanPublishImageCommunication(paid),"走动后的新revision不能发布旧ACK余额");
            var condition = f.Condition()!;
            That(condition.Outcome == QuestLifecycleOutcome.Committed && condition.Character.ActiveMainQuests[0].ConditionState == 0 &&
                condition.Character.Inventory.Gold == 40000,"走动后仍沿已付款来源推进33，不永久绑定旧revision");
        }
        using (var f = new Fixture(gold:9999))
        {
            var before = f.Read();
            var rejected = f.Use()!;
            That(!rejected.Accepted && f.Session.CanPublishImageCommunication(rejected) &&
                QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Receipts == 0,"金币不足可发布中立失败，零扣款、零receipt及零来源");
            That(f.Condition()!.Outcome == QuestLifecycleOutcome.Rejected,"不足金币不能伪造NPC会面");
        }
        using (var f = new Fixture(source:false))
            That(f.Use() is {Accepted:false} && f.Receipts == 0,"有活动任务但当前地图没有真实装置时拒绝");
        using (var f = new Fixture(active:false))
            That(f.Use() is {Accepted:false} && f.Receipts == 0,"源NPC存在但没有require quest活动槽时拒绝");
        using (var f = new Fixture(scalar:0))
            That(f.Use() is {Accepted:true},"require quest只要求活动槽，已达成未交付仍能过滤到通讯NPC");
        using (var f = new Fixture())
        {
            f.Use();
            That(f.Session.Travel(new(14,2,50,50,0,14,1,0)) && f.Use() is {Accepted:false},
                "实际切图到无装置区域，不沿用原来源");
            That(f.Session.Travel(new(14,1,50,50,0,14,2,0)) && f.Condition()!.Outcome == QuestLifecycleOutcome.Rejected,
                "同Town内切图后返回相同Area也不复活旧来源");
        }
        using (var f = new Fixture())
        {
            f.Use();
            That(f.Session.BeginDungeonSelection() is not null && f.Session.ReturnFromDungeonSelection(false) is not null,
                "真实离开Town并返回相同地图");
            That(f.Condition()!.Outcome == QuestLifecycleOutcome.Rejected && f.Use()!.Character.Inventory.Gold == 30000,
                "回到同一Town/Area不复活旧来源，重新使用才收费");
        }
        using (var f = new Fixture())
        {
            f.Use(); var old = f.Read().ActiveMainQuests[0].AcceptanceId;
            That(f.Change(QuestLifecycleAction.Giveup)!.Commit is not null && f.Change(QuestLifecycleAction.Accept)!.Commit is not null &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 3734).AcceptanceId != old,
                "真实32/31放弃重接生成新AcceptanceId");
            That(f.Condition()!.Outcome == QuestLifecycleOutcome.Rejected,"旧付款的接取身份不能推进重接任务");
            f.Use();
            That(f.Condition()!.Outcome == QuestLifecycleOutcome.Committed,"重接后重新付款的真实来源可推进");
        }
        using (var f = new Fixture())
        {
            var before = f.Read();
            f.Sql("CREATE TRIGGER communication_fail AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'communication rollback'); END");
            Reject<SqliteException>(() => f.Use(),"receipt晚期SQL失败");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()) && QuestCompletionRepositoryTests.Same(before,f.Session.Selected!) && f.Receipts == 0,
                "晚期失败回滚金币、寻物进度和revision，不安装通讯来源");
            That(f.Condition()!.Outcome == QuestLifecycleOutcome.Rejected,"SQL失败后的临时NPC不可用");
            f.Sql("DROP TRIGGER communication_fail");
            That(f.Use() is {Accepted:true} && f.Read().Inventory.Gold == 40000 && f.Receipts == 1,"冻结付款计划恢复后只扣一次");
        }
        using (var f = new Fixture(lose:true))
        {
            Reject<IOException>(() => f.Use(),"真实SQL提交后返回结果丢失");
            That(f.Read().Inventory.Gold == 40000 && f.Session.Selected!.Inventory.Gold == 50000 &&
                f.Condition()!.Outcome == QuestLifecycleOutcome.Rejected,"丢结果不提前安装来源或成功快照");
            var recovered = f.Use();
            That(recovered is {Accepted:true,Commit.Duplicate:true,Commit.ProjectionCurrent:true} && f.Lost!.SameLifecyclePlan &&
                f.Read().Inventory.Gold == 40000 && f.Receipts == 1,"按原operation恢复原付款receipt，不再次扣费");
        }
        using (var f = new Fixture())
        {
            f.Use(); f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady();
            That(f.Condition()!.Outcome == QuestLifecycleOutcome.Rejected,"重新选角不继承旧通讯演员");
            using var replacement = f.Owners.Acquire(f.Owner.Account,() => { });
            Reject<InvalidOperationException>(() => f.Use(),"旧owner不能再次付款或发布来源");
        }
        Console.WriteLine($"PASS {Count-checks} 通讯资源、费用事务、会面来源及生命周期核心断言");
    }

    public static void RunResources(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count; var catalog = ImageCommunicationCatalog.Load(pvf,towns); var quest = quests.Get(3734);
        That(catalog.Charge == 10000 && catalog.SummonTime == 20000 && catalog.SourceNpcs.Contains(78) &&
            catalog.Npcs.Any(row => row is {NpcId:2000,RequiredQuest:3734}) &&
            catalog.Npcs.Any(row => row is {NpcId:2001,RequiredQuest:3741}),"共享真实资源保留两个任务及真实NPC78 role");
        That(quest is {Condition:"[meet npc]",SubType:-1} && quest.IntData.SequenceEqual([2000]) &&
            towns.Areas[(14,1)].Phases.Prepend(towns.Areas[(14,1)].Default).All(map =>
                map.Npcs.Any(npc => npc.Id == 78) && map.Npcs.All(npc => npc.Id != 2000)),"真实3734要求临时2000，三个phase均只有源78");
        var growth = CharacterGrowthCatalog.Load(pvf,skills); var parameters = QuestRewardParameterCatalog.Load(pvf);
        byte level = checked((byte)Math.Max(81,quest.MinimumLevel));
        using var f = new QuestOrdinaryCompletionTests.Fixture(towns,characters,skills,growth,parameters,items,quest,
            materialCount:0,definitions:quests.All.ToArray(),configure:character => character with
            {
                Level=level,AcceptedStoryLevel=level,
                InitialLearning=SkillPointLedger.Synchronize(0,0,level,character.InitialLearning with
                    { Experience=growth.GetLevelStartExperience(level) },skills),
                CompletedQuests=quest.Definition!.PrerequisiteGroups.Take(1).SelectMany(group => group).Append(3697).Distinct().ToArray(),
                Inventory=character.Inventory with {Gold=50000}
            });
        f.Session.ReturnToRoster(); var current = f.Read();
        var region = towns.Areas[(14,1)].Map(QuestEligibility.Completed(current)).Bounds.First(row =>
            row.Conditions.Length == 0 && row.Quests.All(QuestEligibility.Completed(current).Contains));
        var landing = new Location(14,1,checked((ushort)(region.Bounds.X+region.Bounds.Width/2)),
            checked((ushort)(region.Bounds.Y+region.Bounds.Height/2)));
        f.Repository.Save(f.Owner,current,landing,current.Return);
        f.Session.Select(0,resumeTown:true); f.Session.MarkReady();
        That(f.Change(3734,QuestLifecycleAction.Condition,new(null,0,0))!.Outcome == QuestLifecycleOutcome.Rejected,
            "真实任务未付款不接受2000会面");
        That(f.Session.UseImageCommunication(catalog,quests) is {Accepted:true} && f.Read().Inventory.Gold == 40000 &&
            f.Read().ActiveMainQuests.Single(row => row.QuestId == 3734).ConditionState == 1,"真实475付款不直接完成3734");
        That(f.Change(3734,QuestLifecycleAction.Condition,new(null,0,0))!.Outcome == QuestLifecycleOutcome.Committed,
            "真实3734通过公共33保存会面");
        var finished = f.Finish(ushort.MaxValue,3734);
        That(finished.Outcome == QuestFinishOutcome.Committed && f.Read().CompletedQuests.Contains(3734) &&
            f.Read().ActiveMainQuests.All(row => row.QuestId != 3734),"真实34按原奖励合同交付3734："+finished.Outcome+" / "+finished.Failure);
        That(f.Change(3735,QuestLifecycleAction.Accept)!.Outcome == QuestLifecycleOutcome.Committed &&
            f.Read().ActiveMainQuests.Any(row => row.QuestId == 3735),"3734交付后公共接取允许后继3735，不伪造物件材料完成");
        Console.WriteLine($"PASS {Count-checks} 共享通讯资源及3734→3735业务断言；不是TCP或Windows验收");
    }

    private sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(),"S5A9-communication-"+Guid.NewGuid().ToString("N"));
        readonly string path;
        readonly GoldLimitCatalog limits = new("[gold limit from level]\n0 100000\n1 100000\n[/gold limit from level]");
        readonly EpicPieceCatalog pieces = new("[piece list]\n1 2\n[/piece list]");
        public AccountOwners Owners {get;} = new();
        public AccountOwners.Lease Owner {get;}
        public CharacterRepository Repository {get;}
        public GameSession Session {get;}
        public QuestCatalog Quests {get;}
        public LostResultRepository? Lost {get;}
        public Fixture(uint gold=50000,bool source=true,uint scalar=1,bool lose=false,bool active=true)
        {
            path=Path.Combine(root,"game.db"); var point = new Location(14,1,50,50);
            var map = new TownMap("fixture/communication.map",[new(new(0,0,2000,1000),[],[])],
                [new(new(0,0,100,100),14,2,[],[])],false,source ? [new(78,1397,210)] : []);
            var area = new TownArea(14,1,1,[],[],[],point,map,[],[]);
            var other = area with { Area=2,Default=map with {Path="fixture/other.map",Npcs=[],Links=[new(new(0,0,100,100),14,1,[],[])]} };
            var towns = new TownCatalog(new(){[(14,1)]=area,[(14,2)]=other},point,point,new(0,[],[]),[],new Dictionary<uint,uint[]>());
            var meet = QuestEligibilityTests.Quest(3734) with {IntData=[2000]};
            var seek = QuestEligibilityTests.Quest(100) with {Condition="[seeking]",IntData=[0,45000]};
            Quests=new(new(){[3734]=meet,[100]=seek}); Repository=new(path,Quests); Owner=Owners.Acquire("communication",() => { });
            Repository.Create(Owner,new("ImageTest",point,point)
            {Inventory=InventorySnapshot.Empty with {Gold=gold},ActiveMainQuests=active ?
                [new(0,3734,scalar),new(1,100,gold>=45000 ? 0u : 1u)] : [new(1,100,gold>=45000 ? 0u : 1u)]});
            Lost=lose ? new(Repository){LoseLifecycleResult=true} : null;
            Session=new(Lost is null ? Repository : Lost,towns,Owner);
            Session.Select(0,resumeTown:true); Session.MarkReady();
        }
        public ImageCommunicationReceipt? Use() => Session.UseImageCommunication(Catalog,Quests);
        // 这些核心任务没有物品奖励，ItemCatalog不会被读取；实际资源流程使用共享完整物品目录。
        public QuestLifecycleReceipt? Condition() => Session.ChangeQuest(3734,QuestLifecycleAction.Condition,Quests,null!,limits,pieces,new(null,0,0));
        public QuestLifecycleReceipt? Change(QuestLifecycleAction action) => Session.ChangeQuest(3734,action,Quests,null!,limits,pieces);
        public Character Read() => Repository.LoadCharacter(Owner,0);
        public long Receipts {get {using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT count(*) FROM character_quest_lifecycle_receipts";return (long)c.ExecuteScalar()!;}}
        SqliteConnection Open() {var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();return db;}
        public void Sql(string sql) {using var db=Open();using var c=db.CreateCommand();c.CommandText=sql;c.ExecuteNonQuery();}
        public void Dispose() {Session.End();Owner.Dispose();Directory.Delete(root,true);}
    }
}
