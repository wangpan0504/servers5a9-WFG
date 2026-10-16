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
            var before = f.Session.Selected!;
            f.Sql("CREATE TRIGGER communication_fail AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'communication rollback'); END");
            Reject<SqliteException>(() => f.Use(),"receipt晚期SQL失败");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()) && QuestCompletionRepositoryTests.Same(before,f.Session.Selected!) && f.Receipts == 0,
                "晚期失败回滚金币、寻物进度和revision，不安装通讯来源");
            AssertFrozen(f,before);
            That(f.Condition() is null,"SQL失败后的临时NPC不可用，原付款优先恢复");
            f.Sql("DROP TRIGGER communication_fail");
            That(f.Use() is {Accepted:true} && f.Lost!.SameLifecyclePlan && f.Read().Inventory.Gold == 40000 && f.Receipts == 1,
                "冻结付款计划恢复后只扣一次，仍为原Plan");
            That(f.Session.Move(new(51,52,0,0,0,0)) && f.Condition()?.Outcome == QuestLifecycleOutcome.Committed,
                "付款恢复后普通移动和会面保存恢复，不冻结成功来源缓存");
        }
        using (var f = new Fixture(lose:true))
        {
            var before = f.Session.Selected!;
            Reject<IOException>(() => f.Use(),"真实SQL提交后返回结果丢失");
            AssertFrozen(f,before);
            That(f.Read().Inventory.Gold == 40000 && f.Session.Selected!.Inventory.Gold == 50000 &&
                f.Condition() is null,"丢结果不提前安装来源或成功快照");
            var recovered = f.Use();
            That(recovered is {Accepted:true,Commit.Duplicate:true,Commit.ProjectionCurrent:true} && f.Lost!.SameLifecyclePlan &&
                f.Read().Inventory.Gold == 40000 && f.Receipts == 1,"按原operation恢复原付款receipt，不再次扣费");
        }
        using (var f = new Fixture())
        {
            var before = f.Session.Selected!;
            f.Sql("CREATE TRIGGER communication_reverse_fail AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'lifecycle rollback'); END");
            Reject<SqliteException>(() => f.Change(QuestLifecycleAction.Giveup),"普通32失败留下原任务计划");
            That(f.Use() is null && ReferenceEquals(before,f.Session.Selected) && f.Receipts == 0 &&
                QuestCompletionRepositoryTests.Same(before,f.Read()),"通讯不能覆盖未决普通生命周期");
            f.Sql("DROP TRIGGER communication_reverse_fail");
            That(f.Change(QuestLifecycleAction.Giveup)?.Commit is {ProjectionCurrent:true} && f.Lost!.SameLifecyclePlan &&
                f.Receipts == 1,"普通生命周期按原Plan恢复，不被475替换");
        }
        using (var f = new Fixture())
        {
            var before = f.Session.Selected!; int calls = 0;
            f.Sql("CREATE TRIGGER communication_interleave_fail AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'communication planning rollback'); END");
            var result = f.Session.PlanAndCommitInventory(character =>
            {
                calls++;
                Reject<SqliteException>(() => f.Use(),"库存锁外规划期间通讯提交失败");
                return DfoServer.Game.Currency.CurrencyService.SetGold(character,60000);
            });
            That(!result.Accepted && calls == 1 && ReferenceEquals(before,f.Session.Selected) &&
                QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Receipts == 0,
                "库存回锁再次检查通讯冻结，不能覆盖原付款快照");
            f.Sql("DROP TRIGGER communication_interleave_fail");
            That(f.Use() is {Accepted:true} && f.Lost!.SameLifecyclePlan && f.Receipts == 1 &&
                f.Session.PlanAndCommitInventory(character => DfoServer.Game.Currency.CurrencyService.SetGold(character,60000)).Accepted &&
                f.Read().Inventory.Gold == 60000,"通讯原Plan恢复后库存可重新规划提交");
        }
        using (var f = new Fixture(lose:true))
        {
            var before = f.Session.Selected!;
            Reject<IOException>(() => f.Use(),"建立提交成功但返回丢失的通讯计划");
            var paid = f.Read();
            f.Repository.Save(f.Owner,paid,paid.Position with { X = 51 },paid.Return);
            var actual = f.Read();
            var stale = f.Use()!;
            That(stale.Commit is {Duplicate:true,ProjectionCurrent:false} && f.Lost!.SameLifecyclePlan &&
                ReferenceEquals(before,f.Session.Selected) && !f.Session.CanPublishImageCommunication(stale),
                "外部写入后的旧付款回执不能安装或发布");
            AssertFrozen(f,before);
            That(f.Use()?.Commit is {Duplicate:true,ProjectionCurrent:false} && f.Lost!.SameLifecyclePlan &&
                QuestCompletionRepositoryTests.Same(actual,f.Read()) && f.Receipts == 1,
                "失效投影仍保留原Plan，不清pending或再次扣费");
        }
        using (var f = new Fixture(renameTicket:true))
        {
            var catalog = AdventureCoreTests.SyntheticCatalog(); var now = DateTimeOffset.UtcNow;
            var service = new DfoServer.Game.Adventure.AdventureNameService(catalog,
                id => id == 2000 ? new(2000,1,95,ushort.MaxValue) : null);
            f.Repository.InitializeAdventure(f.Owner,catalog,now);
            var before = f.Session.Selected!;
            var adventure = f.Repository.ReadAdventure(f.Owner,catalog,now);
            That(service.Prepare(adventure,before,"RecoveredGroup",false,65,0,now).Plan is {Inventory:not null},
                "真实改名券与冒险团目录可规划角色库存写入，非无效参数拒绝对照");
            f.Sql("CREATE TRIGGER communication_adventure_fail AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'communication adventure rollback'); END");
            Reject<SqliteException>(() => f.Use(),"冒险团写入前建立通讯付款回滚");
            That(!f.Session.ChangeAdventureName(service,"RecoveredGroup",false,65,0,now).Accepted &&
                ReferenceEquals(before,f.Session.Selected) && QuestCompletionRepositoryTests.Same(before,f.Read()) &&
                f.Session.ReadAdventureName() == "" &&
                f.Repository.ReadAdventure(f.Owner,catalog,now).State.Revision == adventure.State.Revision,
                "未决通信拒绝可执行冒险团改名，不消费券、改名或覆盖原角色");
            f.Sql("DROP TRIGGER communication_adventure_fail");
            That(f.Use() is {Accepted:true} && f.Lost!.SameLifecyclePlan && f.Receipts == 1,
                "冒险团拒绝后通信仍按原Plan恢复一次");
            var paid = f.Session.Selected!;
            That(f.Session.ChangeAdventureName(service,"RecoveredGroup",false,65,0,now).Accepted &&
                f.Session.ReadAdventureName() == "RecoveredGroup" && f.Read().Inventory.Find(new(InventoryListType.Main,65)) is null &&
                f.Read().Inventory.Gold == 40000 && f.Read().Revision == paid.Revision+1 &&
                QuestCompletionRepositoryTests.Same(f.Read(),f.Session.Selected!) && f.Receipts == 1,
                "通信恢复后真实冒险团改名消费券并安装新快照，不重复付款");
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

    static void AssertFrozen(Fixture f, Character expected)
    {
        var persisted = f.Read(); long receipts = f.Receipts; int calls = 0;
        That(!f.Session.Move(new(51,52,0,0,0,0)) && !f.Session.Travel(new(14,2,50,50,0,14,1,0)),
            "未决通讯禁止走动和切图，保留原场景及快照");
        foreach (bool town in new[] { false,true })
        {
            That(!f.Session.MutateInventory(character => { calls++; return InventoryResult.Reject(); },requireTown:town).Accepted &&
                !f.Session.PlanAndCommitInventory(character => { calls++; return InventoryResult.Reject(); },requireTown:town).Accepted,
                "未决通讯拒绝城镇及通用库存入口");
        }
        That(calls == 0 && f.Change(QuestLifecycleAction.Giveup) is null && f.Change(QuestLifecycleAction.Accept) is null &&
            f.Condition() is null && f.Session.FinishQuest(3734,ushort.MaxValue,f.Quests,null!,null!,null!,null!,null!).Outcome ==
                QuestFinishOutcome.Unsupported,"通信冻结先于普通31/32/33/34规划");
        // 这些入口须在读取资源前拒绝；null目录证明不会进入锁外规划或直接库存提交。
        That(f.Session.QuickClearQuest(1,f.Quests,null!) is null && f.Session.PrepareManualQuestSkip(f.Quests) is null &&
            f.Session.AnswerManualQuestSkip([],f.Quests) is null && f.Session.FinishManualQuestSkip() is null &&
            !f.Session.RentEquipment(null!,1,0,0,DateTimeOffset.UtcNow).Accepted &&
            !f.Session.UpdateTitleAchievements(null!).Accepted &&
            f.Session.UseConsumable(new(new(InventoryListType.Main,0),0,1,0),null!).Outcome == ConsumableUseOutcome.Rejected,
            "未决通信阻止跳过准备/提交、租赁、称号和直接消费写入");
        That(f.Session.BeginDungeonSelection() is null && f.Session.BeginClassExperienceSelection() is null &&
            f.Session.BeginTutorialSelection(1,null!) is null && f.Session.EnterFormalDungeon(1,0,0,null!,null!) is null &&
            f.Session.Phase == SessionPhase.Town,"选择和直接入场不能隐式清除未决通信");
        Reject<InvalidOperationException>(() => f.Session.SetCharacterGold(expected.Name,60000),"未决通信拒绝GM金币覆盖");
        That(f.Session.AcceptStoryDigest() is null && ReferenceEquals(expected,f.Session.Selected) &&
            QuestCompletionRepositoryTests.Same(persisted,f.Read()) && f.Receipts == receipts,
            "全部拒绝保持在线原引用、持久资产和回执数量");
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
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills); var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
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
        public Fixture(uint gold=50000,bool source=true,uint scalar=1,bool lose=false,bool active=true,bool renameTicket=false)
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
            if (renameTicket) new DfoServer.Game.Auth.AuthenticationService(new AuthenticationRepository(path))
                .Create("communication","fixture-password");
            Repository.Create(Owner,new("ImageTest",point,point)
            {Inventory=InventorySnapshot.Empty with {Gold=gold,Items=renameTicket ?
                [new(0,new(InventoryListType.Main,65),new(2000,ItemKind.Consumable,1))] : []},ActiveMainQuests=active ?
                [new(0,3734,scalar),new(1,100,gold>=45000 ? 0u : 1u)] : [new(1,100,gold>=45000 ? 0u : 1u)]});
            Lost=new(Repository){LoseLifecycleResult=lose,LoseCompletionResult=false};
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
