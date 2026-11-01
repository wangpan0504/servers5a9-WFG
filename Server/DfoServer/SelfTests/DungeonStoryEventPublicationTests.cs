using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonStoryEventPublicationTests
{
    public static async Task RunAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,SkillLearningCatalog learning)
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(),"S5A9-story-events-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var parent = QuestEligibilityTests.Quest(200);
            var child = parent with { Id = 201,Grade = "[sub]",MainQuest = 200,Condition = "[hunt enemy]",IntData = [777,-1,303,3,1],
                RewardType = null,Attributes = ["not give exp quest"] };
            var quests = new QuestCatalog(new() { [200] = parent,[201] = child });
            string path = Path.Combine(root,"game.db");
            var repository = new CharacterRepository(path,quests);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("story-event-fixture",() => { });
            var request = SkillRepositoryTests.Fixture("StoryEvents",towns,characters,learning);
            var created = repository.Create(owner,request with { DungeonState = new(10,0,0,0,0),
                ActiveMainQuests = [new(0,200,1),new(1,201,1)] });
            var session = new GameSession(repository,towns,owner,questCatalog: quests);
            session.Select(created.Slot); session.MarkReady();
            That(session.CurrentFormalDungeonScene() is null,"普通城镇不能回复地下城暂停成功");
            var expected = session.Selected!;
            var map = new DungeonMap("boss",new(0,0),900100,"map/synthetic-story.map");
            var maze = new DungeonMaze(0,1,1,"BB",[map.Position],[map.Position],[map],[new(0,0),new(0,200),new(0,-1)])
                { BossEntrance = new(1,0,[new(1,0,1)]) };
            var entry = new DungeonEntryPlan(expected.Id,expected.Revision+1,777,200,0,0,maze,map,map) { QuestAcceptances = [.. expected.ActiveMainQuests] };
            var plan = DungeonMonsterPlan.FromRoom(new(map,[new(1,[1,0,3,0,0,0,1,1],"[fixed]","[boss]","monster/goblin/goblin.mob")],[],[]),expected.Id,10);
            var route = new DungeonFixedRoute([plan],[]); var authority = new NativeDungeonCombatAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry,1,session.SelectionEpoch,route,authority,out var run,out _) && authority.BindTo(run!),
                "只用合成地图、目标及战斗安装正式实例，不进入实际任务路线");
            object Nested(string name,params object?[] args) => typeof(GameSession).GetNestedType(name,BindingFlags.NonPublic)!
                .GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
                .Single(ctor => ctor.GetParameters().Length == args.Length).Invoke(args);
            var dungeon = Nested("FormalDungeon",entry,route,run,authority,1u,new DungeonGroundState(run!,route.Rooms,[],""),
                new Dictionary<int,DungeonExplicitDropPool>(),(Func<int,int>)(_ => 0),null,
                System.Collections.Immutable.ImmutableDictionary<ushort,DungeonDropGenerationPlan>.Empty);
            var prepared = Nested("PreparedFormalDungeon",expected,session.SelectionEpoch,dungeon,11u);
            That(typeof(GameSession).GetMethod("InstallFormalDungeon",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(session,[prepared]) is not null,
                "合成安装沿原attempt/key预留事务，不绕过在线角色状态");
            using var log = new ServerLog(root);
            var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning); var kills = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf);
            var stats = new CharacterStatComputer(characters);
            var commands = new GameCommandRegistry();
            new DungeonHandler(log,global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf),global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf),growth,kills,
                new TownProjection(towns,stats,null,items.EpicPieces),new CharacterLevelProjection(stats)).Register(commands);
            commands.Seal();
            using var output = new MemoryStream(); using var stop = new CancellationTokenSource();
            await using var sender = new GameConnectionSender(owner,output,stop,0,(_,_,_,_,_) => { });
            var context = new GameConnectionContext(new(88,IPAddress.Loopback,output,stop),owner,session,sender);
            async Task Pause()
            {
                long revision = session.Selected!.Revision;
                foreach (byte[] body in new byte[][] { [0,0],[1,0],[0,1],[1,1],[0,2],[1,2],[1,2] })
                {
                    bool restore = body[0] == 1 && session.DungeonSnapshot!.Phase is SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement;
                    output.SetLength(0);
                    That(await commands.DispatchAsync(context,new(1,191,body)) == CommandResult.Handled,"真实命令表接入剧情暂停和恢复");
                    byte[] frame = output.ToArray();
                    That(frame.Length == (restore ? 40 : 20) && frame[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 170 &&
                        BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(16)) == expected.Id && frame[18] == body[0] && frame[19] == body[1],
                        "暂停只发170；已加载场景恢复沿170→3顺序，不重放加载、Actor或结算通知");
                    if (restore)
                        That(frame[20] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(21)) == 3 &&
                            frame.AsSpan(36).SequenceEqual(new byte[] { 1,(byte)expected.Id,(byte)(expected.Id >> 8),1 }),
                            "三个演出域及重复恢复均重新激活当前角色，使用独立编码的4B状态正文");
                }
                That(session.Selected!.Revision == revision && repository.LoadCharacter(owner,created.Slot)!.Revision == revision,
                    "暂停交换不修改任务或数据库revision");
            }
            await Pause();
            That(session.CompleteFormalDungeonLoading() is not null,"合成加载经过正式确认");
            await Pause();
            var selected = session.Selected!;
            var death = new NativeDungeonDeathObservation(10,selected.Id,0x0111,selected.Id,0,0,0,3,0,0,0,true,new string('A',64));
            Sql("CREATE TRIGGER fail_story_object AFTER UPDATE ON character_active_main_quests WHEN NEW.quest_id=201 AND NEW.condition_state=0 BEGIN SELECT RAISE(ABORT,'story rollback'); END");
            Reject<SqliteException>(() => session.ObserveFormalDungeonDeath(death,kills,growth),"动态物件进度末段失败必须回滚");
            That(SkillRepositoryTests.Equal(selected,repository.LoadCharacter(owner,created.Slot)!) && run!.HasPendingDeath &&
                Scalar("SELECT count(*) FROM character_dungeon_growth_receipts") == 0,"失败不保存经验、技能、任务或死亡回执，也不提前确认清图");
            That(!run!.BossEntranceReady && session.PendingDungeonBossEntrance() is null,"SQL失败也不能提前开放Boss入口");
            Sql("DROP TRIGGER fail_story_object");
            byte[] body = new byte[88];
            BinaryPrimitives.WriteUInt32LittleEndian(body,10);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),expected.Id);
            body[30] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(31),0x0111);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(33),expected.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(56),3);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(84),ClientBodyChecksum.Compute(body.AsSpan(0,84)));
            output.SetLength(0);
            That(await commands.DispatchAsync(context,new(1,39,body)) == CommandResult.Handled,"原死亡通过真实Handler恢复提交和投影");
            That(run.ClearFact is not null && session.Selected!.ActiveMainQuests.Single(row => row.QuestId == 201).ConditionState == 0 &&
                session.Selected.Revision == selected.Revision+1 && Scalar("SELECT count(*) FROM character_dungeon_growth_receipts") == 1,
                "原死亡恢复后清房、动态物件、经验和revision整笔提交");
            var frames = new List<(ushort Opcode,byte[] Body)>();
            byte[] written = output.ToArray();
            for (int at = 0; at < written.Length;)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(written.AsSpan(at+3));
                frames.Add((BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(at+1)),written.AsSpan(at+16,length-16).ToArray()));
                at += length;
            }
            That(frames[0].Opcode == 38 && frames[1].Opcode == 312 && frames[1].Body.SequenceEqual(new byte[5]) &&
                frames.Count(frame => frame.Opcode == 312) == 1 && run.BossEntranceOpen && session.PendingDungeonBossEntrance() is null,
                "真实死亡通知之后仅发送一次入口开放通知，发送完成后才启用条件传送");
            var committed = repository.LoadCharacter(owner,created.Slot)!;
            That(session.ObserveFormalDungeonDeath(death,kills,growth).Outcome == DungeonRunOutcome.Duplicate &&
                SkillRepositoryTests.Equal(committed,repository.LoadCharacter(owner,created.Slot)!),"重复死亡不能重复推进动态物件或发经验");
            output.SetLength(0);
            That(await commands.DispatchAsync(context,new(1,39,body)) == CommandResult.Handled && output.Length == 0,
                "重复死亡不能重发入口开放通知或结算");
            await Pause();
            var receipt = session.CurrentFormalDungeonScene()!;
            session.ReturnToRoster(); session.Select(created.Slot); session.MarkReady();
            output.SetLength(0);
            That(!session.CanPublishFormalDungeon(receipt) && await commands.DispatchAsync(context,new(1,191,[1,1])) == CommandResult.Unhandled &&
                output.Length == 0 && session.Selected!.ActiveMainQuests.Single(row => row.QuestId == 201).ConditionState == 0,
                "重选使原暂停回执失效，重登保留已提交物件进度");
            await TempleAsync(towns,pvf,characters,items,learning,commands);

            void Sql(string sql) { using var db = new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long Scalar(string sql) { using var db = new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        }
        finally { TestFiles.DeleteDirectory(root,true); }
        Console.WriteLine($"PASS {Count-before} 剧情事件真实Handler、Sender、SQLite回滚、重放及重选断言（合成场景）");
    }

    static async Task TempleAsync(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        SkillLearningCatalog learning,GameCommandRegistry commands)
    {
        var quests = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning);
        var completed = DfoServer.Development.CharacterPreparation.Load(pvf).MapQuests.Append(3203).Where(id => id != 3204).Distinct().ToArray();
        using var f = new QuestSceneTests.Fixture(towns,characters,learning,growth,quests,false,27,
            completed);
        That(f.Session.ChangeQuest(3204,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
            "隔离Lv27角色合法接取真实3204，不使用现场玩家库");
        f.TravelTo(towns,new(40,4,800,285));
        var entry = f.Session.EnterFormalDungeon(24,3204,0,global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf),global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf))!;
        That(entry is not null && entry.Snapshot.CurrentMap.Id == 53480,"真实3204使用53480基础房间入场");
        f.Session.CompleteFormalDungeonLoading();
        var baseKeys = f.Session.DungeonSnapshot!.LivingMonsters.ToArray();
        long revision = f.Read().Revision;
        using var output = new MemoryStream(); using var stop = new CancellationTokenSource();
        await using var sender = new GameConnectionSender(f.Owner,output,stop,0,(_,_,_,_,_) => { });
        var context = new GameConnectionContext(new(89,IPAddress.Loopback,output,stop),f.Owner,f.Session,sender);
        byte[] move = new byte[100]; move[0] = 2; move[1] = 2; move[10] = 1;
        That(await commands.DispatchAsync(context,new(1,45,move)) == CommandResult.Handled &&
            f.Session.DungeonSnapshot!.CurrentMap.Id == 53501,"复现首个kind1进入原演出层53501");
        output.SetLength(0);
        await commands.DispatchAsync(context,new(1,37,new byte[8]));
        output.SetLength(0);
        That(await commands.DispatchAsync(context,new(1,45,move)) == CommandResult.Handled,"第二个相同kind1结束演出层，不再静默拒绝");
        byte[] written = output.ToArray();
        byte[] expected = new byte[16]; expected[0] = 2; expected[1] = 2; expected[2] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(3),entry!.MapSeed);
        expected[9] = 1; expected[15] = 255;
        That(written.Length == 49 && written[0] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(1)) == 45 && written[16] == 1 &&
            written[17] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(written.AsSpan(18)) == 29 && written.AsSpan(33).SequenceEqual(expected),
            "恢复沿ACK45→NOTI29精确16B缓存正文，不重新下发基础演员或地图编号");
        That(f.Session.DungeonSnapshot!.CurrentMap.Id == 53480 && f.Session.DungeonSnapshot.LivingMonsters.SequenceEqual(baseKeys) &&
            f.Read().Revision == revision && f.Read().ActiveMainQuests.Single(row => row.QuestId == 3204).ConditionState == 1,
            "恢复原房间key和任务接取，演出换层不创造死亡、奖励或清图凭据");
        var restored = f.Session.DungeonSnapshot;
        output.SetLength(0);
        That(await commands.DispatchAsync(context,new(1,45,move)) == CommandResult.Handled && output.Length == 0 &&
            f.Session.DungeonSnapshot!.Identity == restored!.Identity && f.Session.DungeonSnapshot.Phase == restored.Phase,
            "加载前的最终自动确认仅消费一次，不重发地图或重置加载");
        await commands.DispatchAsync(context,new(1,37,new byte[8]));
        output.SetLength(0);
        That(await commands.DispatchAsync(context,new(1,45,move)) == CommandResult.Unhandled && output.Length == 0 &&
            f.Session.DungeonSnapshot!.Phase == SinglePlayerDungeonPhase.Dungeon,"重复最终确认不能再次进入演出层");
    }
}
