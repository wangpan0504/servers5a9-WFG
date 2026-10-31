using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class EmotionTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var titles = new DfoServer.GameWorld.Quests.TitleCatalog(resources.Archive, resources.Quests);
        EmotionSelectionTests.RunResources(resources.Archive, titles);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "emotion"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        using var actor = Open(fixture, "moodowner", gamePort: endpoints[21], channelNumber: 21);
        using var observer = Open(fixture, "moodobserver", gamePort: endpoints[21], channelNumber: 21);
        actor.Send(254, [1, 0]); actor.Expect(1, 254, [0, 3, 0]);
        actor.Create("MoodFirst");
        actor.Create("MoodSecond");
        observer.Create("MoodOther");
        actor.Select(); observer.Select();
        actor.Send(1426); actor.SendBarrier(); actor.Batch((0, 23), (0, 24), (1, 1347));
        observer.Send(1426); observer.SendBarrier(); observer.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
        actor.SendBarrier(); actor.Batch((0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
        actor.Send(254, [1, 0]); actor.Expect(1, 254, [1, 1, 0]);
        var own = actor.Expect(0, 2);
        actor.Barrier();
        observer.SendBarrier(); var broadcast = observer.Batch((0, 2), (1, 1347));
        That(own.Body[4] == 21 && broadcast[0].Body[4] == 21 && U16(own.Body, 119) == 1 && U16(broadcast[0].Body, 119) == 1,
            "实收CMD254先提交并ACK，本人和同频道同屏Actor投影已提交心情");
        That(fixture.Query("SELECT name,emotion FROM characters ORDER BY id") is [["MoodFirst", 1L], ["MoodSecond", 0L], ["MoodOther", 0L]],
            "角色独立保存，不把同账号或其他账号的心情一起修改");
        string saved = fixture.Snapshot();
        actor.Send(254, [1, 0]); actor.Expect(1, 254, [1, 1, 0]); actor.Barrier(); observer.Barrier();
        actor.Send(254, [255, 255]); actor.Expect(1, 254, [0, 1, 0]); actor.Barrier();
        actor.Send(254, [10, 0]); actor.Expect(1, 254, [0, 3, 0]); actor.Barrier();
        actor.Send(254, [232, 3]); actor.Expect(1, 254, [0, 3, 0]); actor.Barrier();
        actor.Send(254, [233, 3]); actor.Expect(1, 254, [0, 3, 0]); actor.Barrier();
        That(fixture.Snapshot() == saved, "重复、无效及锁定心情不写库，也不截断成成功");
        actor.Send(7); actor.Expect(0, 6); actor.Expect(1, 7, [1]); actor.Expect(0, 2);
        observer.SendBarrier(); observer.Batch((0, 6), (1, 1347));
        int section = titles.Sections.Single(pair => pair.Value.Emotion == 10).Key;
        Execute($"INSERT INTO character_title_sections SELECT id,account,{section},1 FROM characters WHERE name='MoodFirst'");
        var replay = actor.Select();
        That(U16(replay[2].Body, 119) == 1, "重新选角从数据库回放心情");
        actor.Send(254, [10, 0]); actor.Expect(1, 254, [1, 10, 0]);
        That(U16(actor.Expect(0, 2).Body, 119) == 10, "持久章节资格回放后选择真实称号心情成功，不再误报不存在");
        actor.Barrier();
        actor.Send(254, [0, 0]); actor.Expect(1, 254, [1, 0, 0]); actor.Expect(0, 2);
        actor.Send(3, [0]); actor.Expect(1, 3, [1]); actor.Closed();
        using var restarted = Open(fixture, "moodowner");
        That(U16(restarted.Select()[2].Body, 119) == 0, "普通心情重置后重连保持0");
        Execute("CREATE TRIGGER fail_mood BEFORE UPDATE OF emotion ON characters BEGIN SELECT RAISE(ABORT,'mood'); END");
        restarted.Send(254, [1, 0]); restarted.Closed();
        That(fixture.Query("SELECT emotion FROM characters WHERE name='MoodFirst'") is [[0L]], "在线SQL失败没有ACK成功和半份角色心情");
        Execute("DROP TRIGGER fail_mood");
        void Execute(string sql)
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }
}
