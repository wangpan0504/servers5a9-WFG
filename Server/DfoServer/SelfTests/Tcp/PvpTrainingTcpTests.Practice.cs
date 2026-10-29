using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class PvpTrainingTcpTests
{
    static async Task PracticeAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var f = new TcpTestFixture(source, Path.Combine(root, "pvp-practice"), resources: resources);
        const string account = "practice-tcp";
        var repo = new CharacterRepository(f.Database, resources.Quests);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        ushort id;
        using (var owner = new AccountOwners().Acquire(account, () => { }))
        {
            var seed = CharacterCreation.Prepare("PracticeTCP", resources.Characters.Get(0),
                new Location(10, 0, 474, 234), resources.Towns.InitialReturn, resources.Learning.Get);
            var prepared = seed with { Level = 60, AcceptedStoryLevel = 60,
                InitialLearning = SkillPointLedger.Synchronize(0, 0, 60, seed.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(60) }, resources.Learning) };
            id = repo.Create(owner, prepared).Id;
            using var observerOwner = new AccountOwners().Acquire("practice-observer", () => { });
            repo.Create(observerOwner, prepared with { Name = "PracticeObs" });
        }
        MissionAtomic(f, resources);
        await f.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(f);
        byte[] create = [0, 2, 0, 0, 0, 0xD1, 0xB5, 0, 0, 0, 1, 0];
        byte[] left = [(byte)id, (byte)(id >> 8), 6, 1, 0, 0, 255, 255];
        foreach (byte channel in new byte[] { 101, 47, 51, 55 })
        {
            using var peer = Open(f, account, gamePort: endpoints[channel], channelNumber: channel);
            peer.Select(prepared: true); peer.Barrier();
            if (channel == 55) That(peer.LoginEndpoint[3] == 13, "新版自由入口LOGIN必须保留type13，旧5归一到4不能启用type13控件");
            string before = f.Snapshot();
            Enter();
            peer.Send(50, create); peer.Expect(1, 50, [0, 19, 0]);
            peer.Send(33, [33, 0, 0x70, 0x0A, 0x20, 0, 0]); peer.Expect(1, 33, [0, 19, 0]);
            peer.Send(52, [1, 254]); peer.Send(52, [0, 0]); peer.Barrier();
            peer.Send(50, create); peer.Expect(1, 50, [0, 19, 0]);
            peer.Send(52, [0, 254]); peer.Expect(0, 43, left);PvpArenaTcpTests.TownReturn(peer); peer.Barrier();
            That(before == f.Snapshot(), "普通练习无任务前置，错误进度和退出不写任务或角色资产");
            Enter();
            peer.Send(13); peer.Expect(0, 43, left);PvpArenaTcpTests.TownReturn(peer); peer.Barrier();
            Enter();
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(prepared: true); peer.Barrier();
            peer.Send(13); peer.Expect(1, 13, [0, 19, 0]); peer.Barrier();
            Enter();
            peer.Send(52, [0, 254]); peer.Expect(0, 43, left);PvpArenaTcpTests.TownReturn(peer); peer.Barrier();
            That(before == f.Snapshot(), "换角清除普通练习实例，重入不恢复旧实例或修改资产");
            if (channel == 55)
            {
                using var observer = Open(f, "practice-observer", gamePort: endpoints[channel], channelNumber: channel);
                observer.Select(prepared: true);
                PvpArenaTcpTests.TownPeer(observer, peer.Id);
                PvpArenaTcpTests.TownPeer(peer, observer.Id);
                observer.Barrier(); peer.Barrier();
                before = f.Snapshot();
                peer.Send(50, [3, 0, 0, 0, 0, 0]); peer.Expect(1, 50, [1]);
                That(U16(peer.Expect(0, 2).Body, 121) == id, "自由练习频道房间先加载真实本人");
                var room = peer.Expect(0, 41).Body;
                ushort roomId = U16(room, 2);
                That(room[9] == 1 && U16(room, 11) == id, "自由练习频道可创建实际公共房间并占本人席位");
                Bytes(observer.Expect(0, 41).Body, room, "type13大厅旁观者实时收到同频道实际房间目录");
                peer.Send(52, [0, 254]); PvpArenaTcpTests.Left(peer, roomId, 1, 0); peer.Barrier();
                observer.Expect(0, 42, Build(w => { w.Write(roomId); w.Write((byte)0); w.Write((byte)255);
                    w.Write((ushort)0); w.Write((byte)1); w.Write(0u); }));
                observer.Barrier();
                That(before == f.Snapshot(), "自由练习频道退出公共房间恢复大厅且不改资产");
                observer.Send(3, [1]); observer.Expect(0, 6, Build(w => w.Write(peer.Id)));
                observer.Expect(1, 3, [1]); observer.Closed();
                peer.Expect(0, 6, Build(w => w.Write(observer.Id))); peer.Barrier();
            }
            peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();

            void Enter()
            {
                peer.Send(50, create); peer.Expect(1, 50, [1]);
                var body = peer.Expect(0, 41).Body;
                That(body.Length == 43 && U16(body) == 1 && U16(body, 2) == id && body[10] == 0 && body[13] == 6,
                    "普通练习使用独立mode6且队长是当前角色");
                for (int seat = 0; seat < 8; seat++)
                    That(body[14 + seat * 3] == 0 && U16(body, 15 + seat * 3) == (seat == 0 ? id : ushort.MaxValue),
                        "普通练习八席只占本人，其余FFFF");
            }
        }
        MissionPractice(f, resources, id, account, endpoints[101]);
        await f.StopAsync();
    }
}
