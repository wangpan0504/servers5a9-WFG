using DfoServer.Game.Dungeons;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonDespairEntryTests
{
    public static void Run(TcpTestResources resources)
    {
        int before=Count;
        byte[] selection = new byte[42];
        selection[9] = 0x34; selection[10] = 0x12;
        selection[20] = 39; selection[39] = 72;
        Bytes(DfoServer.Network.Builders.DungeonPackets.EmptySelectNotice(0x1234,72,39),selection,
            "选图通知同时保留传奇深渊、绝望与悲叹的独立字段，不能将第二个参数复用于绝望楼层");
        Reject<ArgumentOutOfRangeException>(() => DfoServer.Network.Builders.DungeonPackets.EmptySelectNotice(despairLayer:101),
            "绝望选图楼层拒绝超出100层");
        var dungeons=SelfTestCatalogs.Dungeons(resources.Archive);
        var tower=DfoServer.GameWorld.Dungeons.TowerOfDespairCatalog.Load(resources.Archive);
        var growth=SelfTestCatalogs.Growth(resources.Archive);
        using var fixture=new QuestSceneTests.Fixture(resources.Towns,resources.Characters,resources.Learning,growth,
            resources.Quests,false,70);
        var repo=fixture.Repository;
        var character=fixture.Read();
        var progress=repo.LoadDespairProgress(fixture.Owner,character);
        const int day=739911;
        var first=DungeonDespairEntryPolicy.Prepare(11008,character,day,progress,new(0,0),tower,dungeons.Get(11008),0)!;
        That(progress==new DungeonDespairProgress() && first.Ticket is null && first.DungeonId==11008 && first.Entry.Layer==1,
            "新角色绝望从第一层开始，首进免4473，不能复用普通必扣票");
        var retry=DungeonDespairEntryPolicy.Prepare(11008,character,day,progress,new(1,1),tower,dungeons.Get(11008),0);
        That(retry?.Ticket is {ItemId:4473,Count:1},"本版邀请函说明与官方失败重试规则绑定4473");
        That(DungeonDespairEntryPolicy.Prepare(11009,character,day,progress,new(0,0),tower,dungeons.Get(11008),0) is null &&
            DungeonDespairEntryPolicy.Prepare(11008,character,day,progress with {LastClearDay=day},new(1,1),tower,dungeons.Get(11008),0) is null,
            "不能直报未来楼层，已成功通关当天不能免费继续升层");
        That(DungeonDespairEntryPolicy.Prepare(11008,character,day,progress,new(0,3),tower,dungeons.Get(11008),0) is null &&
            DungeonDespairEntryPolicy.Prepare(11008,character,day,progress,new(0,3),tower,dungeons.Get(11008),2) is not null &&
            DungeonDespairEntryPolicy.Prepare(11008,character,day,progress,new(1,3),tower,dungeons.Get(11008),0) is not null,
            "账号基础角色名额与PVF冒险团增量独立于既有角色失败重试");
        var identity=new DungeonRunIdentity(character.Id,0,character.DungeonState!.LastAttempt+1,Guid.NewGuid(),1);
        var charge=new DungeonEntryCharge(null,0,identity,15130) {DungeonId=11008,Special=first.Entry};
        fixture.Sql("CREATE TRIGGER fail_despair_entry BEFORE INSERT ON dungeon_special_entry_receipts BEGIN SELECT RAISE(ABORT,'test rollback'); END");
        try
        {
            Reject<Microsoft.Data.Sqlite.SqliteException>(()=>repo.CommitDungeonEntry(fixture.Owner,character,500,charge),
                "绝望入场回执失败触发事务回滚");
            That(fixture.Read().DungeonState==character.DungeonState && repo.LoadDespairProgress(fixture.Owner,character)==progress,
                "失败不留尝试号、次数或半行初始进度");
        }
        finally {fixture.Sql("DROP TRIGGER fail_despair_entry");}
        character=repo.CommitDungeonEntry(fixture.Owner,character,500,charge).Character;
        That(repo.LoadDespairProgress(fixture.Owner,character)==progress &&
            repo.LoadSpecialEntryUsage(fixture.Owner,character,day,DungeonSpecialEntryKind.Despair)==new DungeonSpecialEntryUsage(1,1),
            "首进尝试号、角色名额与初始楼层在同一事务持久化");
        fixture.Sql("UPDATE dungeon_despair_progress SET next_layer=2,last_clear_day=$p0,clear_count=1",day);
        var stale=charge with {Identity=identity with {InstanceId=Guid.NewGuid(),TaskAttempt=identity.TaskAttempt+1},
            Special=first.Entry with {Expected=new(1,1)}};
        long revision=character.Revision;
        Reject<InvalidOperationException>(()=>repo.CommitDungeonEntry(fixture.Owner,character,501,stale),
            "入场准备之后楼层推进不能被旧进度覆盖");
        That(fixture.Read().Revision==revision && fixture.Read().DungeonState==character.DungeonState &&
            repo.LoadDespairProgress(fixture.Owner,character)==new DungeonDespairProgress(2,day,1),
            "进度版本冲突回滚整个尝试预留并保留已通关楼层");
        Console.WriteLine($"PASS {Count-before} 绝望首次准入、重试票、账号名额和进度版本原子性断言");
    }
}
