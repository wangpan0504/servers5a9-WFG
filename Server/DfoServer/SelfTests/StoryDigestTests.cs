using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class StoryDigestTests
{
    public static void Run()
    {
        int checks = Count;
        using var fixture = new Fixture();
        for (byte job = 0; job < 16; job++)
        {
            byte level = new byte[] { 1,20,95 }[job % 3];
            var before = fixture.Create("Story"+job,job,level,0);
            var after = fixture.Repository.AdvanceAcceptedStoryLevel(fixture.Owner,before);
            That(after.AcceptedStoryLevel == level && after.Revision == before.Revision+1 && Assets(after) == Assets(before),
                $"职业{job}剧情接受等级按本角色等级推进，保持职业、任务、资产和技能");
            That(Same(after,fixture.Repository.LoadCharacter(fixture.Owner,after.Slot)),"剧情接受等级实际提交并可重开恢复");
            using (var writer = fixture.Open())
            using (var transaction = writer.BeginTransaction(deferred: false))
                That(ReferenceEquals(after,fixture.Repository.AdvanceAcceptedStoryLevel(fixture.Owner,after)),
                    "重复剧情接受不申请writer，返回原在线快照且不递增revision");
            Reject<InvalidOperationException>(() => fixture.Repository.AdvanceAcceptedStoryLevel(fixture.Owner,before),
                "旧revision不能重新提交剧情接受");
        }
        var ahead = fixture.Create("StoryAhead",14,3,95);
        using (var writer = fixture.Open())
        using (var transaction = writer.BeginTransaction(deferred: false))
            That(ReferenceEquals(ahead,fixture.Repository.AdvanceAcceptedStoryLevel(fixture.Owner,ahead)),
                "已接受等级高于当前等级时保持最大值，不降级或申请writer");

        foreach (var field in new[] { CharacterProgressField.Level,CharacterProgressField.Story })
        {
            var before = fixture.Create("StoryCas"+(int)field,15,20,1);
            var changed = fixture.Repository.UpdateProgress(fixture.Owner,new(field,fixture.Owner.Account,before.Name,2,0));
            That(changed.Revision == before.Revision,"CAS fixture只改变等级或剧情字段，保持原revision");
            Reject<InvalidOperationException>(() => fixture.Repository.AdvanceAcceptedStoryLevel(fixture.Owner,before),
                "同revision的等级或剧情来源变化仍拒绝旧快照");
            That(Same(changed,fixture.Repository.LoadCharacter(fixture.Owner,before.Slot)),"拒绝旧来源保持新进度");
        }
        var foreign = fixture.Create("StoryOwner",0,20,1);
        using (var other = fixture.Owners.Acquire("story-other",() => { }))
            Reject<InvalidDataException>(() => fixture.Repository.AdvanceAcceptedStoryLevel(other,foreign),"其他账号不能保存本角色剧情等级");

        var first = fixture.Create("StorySession",14,20,1);
        var second = fixture.Create("StorySecond",15,95,1);
        var session = new GameSession(fixture.Repository,fixture.Towns,fixture.Owner);
        That(session.AcceptStoryDigest() is null,"未选角色不能推进剧情接受等级");
        session.Select(first.Slot);
        That(session.AcceptStoryDigest() is null,"尚未Ready的选角不能保存剧情等级");
        session.MarkReady();
        var selected = session.Selected!;
        fixture.Sql("CREATE TRIGGER story_abort AFTER UPDATE OF accepted_story_level ON characters BEGIN SELECT RAISE(ABORT,'story rollback'); END");
        Reject<SqliteException>(() => session.AcceptStoryDigest(),"剧情等级SQL提交失败向上传递");
        That(ReferenceEquals(selected,session.Selected) && Same(selected,fixture.Repository.LoadCharacter(fixture.Owner,first.Slot)),
            "SQL失败完整回滚剧情等级及revision，保留原Selected");
        fixture.Sql("DROP TRIGGER story_abort");
        var accepted = session.AcceptStoryDigest()!;
        That(ReferenceEquals(accepted,session.Selected) && accepted.AcceptedStoryLevel == first.Level &&
            accepted.Revision == selected.Revision+1 && Assets(accepted) == Assets(selected),"成功提交后才安装在线剧情快照");
        That(ReferenceEquals(accepted,session.AcceptStoryDigest()),"在线重复接受保留原对象和revision");
        long epoch = session.SelectionEpoch;
        session.ReturnToRoster();
        That(session.AcceptStoryDigest() is null,"返回名册后无选中角色，不保存迟到剧情通知");
        session.Select(second.Slot); session.MarkReady();
        var another = session.AcceptStoryDigest()!;
        That(session.SelectionEpoch > epoch && another.Id == second.Id && another.AcceptedStoryLevel == second.Level &&
            fixture.Repository.LoadCharacter(fixture.Owner,first.Slot).AcceptedStoryLevel == first.Level,
            "重选后只推进当前角色，不复用旧选角或影响前一角色");
        session.ReturnToRoster(); session.Select(first.Slot); session.MarkReady();
        That(session.Selected!.AcceptedStoryLevel == first.Level && Same(session.Selected,fixture.Repository.LoadCharacter(fixture.Owner,first.Slot)),
            "重新选角从持久层恢复剧情接受等级");
        using var replacement = fixture.Owners.Acquire(fixture.Owner.Account,() => { });
        Reject<InvalidOperationException>(() => fixture.Repository.AdvanceAcceptedStoryLevel(fixture.Owner,foreign),"过期owner不能提交剧情等级");
        Reject<InvalidOperationException>(() => session.AcceptStoryDigest(),"旧会话在账号接管后不能推进剧情等级");
        That(fixture.Repository.LoadCharacter(replacement,foreign.Slot).AcceptedStoryLevel == 1,"过期owner拒绝没有部分写入");
        Console.WriteLine($"PASS {Count-checks} 剧情接受等级单调保存、幂等、CAS与会话回滚断言（合成资源）");
    }

    static string Assets(Character character) => JsonSerializer.Serialize(character with { Revision = 0, AcceptedStoryLevel = 0 });
    static bool Same(Character first,Character second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(),"S5A9-story-digest-"+Guid.NewGuid().ToString("N"));
        readonly string database;
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public TownCatalog Towns { get; }

        public Fixture()
        {
            Directory.CreateDirectory(root); database = Path.Combine(root,"game.db");
            var room = new Location(1,0,10,10); var street = new Location(2,0,10,10);
            TownArea Area(Location point,bool isRoom,Location target) => new(point.Town,point.Area,1,[],[],[],point,
                new("synthetic.map",[new(new(0,0,20,20),[],[])],[new(new(0,0,20,20),target.Town,target.Area,[],[])],isRoom,[]),[],[]);
            Towns = new(new() { [(1,0)] = Area(room,true,street),[(2,0)] = Area(street,false,room) },
                room,street,new(0,[],[]),[],new Dictionary<uint,uint[]>());
            Repository = new(database); Owner = Owners.Acquire("story-digest",() => { });
        }

        public Character Create(string name,byte job,byte level,uint story) => Repository.Create(Owner,
            new(name,Towns.Room,Towns.InitialReturn) {
                Job = job, Level = level, AcceptedStoryLevel = story, Skills = [new(100,1)],
                InitialLearning = new(123456,[new(0,50,5,[new(150,100,1)]),new(1,70,7,[new(151,100,1)])]) { BonusSp = 3,BonusTp = 4 },
                CompletedQuests = [101], ActiveMainQuests = [new(0,100,17) { AcceptanceId = Guid.NewGuid() }],
                DungeonState = CharacterDungeonState.NewOrdinary,
                Inventory = new([new(0,new(InventoryListType.Main,65),new(177,ItemKind.Consumable,7))],Gold:1234)
            });

        public SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database,Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        public void Sql(string sql)
        {
            using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose() { Owner.Dispose(); Directory.Delete(root,recursive: true); }
    }
}
