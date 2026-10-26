using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpPrivateCharacterTests
{
    public static void Run()
    {
        int before=Count;
        var location=new Location(10,1,10,10);
        var ordinary=new Character(1,0,"PrivateTest",0,location,location){Level=85};
        var special=ordinary with {PvpPrivate=new(30)};
        foreach(int id in new[]{2672,2673,2674,2676,2677,2678,2679})
        {
            int mode=id is 2673 or 2677 or 2679 ? 2 : id==2674 ? 1 : 0;
            int expose=id>=2675 ? id==2679 ? 2 : 1 : 0;
            var quest=QuestEligibilityTests.Quest(id) with {Condition="[custom multi condition]",Npc=323,CompleteNpc=-1,
                IntData=mode==2 ? [1,0,0,2] : [1,1,1,mode],Definition=new([new("pvp private expose",[new(0,expose,null)],[])])};
            That(PvpTrainingExposure.Allows(ordinary,quest)==(expose!=1) &&
                PvpTrainingExposure.Allows(special,quest)==(expose!=0),"普通与专用身份按已识别0/1/2分流，不依据角色等级");
            foreach(int invalid in new[]{-1,3,255})
                That(!PvpTrainingExposure.Allows(special,quest with {Definition=new([new("pvp private expose",[new(0,invalid,null)],[])])}),
                    "未知expose拒绝，不把非零统称专用");
            That(!PvpTrainingExposure.Allows(special,quest with {Npc=324}),"同ID不匹配原训练定义不能借用兼容解释");
            That(PvpTrainingExposure.Allows(special with {PvpPrivate=null},quest)==(expose!=1),"转普通后不再取得专用链资格");
        }
        var normalRoster=CharacterPackets.Roster([CharacterRosterEntry.FromCharacter(ordinary)]);
        var privateRoster=CharacterPackets.Roster([CharacterRosterEntry.FromCharacter(special)]);
        int tail=normalRoster.Length-10-48; // 单角色空穿戴行；后接固定10字节名册收尾。
        That(normalRoster.Where((value,index)=>value!=privateRoster[index]).Count()==1 &&
            privateRoster[tail+34]==1 && normalRoster[tail+34]==0,"名册仅已证tail34投影专用身份，不挪用相邻状态位");
        var actor=TownPackets.Actor(ordinary,utcSeconds:0);var privateActor=TownPackets.Actor(special,utcSeconds:0);
        That(actor.Where((value,index)=>value!=privateActor[index]).Count()==1 && privateActor[5+43]==1 && actor[5+43]==0,
            "入城116字节块的43号字节只用0/1，同普通角色其他字段不变");
        EquipmentTransfers(ordinary);
        CreationWire();
        GuardianRecreatePolicy();
        string root=Path.Combine(Path.GetTempPath(),"S5A9-private-character-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"game.db");var repo=new CharacterRepository(path);
            var owners=new AccountOwners();using var owner=owners.Acquire("private-owner",()=>{});
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            NewCharacter New(string name,byte grow=1)=>new(name,location,location){Job=0,GrowType=grow,Level=85,
                PvpPrivateCreation=new(85,30,now)};
            Reject<InvalidOperationException>(()=>repo.Create(owner,New("NoLevel")),"账号没有普通85级角色不能创建专用角色");
            repo.Create(owner,new("Qualified",location,location){Level=85});
            Sql("CREATE TRIGGER private_abort AFTER INSERT ON account_pvp_private_characters BEGIN SELECT RAISE(ABORT,'private rollback'); END");
            Reject<SqliteException>(()=>repo.Create(owner,New("Rollback")),"身份账本失败连同创建角色原子回滚");
            That(repo.List(owner).Length==1 && !repo.NameExists("Rollback"),"创建失败不占名、不留普通伪专用角色");
            Sql("DROP TRIGGER private_abort");
            var created=repo.Create(owner,New("PrivateOne"));
            That(created.IsPvpPrivate && repo.ListRoster(owner).Single(row=>row.Id==created.Id).IsPvpPrivate &&
                new CharacterRepository(path).LoadCharacter(owner,created.Slot).PvpPrivate?.RecreateDays==30,
                "完整角色、轻量名册与重登投影同一持久身份");
            Reject<InvalidOperationException>(()=>repo.Create(owner,New("Duplicate")),"同账号同职业转职不能重复创建");
            var other=repo.Create(owner,New("PrivateTwo",2));
            That(other.IsPvpPrivate,"另一转职有独立创建账本");
            using(var stranger=owners.Acquire("another-owner",()=>{}))
                Reject<InvalidOperationException>(()=>repo.Create(stranger,New("OtherAccount")),"别的账号高级角色不能提供建角资格");
            Sql("CREATE TRIGGER private_delete_abort BEFORE DELETE ON characters BEGIN SELECT RAISE(ABORT,'delete rollback'); END");
            Reject<SqliteException>(()=>repo.Delete(owner,new(created.Id,created.Slot,created.Name,created.Revision)),"晚期删除失败回滚冷却及身份");
            That(repo.LoadCharacter(owner,created.Slot).IsPvpPrivate,"删除失败身份仍在，不能重建套利");
            Sql("DROP TRIGGER private_delete_abort");
            repo.Delete(owner,new(created.Id,created.Slot,created.Name,created.Revision));
            Reject<InvalidOperationException>(()=>new CharacterRepository(path).Create(owner,New("Recreated")),"删角重登与改名均不能绕过原30天冷却");
            var future=New("AfterCool") with {PvpPrivateCreation=new(85,30,now+31L*86400)};
            That(repo.Create(owner,future).IsPvpPrivate,"冷却到期可复用相同转职账本而不清其他分支");
            // 独立空v95库真正缺少表；不通过降版本但留表伪装升级。
            string migration=Path.Combine(root,"migration.db");new CharacterRepository(migration);
            Sql("DROP TABLE character_operation_hope_reward_receipts; DROP TABLE account_pvp_private_characters; ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=95",migration);
            var upgraded=new CharacterRepository(migration);
            using(var upgradedOwner=owners.Acquire("migrated",()=>{}))
                That(!upgraded.Create(upgradedOwner,new("Migrated",location,location)).IsPvpPrivate,"v95升级后普通角色保持普通，未凭等级补造专用身份");
            Sql("DROP TABLE character_operation_hope_reward_receipts; DROP TABLE account_pvp_private_characters; CREATE TABLE account_pvp_private_characters(account TEXT,job INTEGER,grow_type INTEGER,character_id INTEGER,blocked_until INTEGER,recreate_days INTEGER); ALTER TABLE characters DROP COLUMN selected_skill_tree; PRAGMA user_version=95",migration);
            Reject<InvalidDataException>(()=>new CharacterRepository(migration),"同名缺约束表不能蒙混schema96校验");
            void Sql(string sql,string? target=null)
            {using var db=new SqliteConnection("Data Source="+(target??path)+";Pooling=False");db.Open();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
        }
        finally {Directory.Delete(root,true);}
        Console.WriteLine($"PASS {Count-before} 专用决斗身份、分流、创建冷却及迁移断言");
    }
    public static void RunResources(DfoServer.GameWorld.Towns.TownCatalog towns,PvfLib.PvfArchive pvf,
        DfoServer.GameWorld.Characters.CharacterCatalog characters,DfoServer.Infrastructure.ItemResources items,
        DfoServer.GameWorld.Characters.SkillLearningCatalog learning)
    {
        int before=Count;
        var catalog=DfoServer.GameWorld.Characters.PvpPrivateCharacterCatalog.Load(pvf);
        var growth=DfoServer.Testing.SelfTestCatalogs.Growth(pvf,learning);
        var service=new PvpPrivateCharacterService(characters,catalog,learning,growth,items.Items,towns);
        int supported=0;
        That(catalog.AccountLevelRequirement==85 && catalog.Definitions.Count()==58,"原注册表58份专用初装且账号普通角色等级门槛85");
        foreach(var definition in catalog.Definitions)
        {
            bool ready=service.TryPrepare("Private",definition.Job,definition.GrowType,DateTimeOffset.UtcNow,out var plan,out var reason);
            bool broken=definition.Job==15;
            That(ready!=broken,"逐职业核验专用资源，守护者仅补已确认冷却，枪剑士错配仍拒绝："+definition.Job+"/"+definition.GrowType+" "+reason);
            if(!ready) {That(plan is null,"坏定义不返回半份角色规划");continue;}
            supported++;
            if(definition.Job==12 && definition.GrowType is 3 or 4)
                That(definition.RecreatePenalty is null && plan!.PvpPrivateCreation!.RecreateDays==30,
                    "真实守护者资源仍缺字段，30天仅来自已批准领域兼容规则");
            That(plan!.Level==85 && plan.GrowType==definition.GrowType && plan.PvpPrivateCreation!.RecreateDays==PvpPrivateCharacterPlan.RecreateDays(definition) &&
                plan.Inventory.Items.Length==definition.Equipment.Length && plan.Inventory.Items.All(item=>item.Uid==0),
                "创建保留原基础分支/数量/冷却，真实UID只由数据库事务分配");
            foreach(var row in definition.Equipment)
            {
                var item=plan.Inventory.Items.Single(item=>item.Core.ItemId==row.ItemId);
                var template=items.Items.Get(row.ItemId);
                That(template.PvpFreeEquipment && template.AllowsUse(85,definition.Job) &&
                    (row.Flag==1 ? item.Position.List==DfoServer.Game.Inventory.Models.InventoryListType.Equipment && item.Position.Index==template.EquipmentSlot :
                        item.Position.List==DfoServer.Game.Inventory.Models.InventoryListType.Main),"每件原声明装备都验证实际职业、部位和受保护专用标记");
                That(!DfoServer.Game.Inventory.Services.InventoryMoveService.Allows(
                    new Character(1,0,"Ordinary",0,plan.Position,plan.Return){Job=definition.Job,Level=85,EquipmentSlotFlags=31},
                    new(DfoServer.Game.Inventory.Models.InventoryListType.Equipment,template.EquipmentSlot),item.Core,items.Items),
                    "普通角色不能穿戴专用初始装备");
            }
        }
        That(supported==54,"原52份与两份已确认守护者冷却兼容规划通过，四份枪剑士错配仍拒绝");
        VerifyCreationSession(towns,service);
        Console.WriteLine($"PASS {Count-before} 58分支专用建角资源及装备保护断言");
    }

    static void GuardianRecreatePolicy()
    {
        var definition=new DfoServer.GameWorld.Characters.PvpPrivateCharacterDefinition(12,3,"character/Knight/Knight.chr",[],null);
        foreach(byte grow in new byte[]{3,4})
        {
            var guardian=definition with {GrowType=grow};
            That(PvpPrivateCharacterPlan.RecreateDays(guardian)==30 && guardian.RecreatePenalty is null,
                "仅守护者两分支缺字段采用本服30天，资源原始值仍保留缺失");
            foreach(int explicitDays in new[]{0,7,30,36500,-1,36501})
                That(PvpPrivateCharacterPlan.RecreateDays(guardian with {RecreatePenalty=explicitDays})==explicitDays,
                    "明确配置包括非法值不被30天掩盖，非法值仍交原验证拒绝");
        }
        foreach(byte grow in new byte[]{0,1,2,5})
            That(PvpPrivateCharacterPlan.RecreateDays(definition with {GrowType=grow}) is null,"其它守护者分支缺值不继承");
        foreach(byte job in new byte[]{0,11,13,15})
            That(PvpPrivateCharacterPlan.RecreateDays(definition with {Job=job}) is null,"其它职业缺值不继承");
        That(PvpPrivateCharacterPlan.RecreateDays(definition with {Path="character/knight/other.chr"}) is null,
            "未知替代CHR不套用原文件兼容规则");
    }

    internal static void VerifyGuardianRecreation(TownCatalog towns,PvpPrivateCharacterService service)
    {
        string root=Path.Combine(Path.GetTempPath(),"S5A9-guardian-recreate-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"game.db");var repo=new CharacterRepository(path);
            using var owner=new AccountOwners().Acquire("guardian-recreate",()=>{});
            repo.Create(owner,new("Qualifier",towns.Room,towns.InitialReturn){Level=85});
            foreach(byte grow in new byte[]{3,4})
            {
                That(service.TryPrepare("Guardian"+grow,12,grow,DateTimeOffset.UtcNow,out var plan,out _),"两守护者分支使用真实资源完成规划");
                var created=repo.Create(owner,plan!);
                That(new CharacterRepository(path).LoadCharacter(owner,created.Slot).PvpPrivate?.RecreateDays==30,
                    "兼容冷却随真实身份持久化，重开仓储不丢失");
                Reject<InvalidOperationException>(()=>repo.Create(owner,plan! with {Name="Repeat"+grow}),"同分支重放不能重复创建");
                repo.Delete(owner,new(created.Id,created.Slot,created.Name,created.Revision));
                using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();using var command=db.CreateCommand();
                command.CommandText="SELECT blocked_until FROM account_pvp_private_characters WHERE account='guardian-recreate' AND job=12 AND grow_type=$grow";
                command.Parameters.AddWithValue("$grow",grow);long until=(long)command.ExecuteScalar()!;
                var before=plan! with {Name="Before"+grow,PvpPrivateCreation=plan!.PvpPrivateCreation! with {CreatedAtUnix=until-1}};
                Reject<InvalidOperationException>(()=>new CharacterRepository(path).Create(owner,before),"删角重登后冷却截止前一秒仍拒绝");
                var after=before with {Name="After"+grow,PvpPrivateCreation=before.PvpPrivateCreation! with {CreatedAtUnix=until}};
                That(new CharacterRepository(path).Create(owner,after).PvpPrivate?.RecreateDays==30,"精确到期可重新创建且冷却策略仍为30天");
            }
        }
        finally {Directory.Delete(root,true);}
    }

    static void CreationWire()
    {
        That(DfoServer.Network.Parsers.CharacterRequests.PvpPrivateCheck([15,0,0,0,5,0,0,0])==(15,5),"872按完整两个u32读取边界职业及基础分支");
        foreach(var invalid in new byte[][]{[],[0,0,0,0,0,0,0],[0,0,0,0,0,0,0,0,0],[16,0,0,0,0,0,0,0],[0,1,0,0,0,0,0,0],[0,0,0,0,6,0,0,0],[0,0,0,0,17,0,0,0]})
            Reject<InvalidDataException>(()=>DfoServer.Network.Parsers.CharacterRequests.PvpPrivateCheck(invalid),"872拒绝宽度、高字节、越界及虚构觉醒位");
        byte[] body=[0,1,0,0,0,(byte)'A',0,0,0,0,0,0,1,0,1,0,0,0];
        var parsed=DfoServer.Network.Parsers.CharacterRequests.ParseCreate(body);
        That(parsed.IsPvpPrivate && parsed.PvpGrow==1,"5专用尾部保留一致的两份基础grow");
        body[14]=2;
        Reject<InvalidDataException>(()=>DfoServer.Network.Parsers.CharacterRequests.ParseCreate(body),"5专用尾部两份grow冲突拒绝");
        body[12]=255;
        That(!DfoServer.Network.Parsers.CharacterRequests.ParseCreate(body).IsPvpPrivate,"普通FF标志携带展示grow不授予专用身份");
        Bytes(CharacterPackets.PvpPrivateChecked(true),[1,0],"872成功额外零字节仍继续命名，不伪造资格提示");
        Bytes(CharacterPackets.PvpPrivateChecked(false),[0,19,0],"872失败严格公共u16错误正文");
    }

    static void VerifyCreationSession(TownCatalog towns,PvpPrivateCharacterService service)
    {
        string root=Path.Combine(Path.GetTempPath(),"S5A9-private-session-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"game.db");
            new DfoServer.Game.Auth.AuthenticationService(new AuthenticationRepository(path)).Create("private-session","fixture-password");
            var repo=new CharacterRepository(path);var owners=new AccountOwners();
            using var owner=owners.Acquire("private-session",()=>{});
            var ordinary=repo.Create(owner,new("Qualifier",towns.Room,towns.InitialReturn){Level=85});
            var session=new DfoServer.Game.Session.GameSession(repo,towns,owner,entry:new(new(2,40),24,"[pvp]"));
            That(service.TryPrepare("Checked",0,1,DateTimeOffset.UtcNow,out var prepared,out _),"事务夹具使用完整真实专用创建规划");
            That(session.CreatePvpPrivateCharacterForClient(prepared!) is null,"未有872会话许可不创建");
            var check=session.CheckPvpPrivateCreation(prepared);
            That(check.Allowed && session.CanPublishPvpPrivateCreationCheck(check),"同代数未选角资格可发布");
            session.Select(ordinary.Slot);session.ReturnToRoster();
            That(!session.CanPublishPvpPrivateCreationCheck(check) && session.CreatePvpPrivateCharacterForClient(prepared!) is null,
                "普通角色选角再返回也使旧872许可失效");
            check=session.CheckPvpPrivateCreation(prepared);
            Sql("CREATE TRIGGER private_session_abort AFTER INSERT ON account_pvp_private_characters BEGIN SELECT RAISE(ABORT,'private session rollback'); END");
            Reject<SqliteException>(()=>session.CreatePvpPrivateCharacterForClient(prepared!),"会话创建末段失败仍整批回滚");
            That(session.CanPublishPvpPrivateCreationCheck(check) && repo.List(owner).Length==1,"明确失败不消耗原资格、不残留角色或装备");
            Sql("DROP TRIGGER private_session_abort");
            var receipt=session.CreatePvpPrivateCharacterForClient(prepared!)!;
            That(receipt.Created.IsPvpPrivate && session.CanPublishCreation(receipt) &&
                session.CreatePvpPrivateCharacterForClient(prepared!) is null,"原资格可重试成功，但成功后消费且重复不建第二个");
            var adventure=AdventureCoreTests.SyntheticCatalog();var now=DateTimeOffset.UtcNow;
            repo.InitializeAdventure(owner,adventure,now);
            That(repo.ReadAdventure(owner,adventure,now).Members.Select(row=>row.Id).SequenceEqual([ordinary.Id]) &&
                repo.ReadAdventureWeeklyRoster(owner,20261022).Select(row=>row.Id).SequenceEqual(new uint[]{ordinary.Id}),
                "账号共同名册和每周出战名册排除专用角色，保留普通角色");
            That(new CharacterRepository(path).ReadAdventure(owner,adventure,now).Members.All(row=>row.Id!=receipt.Created.Id),
                "仓储重开仍从持久身份过滤，不依赖当前在线选择");
            session.Select(receipt.Created.Slot);session.MarkReady();
            That(session.Selected is { Position.Town:10, Return.Town:10 },"专用选角不能被普通入城重定向到赛丽亚房间");
            session.ReturnToRoster();
            using var replacement=owners.Acquire(owner.Account,()=>{});
            Reject<InvalidOperationException>(()=>session.CheckPvpPrivateCreation(prepared),"旧账号generation不能申请新创建资格");
            void Sql(string sql)
            {using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
        }
        finally {Directory.Delete(root,true);}
    }

    static void EquipmentTransfers(Character character)
    {
        var template=new DfoServer.GameWorld.Items.ItemTemplate(100,DfoServer.GameWorld.Items.ItemKind.Equipment,12,1,100,1,0,0,1,"[free]",ushort.MaxValue,0,"专用") {PvpFreeEquipment=true};
        var item=new DfoServer.Game.Inventory.Models.InventoryItem(1,new(DfoServer.Game.Inventory.Models.InventoryListType.Main,9),
            DfoServer.Game.Inventory.Services.InventoryRewardGrantService.Create(template));
        var carried=character with {Inventory=new([item])};
        That(DfoServer.Game.Mailbox.MailboxService.TransferError(item,template,"a","a",0)!=DfoServer.Game.Mailbox.MailboxError.None &&
            DfoServer.Game.Mailbox.MailboxService.TransferError(item,template,"a","b",0)!=DfoServer.Game.Mailbox.MailboxError.None,
            "专用初始装备即使free也不能同账号或跨账号邮寄");
        That(DfoServer.Game.Auction.AuctionService.TransferBlocked(item.Core,template) is not null &&
            !DfoServer.Game.Inventory.Services.InventoryCargoService.AllowsInsert(item.Core,DfoServer.Game.Inventory.Models.InventoryListType.AccountCargo,template),
            "专用装备不能拍卖或存入可转角色账号仓库");
        That(DfoServer.Game.Inventory.Services.InventoryDiscardPlan.Prepare(carried,new(0,0,0,9,1,0),template,0).Plan is null,
            "专用装备不能经地面丢弃转给他人");
        var ordinary=template with {PvpFreeEquipment=false};
        That(DfoServer.Game.Mailbox.MailboxService.TransferError(item,ordinary,"a","b",0)==DfoServer.Game.Mailbox.MailboxError.None &&
            DfoServer.Game.Auction.AuctionService.TransferBlocked(item.Core,ordinary) is null &&
            DfoServer.Game.Inventory.Services.InventoryCargoService.AllowsInsert(item.Core,DfoServer.Game.Inventory.Models.InventoryListType.AccountCargo,ordinary),
            "普通free装备沿原流转规则，不因专用保护一刀切禁止");
    }

}
