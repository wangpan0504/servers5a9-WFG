using System.Reflection;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestCycleContinuationTests
{
    public static void RunCore()
    {
        int checks = Count;
        var date = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).AddDays(1);
        var before = new DateTimeOffset(date.Year,date.Month,date.Day,5,59,59,TimeSpan.FromHours(8));
        var after = before.AddSeconds(1);
        That(QuestCycle.Day(before) != QuestCycle.Day(after),"合成时钟准确跨过UTC+8 06:00游戏日边界");
        using (var f = new Fixture(DateTimeOffset.UtcNow))
        {
            var selected = f.Read();
            var sameDay = selected with { Revision = selected.Revision+1 };
            int day = selected.QuestCycleDay;
            That(QuestMainlineTests.MatchesReconnect(selected,sameDay,day,day,f.Towns.Room) &&
                !QuestMainlineTests.MatchesReconnect(selected,sameDay with { Revision = selected.Revision },day,day,f.Towns.Room),
                "主线同日比较仍要求一次回房保存和完整状态，不允许零次保存");
            f.Session.ReturnToRoster();
            int previousDay = QuestCycle.Day(DateTimeOffset.UtcNow.AddDays(-1));
            // 只准备本轮合成数据库的旧日投影；选角、日切及回房全部沿真实Session和SQLite。
            f.Sql($"""
                INSERT INTO accounts(account_id,salt,password_hash,iterations,disabled)
                  SELECT account,zeroblob(16),zeroblob(32),100000,1 FROM characters;
                UPDATE characters SET quest_cycle_day={previousDay},x=x+1;
                UPDATE character_completed_quests SET daily_period={previousDay},answer=2 WHERE quest_id=100;
                INSERT INTO character_completed_quests(character_id,quest_id,answer) SELECT id,102,3 FROM characters;
                INSERT INTO account_completed_quests(account,quest_id,character_id,acceptance_id,daily_period,answer)
                  SELECT account,100,id,'11111111111111111111111111111111',{previousDay},2 FROM characters;
                INSERT INTO account_completed_quests(account,quest_id,character_id,acceptance_id,answer)
                  SELECT account,102,id,'22222222222222222222222222222222',3 FROM characters;
                """);
            var completed = f.Read();
            int firstDay = QuestCycle.Day(DateTimeOffset.UtcNow);
            f.Session.Select(0); f.Session.MarkReady();
            var restored = f.Read();
            int lastDay = QuestCycle.Day(DateTimeOffset.UtcNow);
            bool Matches(Character value) => QuestMainlineTests.MatchesReconnect(completed,value,firstDay,lastDay,f.Towns.Room);
            That(Matches(restored) && Same(restored,f.Session.Selected!) && restored.Revision == completed.Revision+2 &&
                restored.CompletedQuests.SequenceEqual([102]) && restored.AccountCompletedQuests.SequenceEqual([102]) &&
                restored.QuestAnswers.GetValueOrDefault(102) == 3 && restored.AccountQuestAnswers.GetValueOrDefault(102) == 3 &&
                restored.QuestDailyClaims.Count == 0 && restored.AccountQuestDailyClaims.Count == 0,
                "真实跨日选角精确保存两次，只清过期个人及账号每日投影，永久完成与答案保持");
            That(!Matches(restored with { Revision = restored.Revision-1 }) &&
                !Matches(restored with { Revision = restored.Revision+1 }) &&
                !Matches(restored with { QuestCycleDay = previousDay }) &&
                !Matches(restored with { QuestCycleDay = QuestCycle.Day(DateTimeOffset.UtcNow.AddDays(2)) }),
                "跨日比较不接受错误revision、旧周期或窗口之外的周期");
            That(!Matches(restored with { Level = 2 }) && !Matches(restored with { CompletedQuests = [] }) &&
                !Matches(restored with { QuestAnswers = completed.QuestAnswers }) &&
                !Matches(restored with { AccountQuestAnswers = completed.AccountQuestAnswers }) &&
                !Matches(restored with { ActiveMainQuests = [] }),
                "跨日比较仍拒绝资产、永久完成、过期答案和活动接取变化");
        }
        using (var f = new Fixture(before))
        {
            var selected = f.Session.Selected!;
            f.PrepareContinuation();
            That(f.Session.HasFormalDungeonContinuation(),"夹具处于旧副本已退场、后继等待真实15的边界");
            That(f.Session.AdvanceQuestCycle(before) is null && f.Session.AdvanceQuestCycle(after) is null &&
                ReferenceEquals(selected,f.Session.Selected) && Same(selected,f.Read()),
                "06:00跨日不替换待连图原角色、不清每日完成或增加revision");
            var opening = f.Session.CompleteFormalDungeonContinuation();
            That(opening is { Pending:true,Selection:not null,Opened:true } && f.Session.Phase == SessionPhase.MapSelect &&
                f.Session.DungeonSnapshot is null && Same(selected,f.Read()) && f.Session.AdvanceQuestCycle(after) is null,
                "连图15只打开选择阶段，无持久预留且继续保护跨日");
            That(f.Session.CompleteFormalDungeonContinuation(2).Selection is null &&
                f.Session.EnterFormalDungeonContinuation(2,101,0) is null &&
                f.Session.EnterFormalDungeonContinuation(1,102,0) is null &&
                f.Session.EnterFormalDungeonContinuation(1,101,1) is null && Same(selected,f.Read()),
                "错误15目标和16副本、qid、难度不能使用后继规划或产生持久预留");
            var retrySelection = f.Session.CompleteFormalDungeonContinuation(1);
            That(retrySelection is { Selection:not null,Opened:false } &&
                retrySelection.Selection.SelectionId == opening.Selection!.SelectionId,
                "15(0)后15(dungeon)复用同一选择身份");
            var entered = f.Session.EnterFormalDungeonContinuation(1,101,0);
            That(entered is not null && f.Session.Phase == SessionPhase.Loading,"真实16经Session安装已规划的唯一后继");
            var reserved = f.Session.Selected!;
            That(reserved.Revision == selected.Revision+1 && reserved.QuestCycleDay == selected.QuestCycleDay &&
                reserved.DungeonState!.LastAttempt == selected.DungeonState!.LastAttempt+1 && Same(reserved,f.Read()),
                "安装仅提交一次新副本身份预留，未把跨日混入入场准备");
            var replay = f.Session.CompleteFormalDungeonContinuation();
            That(replay.Pending && replay.Selection is null && ReferenceEquals(replay.Replay,entered) &&
                f.Session.EnterFormalDungeonContinuation(1,101,0) is null &&
                f.Session.AdvanceQuestCycle(after) is null && ReferenceEquals(reserved,f.Session.Selected) && Same(reserved,f.Read()),
                "重复15重放原初始化，等待真实加载完成前仍延后切日");
            var loaded = f.Session.CompleteFormalDungeonLoading();
            That(loaded is not null && f.Session.Phase == SessionPhase.Dungeon && !f.Session.HasFormalDungeonContinuation() &&
                !loaded.Entry.IsOrdinary && loaded.Character.Revision == reserved.Revision &&
                loaded.Character.Fatigue == reserved.Fatigue && f.Session.CanPublishFormalDungeon(loaded),
                "真实任务路线加载完成不扣普通疲劳，清除连图pending并发布当前副本");
            var identity = loaded!.Snapshot.Identity;
            var rolled = f.Session.AdvanceQuestCycle(after);
            That(rolled is not null && rolled.Character.Revision == reserved.Revision+1 &&
                rolled.Character.QuestCycleDay == QuestCycle.Day(after) && rolled.Character.CompletedQuests.Length == 0 &&
                rolled.Character.QuestDailyClaims.Count == 0 && rolled.Character.ActiveMainQuests.SequenceEqual(reserved.ActiveMainQuests) &&
                rolled.Character.DungeonState == reserved.DungeonState && f.Session.DungeonSnapshot!.Identity == identity &&
                f.Session.Phase == SessionPhase.Dungeon && Same(rolled.Character,f.Read()),
                "新图加载后推进一次周期，清旧日完成且保留活动任务接取身份及新run身份");
            That(f.Session.AdvanceQuestCycle(after) is null && Same(rolled!.Character,f.Read()),"同日重复请求不再次写入周期");
        }
        using (var f = new Fixture(before))
        {
            var selected = f.Session.Selected!;
            That(f.Session.AdvanceQuestCycle(before) is null && ReferenceEquals(selected,f.Session.Selected),
                "无pending时06:00前仍属于原游戏日");
            var rolled = f.Session.AdvanceQuestCycle(after);
            That(rolled is not null && rolled.Character.Revision == selected.Revision+1 &&
                rolled.Character.QuestCycleDay == QuestCycle.Day(after) && rolled.Character.CompletedQuests.Length == 0 &&
                rolled.Character.ActiveMainQuests.SequenceEqual(selected.ActiveMainQuests) && Same(rolled.Character,f.Read()) &&
                f.Session.Phase == SessionPhase.Town && f.Session.AdvanceQuestCycle(after) is null,
                "正常无连图pending切日保持原行为，不引入城镇或副本场景新限制");
        }
        using (var f = new Fixture(before))
        {
            var selected = f.Session.Selected!;
            f.PrepareContinuation();
            f.Session.CompleteFormalDungeonContinuation();
            var entered = f.Session.EnterFormalDungeonContinuation(1,101,0);
            That(entered is not null && f.Session.Phase == SessionPhase.Loading &&
                f.Session.ReturnFromFormalDungeon() is not null && f.Session.Phase == SessionPhase.Town &&
                !f.Session.HasFormalDungeonContinuation() && f.Session.CompleteFormalDungeonLoading() is null,
                "连图实际加载前允许正常回城，清除原pending且迟到加载不复活旧副本");
            var rolled = f.Session.AdvanceQuestCycle(after);
            That(rolled is not null && rolled.Character.Revision == selected.Revision+2 &&
                rolled.Character.CompletedQuests.Length == 0 &&
                rolled.Character.ActiveMainQuests.SequenceEqual(selected.ActiveMainQuests) && Same(rolled.Character,f.Read()),
                "加载取消后不再阻挡正常06:00切日，保留原接取且不重复预留副本");
            That(f.Session.HasDungeonContinuationRequest() && f.Session.EnterFormalDungeonContinuation(1,101,0) is null &&
                f.Session.CompleteFormalDungeonLoading() is null && Same(rolled!.Character,f.Read()),
                "取消加载保留有界终态，迟到16不能重新安装后继");
        }
        using (var f = new Fixture(before))
        {
            f.PrepareContinuation(); f.Session.CompleteFormalDungeonContinuation();
            var returned = f.Session.ReturnFromDungeonSelection();
            That(returned is not null && f.Session.Phase == SessionPhase.Town && !f.Session.HasFormalDungeonContinuation() &&
                f.Session.HasDungeonContinuationRequest() && f.Session.EnterFormalDungeonContinuation(1,101,0) is null,
                "MapSelect取消保留终态拦迟到16且旧72尾包不再归活动连图");
            var teleport = typeof(GameSession).GetMethod("CanTeleport",BindingFlags.Instance|BindingFlags.NonPublic)!;
            That((bool)teleport.Invoke(f.Session,null)! && f.Session.AdvanceQuestCycle(after) is not null,
                "取消后恢复城镇传送准入和每日周期，不让终态冻结城镇业务");
            var newSelection = f.Session.BeginClassExperienceSelection();
            That(newSelection is not null && f.Session.ConfirmNewDungeonSelection(newSelection) &&
                !f.Session.HasDungeonContinuationRequest(),"取消后合法新15选择可消费终态并恢复独立入口");
            f.Session.ReturnToRoster();
            That(!f.Session.HasDungeonContinuationRequest() && f.Session.EnterFormalDungeonContinuation(1,101,0) is null,
                "返回选角清除原连图终态，旧规划不得在新epoch安装");
        }
        using (var f = new Fixture(before))
        {
            f.PrepareContinuation(); f.Session.CompleteFormalDungeonContinuation();
            var character = f.Session.Selected!;
            // 同槽位同qid重新接取仍替换原快照，不能复用旧规划。
            var changed = character with { ActiveMainQuests = [character.ActiveMainQuests[0] with { AcceptanceId = Guid.NewGuid() }] };
            typeof(GameSession).GetProperty(nameof(GameSession.Selected))!.SetValue(f.Session,changed);
            var blocked = f.Session.CompleteFormalDungeonContinuation();
            That(blocked is { Pending:true,Cancelled:true,Selection:null } &&
                f.Session.EnterFormalDungeonContinuation(1,101,0) is null && Same(character,f.Read()),
                "同revision换对象仍是原快照失效，15明确终止且不能安装后继或预留attempt");
        }
        using (var f = new Fixture(before))
        {
            var beforePotion = f.Session.Selected!;
            f.PrepareContinuation();
            var returned = f.CommitConsumableReturn(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            That(returned.Revision > beforePotion.Revision+1 && returned.Inventory.ConsumableStates.Length == 0 && Same(returned,f.Read()),
                "副本内商城增益药与退场边界各提交一次库存保存，城镇边界后持久效果为空");
            var opening = f.Session.CompleteFormalDungeonContinuation();
            That(opening is { Pending:true,Cancelled:false,Selection:not null,Opened:true } &&
                f.Session.Phase == SessionPhase.MapSelect,
                "退场保存产生新revision快照后仍打开后继选图，不被误判为失效");
            var entered = f.Session.EnterFormalDungeonContinuation(1,101,0);
            That(entered is not null && entered.Character.Revision == returned.Revision+1 &&
                entered.Entry.CharacterRevision == entered.Character.Revision && Same(entered.Character,f.Read()),
                "后继16按最新revision只预留一次尝试，规划与持久状态一致");
        }
        using (var f = new Fixture(DateTimeOffset.UtcNow))
        {
            var character = f.Read();
            foreach (var binding in new[] { (Guid.NewGuid(),(byte)0),(character.ActiveMainQuests[0].AcceptanceId,(byte)1) })
            {
                f.PrepareContinuation(acceptanceId:binding.Item1,slot:binding.Item2); f.Session.CompleteFormalDungeonContinuation();
                That(f.Session.EnterFormalDungeonContinuation(1,101,0) is null && Same(character,f.Read()),
                    "快照仍相同时复验规划中原AcceptanceId与槽位，错身份不预留attempt");
                f.Session.ReturnFromDungeonSelection();
            }
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(!f.Session.HasDungeonContinuationRequest() && f.Session.EnterFormalDungeonContinuation(1,101,0) is null,
                "同角色重新选角不恢复旧epoch的后继规划");
        }
        using (var f = new Fixture(DateTimeOffset.UtcNow))
        {
            f.Session.ReturnToRoster(); f.Sql("UPDATE characters SET accepted_story_level=0");
            f.Session.Select(0); f.Session.MarkReady();
            f.PrepareContinuation(); var opening = f.Session.CompleteFormalDungeonContinuation();
            var beforeDigest = f.Read(); var updated = f.Session.AcceptStoryDigest();
            var entered = f.Session.EnterFormalDungeonContinuation(1,101,0);
            That(opening.Selection is not null && updated is not null && updated.AcceptedStoryLevel == updated.Level &&
                updated.Revision == beforeDigest.Revision+1 && entered is not null &&
                entered.Character.Revision == updated.Revision+1 && entered.Entry.CharacterRevision == entered.Character.Revision &&
                f.Session.CurrentDungeonSelection is null && Same(entered.Character,f.Read()),
                "MapSelect中1446真实更新同步原规划快照，随后16按新revision仅安装一次");
        }
        using (var f = new Fixture(before))
        {
            f.PrepareContinuation(); f.Session.CompleteFormalDungeonContinuation(); f.Owner.Dispose();
            Testing.TestCheck.Reject<InvalidOperationException>(() => f.Session.EnterFormalDungeonContinuation(1,101,0),
                "失效owner不得提交后继身份预留");
        }
        using (var f = new Fixture(before,forReplay:true))
        {
            var selected = f.Session.Selected!;
            f.PrepareContinuation(forReplay:true);
            That(f.Session.AdvanceQuestCycle(after) is null && ReferenceEquals(selected,f.Session.Selected),
                "已准备回放等待真实15时跨06:00不替换原角色");
            var opening = f.Session.BeginQuestReplayDungeonSelection(1);
            That(opening is { Pending:true,Receipt:not null } && f.Session.Phase == SessionPhase.MapSelect &&
                f.Session.AdvanceQuestCycle(after) is null && f.Session.CurrentDungeonSelection!.ReturnPosition == selected.Position,
                "回放真实15使用当前回城锚点且继续保护周期，无需普通地下城门");
            That(f.Session.EnterQuestReplay(2,101,0) is null && f.Session.EnterQuestReplay(1,102,0) is null &&
                f.Session.EnterQuestReplay(1,101,1) is null && Same(selected,f.Read()),
                "回放真实16复验原副本、qid和难度，错入场不预留或改任务");
            var entered = f.Session.EnterQuestReplay(1,101,0);
            That(entered is { Entry.IsQuestReplay:true,Entry.QuestId:0 } && f.Session.Phase == SessionPhase.Loading &&
                f.Session.Selected!.Revision == selected.Revision+1 && f.Session.Selected.CompletedQuests.SequenceEqual(selected.CompletedQuests) &&
                f.Session.Selected.ActiveMainQuests.SequenceEqual(selected.ActiveMainQuests),
                "回放16仅预留正式副本身份，不重接或重发原任务奖励");
            var retry = f.Session.BeginQuestReplayDungeonSelection(1);
            That(retry is { Pending:true,Receipt:null } && ReferenceEquals(retry.Replay,entered) &&
                f.Session.AdvanceQuestCycle(after) is null && f.Session.Selected!.Revision == selected.Revision+1,
                "回放等待37时重复15沿原初始化且跨周期不重复预留");
            var loaded = f.Session.CompleteFormalDungeonLoading();
            var rolled = f.Session.AdvanceQuestCycle(after);
            That(loaded is not null && rolled is not null && rolled.Character.CompletedQuests.SequenceEqual([101]) &&
                rolled.Character.ActiveMainQuests.SequenceEqual(selected.ActiveMainQuests) && f.Session.Phase == SessionPhase.Dungeon &&
                f.Session.BeginQuestReplayDungeonSelection(1).Pending == false && f.Session.AdvanceQuestCycle(after) is null,
                "回放实际加载后解除周期等待，正常切日只清每日、不改回放完成及其他接取");
        }
        Console.WriteLine($"PASS {Count-checks} 合成Session跨日连图、实际加载与周期持久化断言（非主线）");
    }

    internal sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(),"S5A9-cycle-continuation-"+Guid.NewGuid().ToString("N"));
        readonly CharacterRepository repository;
        readonly AccountOwners.Lease owner;
        public GameSession Session { get; }
        public AccountOwners.Lease Owner => owner;
        public TownCatalog Towns { get; }

        public Fixture(DateTimeOffset before,bool forReplay = false)
        {
            var daily = QuestEligibilityTests.Quest(100) with { Grade="[daily]" };
            var quests = new QuestCatalog(new() { [100]=daily,[101]=QuestEligibilityTests.Quest(101),[102]=QuestEligibilityTests.Quest(102) });
            repository = new(Path.Combine(root,"game.db"),quests);
            owner = new AccountOwners().Acquire("cycle-continuation",() => { });
            var point = new Location(1,0,100,100);
            var map = new TownMap("synthetic-cycle.map",[new(new(0,0,1000,1000),[],[])],[],false,[]);
            var area = new TownArea(point.Town,point.Area,1,[],[],[],point,map,[],[]);
            var towns = new TownCatalog(new() { [(point.Town,point.Area)]=area },point,point,new(0,[],[]),[],new Dictionary<uint,uint[]>());
            Towns = towns;
            repository.Create(owner,new("Cycle",point,point)
                { CompletedQuests=forReplay ? [100,101] : [100],ActiveMainQuests=[new(0,forReplay ? (ushort)102 : (ushort)101,1)],DungeonState=CharacterDungeonState.NewOrdinary });
            using (var db = new SqliteConnection("Data Source="+Path.Combine(root,"game.db")+";Pooling=False"))
            {
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = "UPDATE character_completed_quests SET daily_period=$day WHERE quest_id=100";
                command.Parameters.AddWithValue("$day",QuestCycle.Day(before)); command.ExecuteNonQuery();
            }
            Session = new(repository,towns,owner,questCatalog:quests);
            Session.Select(0); Session.MarkReady(); Session.AdvanceQuestCycle(before);
        }

        public Character Read() => repository.LoadCharacter(owner,0);

        public void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source="+Path.Combine(root,"game.db")+";Pooling=False");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }

        public void PrepareContinuation(bool forReplay = false,Guid? acceptanceId = null,byte? slot = null)
        {
            var character = Session.Selected!; var state = character.DungeonState!;
            var map = new DungeonMap("boss",new(0,0),1,"map/synthetic-cycle.map");
            var maze = new DungeonMaze(0,1,1,"synthetic",[map.Position],[map.Position],[map],[]);
            var entry = new DungeonEntryPlan(character.Id,character.Revision+1,1,forReplay ? 0u : 101u,forReplay ? byte.MaxValue : slot ?? (byte)0,0,maze,map,map)
                { IsQuestReplay = forReplay,RouteQuestId = 101u,QuestAcceptanceId = forReplay ? Guid.Empty : acceptanceId ?? character.ActiveMainQuests[0].AcceptanceId };
            var source = new DungeonRoomMonster(1,[1,0,1,100,100,0,1,1],"[fixed]","[boss]","monster/synthetic-cycle.mob");
            uint key = state.NextObjectKey == character.Id ? state.NextObjectKey+1 : state.NextObjectKey;
            var plan = DungeonMonsterPlan.FromRoom(new(map,[source],[],[]),character.Id,checked((ushort)key));
            var route = new DungeonFixedRoute([plan],[]); var authority = new NativeDungeonCombatAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry,state.LastAttempt+1,Session.SelectionEpoch,route,authority,out var run,out _) &&
                authority.BindTo(run!),"单房合成资源建立独立正式run，不读取或推进真实主线");
            var ground = new DungeonGroundState(run!,route.Rooms,[]);
            // 仅种下已退场的内部准备边界；后续安装、重放、加载和周期提交全部调用实际Session。
            var dungeon = Create("FormalDungeon",entry,route,run,authority,1u,ground,
                new Dictionary<int,DungeonExplicitDropPool>(),(Func<int,int>)(_ => 0),null,
                ImmutableDictionary<ushort,DungeonDropGenerationPlan>.Empty);
            var prepared = Create("PreparedFormalDungeon",character,Session.SelectionEpoch,dungeon,key+1);
            if (forReplay)
            {
                typeof(GameSession).GetField("pendingQuestReplay",BindingFlags.Instance|BindingFlags.NonPublic)!
                    .SetValue(Session,Create("PendingQuestReplay",prepared,101u,SessionPhase.Town,false,null));
                return;
            }
            var previous = new FormalDungeonReceipt(character,owner.Generation,Session.SelectionEpoch,entry,
                run!.Snapshot with { Phase=SinglePlayerDungeonPhase.Settlement },plan,1);
            var returning = new TownTravelReceipt(character,owner.Generation,Session.SelectionEpoch);
            typeof(GameSession).GetField("pendingDungeonContinuation",BindingFlags.Instance|BindingFlags.NonPublic)!
                .SetValue(Session,Create("PendingDungeonContinuation",previous,prepared,returning,null));
        }

        // 复现 #665：主线副本内使用 Maintenance=0 的商城增益药后，退场 CrossBoundary(Town) 丢弃效果行，
        // ReturnFromFormalDungeon 因 inventory 变化保存并替换角色快照；旧实现的连图回执仍指向旧 revision。
        public Character CommitConsumableReturn(long now)
        {
            var character = Session.Selected!;
            var definition = new DfoServer.GameWorld.Items.RecoveryConsumableDefinition(490703930,0,300000,61)
                { EffectMilliseconds = 300000 };
            var charged = ConsumableStatePolicy.Activate(character.Inventory,definition,now);
            That(charged is not null && charged.ConsumableStates.Length == 1,"合成商城增益药真实写入一条Maintenance=0效果");
            var used = repository.Save(owner,character,character.Position,character.Return,
                new InventoryPlan(character.Inventory,charged!,"consumable_use",new(character)));
            typeof(GameSession).GetProperty(nameof(GameSession.Selected))!.SetValue(Session,used);
            var cleared = ConsumableStatePolicy.CrossBoundary(used.Inventory,ConsumableMaintenance.Town,now+1);
            That(cleared.ConsumableStates.Length == 0 && cleared != used.Inventory,"城镇边界丢弃非Town维持的效果，退场必然再保存一次");
            var returned = repository.Save(owner,used,used.Position,used.Return,
                new InventoryPlan(used.Inventory,cleared,"dungeon_retreat_stamina",new(used)));
            typeof(GameSession).GetProperty(nameof(GameSession.Selected))!.SetValue(Session,returned);
            // 这里直接构造旧实现留下的状态：只有 Return 换成退场提交回执，Previous/Dungeon 仍指向用药前快照，
            // 以独立验证 CompleteFormalDungeonContinuation 自身也能按提交历史重绑，而不只依赖退场入口。
            var field = typeof(GameSession).GetField("pendingDungeonContinuation",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var pending = field.GetValue(Session)!;
            object Part(string name) => pending.GetType().GetProperty(name)!.GetValue(pending)!;
            field.SetValue(Session,Create("PendingDungeonContinuation",Part("Previous"),Part("Dungeon"),
                new TownTravelReceipt(returned,owner.Generation,Session.SelectionEpoch),Part("Entered")));
            return returned;
        }

        static object Create(string name,params object?[] args) => typeof(GameSession).GetNestedType(name,BindingFlags.NonPublic)!
            .GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
            .Single(constructor => constructor.GetParameters().Length == args.Length).Invoke(args);

        public void Dispose() { Session.End(); owner.Dispose(); Directory.Delete(root,true); }
    }
}
