using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 同一次正式命令分发覆盖最终死亡、真实忙锁恢复和原祭坛结算。
internal static class BattlePassAltarFinalBusyTests
{
    public static void Run(TcpTestResources resources) => RunAsync(resources).GetAwaiter().GetResult();

    static async Task RunAsync(TcpTestResources resources)
    {
        var archive=resources.Archive;
        var dungeons=SelfTestCatalogs.Dungeons(archive);
        var rooms=SelfTestCatalogs.Rooms(archive);
        var growth=SelfTestCatalogs.Growth(archive);
        var kills=SelfTestCatalogs.KillExperience(archive);
        var items=resources.ItemResources;
        var rewards=new DungeonAltarRewardPolicy(DfoServer.GameWorld.Dungeons.DungeonAltarRewardCatalog.Load(archive),
            DfoServer.GameWorld.Dungeons.DungeonAltarClearItemCatalog.Load(archive),items.DungeonAltarItems,
            kills,items.Items,items.GoldLimits,items.EpicPieces);
        var policy=new BattlePassMissionPolicy("altar-final-busy-fixture",
            new DateTimeOffset(2026,10,22,6,0,0,TimeSpan.FromHours(8)),BattlePassCatalog.Load(archive));
        var area=resources.Towns.Areas.Values.First(row=>row.DungeonWorldMap is {} world &&
            resources.Towns.DungeonMaps[world].Options.Any(option=>option.DungeonId==11006));
        var done=area.Quests.Concat(area.AnyQuests.Take(1)).Concat(area.Default.Bounds.SelectMany(row=>row.Quests))
            .Concat(area.Default.DungeonGates.SelectMany(row=>row.Quests)).Distinct().ToArray();
        var bounds=area.Map(done.ToHashSet()).Bounds.First(row=>row.Conditions.Length==0 && row.Quests.All(done.Contains)).Bounds;
        var position=new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        var at=Enumerable.Range(0,policy.Days).Select(day=>policy.Start.AddDays(day).AddHours(1))
            .First(value=>policy.Active(value).Any(row=>row.Key.Category==0 && row.Key.Id==1));
        var clock=new Clock(at);
        using var fixture=new QuestSceneTests.Fixture(resources.Towns,resources.Characters,resources.Learning,growth,
            resources.Quests,false,70,done,position:position,timeProvider:clock);
        fixture.Repository.ConfigureBattlePassMissions(policy);
        That(policy.Active(clock.GetUtcNow()).Any(row=>row.Key.Category==0 && row.Key.Id==1),"最终祭坛使用活动中的原击杀任务");
        var session=fixture.Session;
        That(session.MutateInventory(c=>CurrencyService.SetGold(c,500000)).Accepted && session.BeginDungeonSelection() is not null,
            "最终祭坛使用原角色和准入");
        var entry=session.EnterFormalDungeon(11007,0,0,dungeons,rooms,items:null,draw:_=>0,pieces:items.EpicPieces)
            ?? throw new InvalidOperationException("最终祭坛真实准入失败："+session.LastFormalDungeonRejection);
        var loaded=session.CompleteFormalDungeonLoading()!;
        That(session.PrepareDungeonAltarUi(loaded),"最终祭坛沿原UI启动");
        var current=session.StartDungeonAltar(loaded)!;
        bool dispatched=false;
        while (!current.State.Completed && !dispatched)
        {
            if (current.State.WaitingForDifficulty)
                current=session.SelectDungeonAltarDifficulty(current.State.Choices.Last())!;
            clock.AdvanceTo(current.Due ?? throw new InvalidOperationException("祭坛没有原计时截止"));
            current=session.TickDungeonAltar(current) ?? throw new InvalidOperationException("祭坛原波次未发布");
            if (current.Batch is not {} batch) continue;
            foreach (var actor in batch.Actors)
            {
                bool final=current.State.Round==current.State.Plan.Definition.RoundCount-1 && current.State.NextDue is null &&
                    session.DungeonSnapshot!.LivingMonsters.Count==1 && session.DungeonSnapshot.LivingMonsters.Contains(actor.ObjectKey);
                if (final)
                {
                    await DispatchFinalAsync(fixture,resources,dungeons,rooms,growth,kills,rewards,clock,actor.ObjectKey);
                    dispatched=true;
                    break;
                }
                current=session.ObserveDungeonAltarDeath(new(actor.ObjectKey,1,0x111,1,600,1,0,70,0,650,1,true,new string('A',64)),growth)
                    ?? throw new InvalidOperationException("原祭坛非最终死亡未确认");
            }
        }
        That(dispatched && entry.Entry.DungeonId==11007,"从原极限祭坛实际波次到达最终击");
    }

