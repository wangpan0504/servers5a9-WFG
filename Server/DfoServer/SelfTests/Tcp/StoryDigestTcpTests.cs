using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class StoryDigestTcpTests
{
    public static async Task RunAsync(ServerOptions options,string root,TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options,Path.Combine(root,"story-digest"),resources: resources);
        var growth = CharacterGrowthCatalog.Load(resources.Archive,resources.Learning);
        var cases = new (byte Job,byte Level)[] { (0,3),(14,20),(15,95) };
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
            foreach (var (job,level) in cases)
            {
                var candidate = CharacterCreation.Prepare("StoryTcp"+job,resources.Characters.Get(job),resources.Towns.Room,
                    resources.Towns.InitialReturn,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(job,0,level,candidate.InitialLearning with {
                    Experience = growth.GetLevelStartExperience(level) },resources.Learning);
                new CharacterRepository(fixture.Database).Create(owner,candidate with {
                    Level = level, InitialLearning = learning, AcceptedStoryLevel = 1 });
            }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            string before = fixture.Snapshot();
            peer.Send(1446,[]);
            // 1347需要已选角色；未选角通过合法名册请求同步，首次8仅登记请求状态。
            peer.Send(8,[255,255,2]); peer.Send(8,[255,255,2]); peer.Expect(0,2);
            That(fixture.Snapshot() == before,"未选角的空1446不保存剧情等级且没有ACK/NOTI");
            for (ushort slot = 0; slot < cases.Length; slot++)
            {
                var packets = peer.Select(slot);
                Bytes(packets[1].Body,[1,0,0,0],"初次选角1382回放既有剧情接受等级1");
                before = fixture.Snapshot();
                foreach (var body in new byte[][] { [0],[1,0,0,0] })
                {
                    peer.Send(1446,body); peer.Barrier();
                    That(fixture.Snapshot() == before,"非空1446不能当成等级字段，也不保存或伪造应答");
                }
                var prior = fixture.Query("SELECT level,accepted_story_level,revision FROM characters WHERE id=$p0",peer.Id)[0];
                string assets = Assets();
                peer.Send(1446,[]); peer.Barrier();
                That(fixture.Query("SELECT level,accepted_story_level,revision FROM characters WHERE id=$p0",peer.Id)
                        is [[long level,long story,long revision]] && level == cases[slot].Level && story == level &&
                    revision == Convert.ToInt64(prior[2])+1 && Assets() == assets,
                    "空1446按服务端当前角色等级推进一次revision，保持角色资产技能且没有ACK/NOTI");
                before = fixture.Snapshot();
                peer.Send(1446,[]); peer.Barrier();
                That(fixture.Snapshot() == before,"重复1446不写库、不递增revision、无额外通知");
                peer.Send(7,[]); peer.Expect(1,7,[1]); peer.Expect(0,2);
                var reselected = peer.Select(slot);
                Bytes(reselected[1].Body,Build(writer => writer.Write((uint)cases[slot].Level)),"同连接重选1382回放已提交剧情接受等级");
                peer.Send(7,[]); peer.Expect(1,7,[1]); peer.Expect(0,2);
            }
        }
        await fixture.StopAsync();
        await fixture.StartAsync("story-digest-restart");
        using (var peer = Open(fixture))
            for (ushort slot = 0; slot < cases.Length; slot++)
            {
                var packets = peer.Select(slot);
                Bytes(packets[1].Body,Build(writer => writer.Write((uint)cases[slot].Level)),"服务重启后1382恢复各角色独立剧情接受等级");
                string before = fixture.Snapshot();
                peer.Send(1446,[]); peer.Barrier();
                That(fixture.Snapshot() == before,"重启后重复1446仍无保存或应答");
                peer.Send(7,[]); peer.Expect(1,7,[1]); peer.Expect(0,2);
            }
        await fixture.StopAsync();

        string Assets()
        {
            string columns = string.Join(',',fixture.Query("PRAGMA table_info(characters)").Select(row => (string)row[1])
                .Where(name => name is not ("revision" or "accepted_story_level")).Select(name => '"'+name+'"'));
            string[] tables = fixture.Query("SELECT name FROM sqlite_schema WHERE type='table' AND name!='characters' ORDER BY name")
                .Select(row => (string)row[0]).ToArray();
            return fixture.Snapshot("SELECT "+columns+" FROM characters ORDER BY id")+
                JsonSerializer.Serialize(tables.Select(table => fixture.Query("SELECT * FROM \""+table+"\" ORDER BY rowid")));
        }
    }
}