    static async Task DispatchFinalAsync(QuestSceneTests.Fixture fixture,TcpTestResources resources,
        DfoServer.GameWorld.Dungeons.DungeonCatalog dungeons,DfoServer.GameWorld.Dungeons.DungeonRoomCatalog rooms,
        DfoServer.GameWorld.Characters.CharacterGrowthCatalog growth,DfoServer.GameWorld.Dungeons.DungeonKillExperienceCatalog kills,
        DungeonAltarRewardPolicy rewards,Clock clock,ushort objectKey)
    {
        var session=fixture.Session;
        string path=(string)fixture.Query("SELECT file FROM pragma_database_list WHERE name='main'")!;
        long beforeSources=Convert.ToInt64(fixture.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_non_target_monster_death'"));
        long beforeReceipts=Convert.ToInt64(fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts"));
        var before=session.Selected!;
        SqliteDatabaseBootstrap.ReleaseRuntimePool();
        using var locker=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path,Pooling=false }.ToString());
        locker.Open();
        using (var setup=locker.CreateCommand())
        {
            setup.CommandText="PRAGMA journal_mode=DELETE";
            That(string.Equals(Convert.ToString(setup.ExecuteScalar()),"delete",StringComparison.OrdinalIgnoreCase),"最终祭坛隔离库切换DELETE日志");
        }
        var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquired=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseGate=new ManualResetEventSlim();
        using var stop=new CancellationTokenSource();
        using var output=new MemoryStream();
        using var log=new ServerLog(Path.GetDirectoryName(path)!);
        var sent=new List<(ushort Opcode,byte[] Body)>();
        byte[]? expected=null;
        bool committedBeforeLock=false;
        await using var sender=new GameConnectionSender(fixture.Owner,output,stop,0,(_,opcode,_,body,action)=>
        {
            sent.Add((opcode,body.ToArray()));
            if (action!="formal_altar_monster_death") return;
            committedBeforeLock=session.Phase==SessionPhase.Settlement &&
                Convert.ToInt64(fixture.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_non_target_monster_death'"))==beforeSources+1 &&
                Convert.ToInt64(fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts"))==beforeReceipts;
            expected=fixture.Repository.ReadBattlePassMissions(fixture.Owner,clock.GetUtcNow())!.ToPayload();
            using var hold=locker.CreateCommand(); hold.CommandText="BEGIN EXCLUSIVE"; hold.ExecuteNonQuery();
            held.TrySetResult();
        });
        var context=new GameConnectionContext(new(1,IPAddress.Loopback,output,stop),fixture.Owner,session,sender);
        var stats=new CharacterStatComputer(resources.Characters);
        var town=new TownProjection(resources.Towns,stats,null,resources.ItemResources.EpicPieces,items:resources.ItemResources.Items);
        var registry=new GameCommandRegistry();
        new DungeonHandler(log,dungeons,rooms,growth,kills,town,new CharacterLevelProjection(stats),
            altarRewards:rewards,altarProjectionBusy:SqliteBusyRetry.IsBusy).Register(registry);
        registry.Seal();
        var request=new Packet(1,(ushort)CmdPacketType.DIE_MONSTER,Death(objectKey,before.Id,70));
        Task<CommandResult> dispatch=Task.Run(()=>registry.DispatchAsync(context,request));
        Task? probe=null;
        bool blocked=false,gateAvailable=false,pending=false,lockHeld=false;
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lockHeld=true;
            await Task.Delay(250);
            probe=Task.Run(()=>fixture.Owner.Execute(()=> { acquired.TrySetResult(); releaseGate.Wait(); return true; }));
            blocked=await Task.WhenAny(acquired.Task,Task.Delay(1000))!=acquired.Task;
            // 原生busy_timeout与托管重试可能叠加，保留首次真实BUSY与锁释放断言。
            gateAvailable=await Task.WhenAny(acquired.Task,Task.Delay(TimeSpan.FromSeconds(95)))==acquired.Task;
            pending=gateAvailable && !dispatch.IsCompleted && sent.Count==1;
        }
        finally
        {
            if (lockHeld) { using var release=locker.CreateCommand(); release.CommandText="ROLLBACK"; release.ExecuteNonQuery(); }
            releaseGate.Set();
            if (probe is not null) await probe.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Exception? failure=null;
        CommandResult result=default;
        try { result=await dispatch.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception error) { failure=error; }
        That(committedBeforeLock && blocked && gateAvailable && pending && failure is null && result==CommandResult.Handled,
            "同一次真实39已提交最终死亡，在忙锁后释放账号锁并恢复处理："+failure);
        var settlement=session.CurrentDungeonAltarResult();
        That(sent.Count==2 && sent[0].Opcode==(ushort)NotiPacketType.DIE_MONSTER &&
            sent[1].Opcode==(ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION && expected is not null && sent[1].Body.SequenceEqual(expected),
            "同一次39先发原死亡通知，再恰好一次发布已提交战令页");
        That(beforeReceipts==0 && settlement?.Result is { Stage:0,Commit.Duplicate:false } && settlement.Result.Pending.Fact is { Cleared:true,CompletedRounds:7 } &&
            Convert.ToInt64(fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts"))==beforeReceipts+1 &&
            Convert.ToString(fixture.Query("SELECT reward_json FROM dungeon_altar_clear_receipts"))==JsonSerializer.Serialize(settlement.Result.Pending.Reward) &&
            Convert.ToInt64(fixture.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='altar_non_target_monster_death'"))==beforeSources+1 &&
            fixture.Read().Revision==settlement.Result.Commit.Character.Revision && ReferenceEquals(session.Selected,settlement.Result.Commit.Character) &&
            JsonSerializer.Serialize(fixture.Read().Inventory)==JsonSerializer.Serialize(settlement.Result.Commit.Character.Inventory),
            "不重发死亡包即进入原七轮奖励事务并只提交一次结算与最终来源");
        long revision=fixture.Read().Revision;
        var same=session.BeginDungeonAltarSettlement(rewards,growth);
        That(same?.Result is not null && ReferenceEquals(same.Result.Pending,settlement!.Result!.Pending) &&
            fixture.Read().Revision==revision && Convert.ToInt64(fixture.Query("SELECT count(*) FROM dungeon_altar_clear_receipts"))==beforeReceipts+1,
            "再次查询结算复用原冻结奖励，不重抽或二次提交");
    }

    static byte[] Death(ushort key,ushort reporter,byte level)
    {
        byte[] bytes=new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,key);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4),reporter);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(18),650);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(26),1);
        bytes[30]=1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(31),0x111);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(33),reporter);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(35),600);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(43),1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56),level);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(60),504);
        byte[] hash=MD5.HashData(bytes.AsSpan(0,84));
        bytes[84]=(byte)(hash[0]^hash[13]^hash[8]^hash[10]^0x81);
        bytes[85]=(byte)(hash[1]^hash[9]^hash[5]^hash[12]^0x78);
        bytes[86]=(byte)(hash[2]^hash[6]^hash[14]^hash[4]^0x1A);
        bytes[87]=(byte)(hash[3]^hash[15]^hash[11]^hash[7]^0xBF);
        return bytes;
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        long elapsed;
        public override long TimestampFrequency=>1000;
        public override long GetTimestamp()=>elapsed;
        public override DateTimeOffset GetUtcNow()=>now.AddMilliseconds(elapsed);
        public void AdvanceTo(long value)=>elapsed=Math.Max(elapsed,value);
    }
}
