using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    // 实验性（#256，未在95客户端实证）：深渊派对入场走真实 TCP：op16 请求体 [7]=1。
    // 未完成区域 [hell quest] 6080 → 拒绝包 [0,0xAD,0,0]；已完成 → 放行并带深渊 DUNGEON_INFO/START_MAP 字段。
    static async Task VerifyHellPartyEntryAsync(ServerOptions source,string root,TcpTestResources resources,CharacterGrowthCatalog growth)
    {
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"hell-party-entry"),resources:resources);
        await fixture.StartAsync();
        var baseDone = new HashSet<int>();
        var area = resources.Towns.Areas.Values.First(row => row.Level <= 95 && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 291100308));
        baseDone.UnionWith(area.Quests);
        if (area.AnyQuests.Length != 0) baseDone.Add(area.AnyQuests[0]);
        var walk = area.Map(baseDone).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done => baseDone.Contains(done))).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(walk.X+walk.Width/2)),checked((ushort)(walk.Y+walk.Height/2)));
        // 实机样本：副本 291100308，[4]=4，[7]=1，[16:20]=6114。
        byte[] request = Convert.FromHexString("94D6591104000001" + "00FFFF" + "0000000000" + "E2170000" + "00" + "00000000");
        foreach (bool qualified in new[] { false, true })
        {
            var done = new HashSet<int>(baseDone);
            if (qualified) done.Add(6080);
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
            {
                var candidate = CharacterCreation.Prepare(qualified ? "HellYes" : "HellNo",resources.Characters.Get(0),position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with
                    { Experience=growth.GetLevelStartExperience(95) },resources.Learning);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with { Level=95,AcceptedStoryLevel=95,
                    CompletedQuests=done.ToArray(),InitialLearning=learning });
            }
            using var peer = Open(fixture);
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            SelectMap(peer);
            peer.Send(16,request);
            if (!qualified)
            {
                peer.Expect(1,16,[0,0xAD,0,0]); peer.Barrier();
                That(fixture.Query("SELECT count(*) FROM character_dungeon_state WHERE character_id=$p0 AND last_attempt>0",peer.Id) is [[0L]],
                    "深渊派对：未完成 [hell quest] 6080 被拒绝，且不产生副本尝试");
                continue;
            }
            var information = peer.Expect(0,28).Body;
            var map = peer.Expect(0,29).Body;
            That(information.Length == 36 && BinaryPrimitives.ReadUInt32LittleEndian(information) == 291100308 && information[4] == 4 &&
                information[10] == 2 && information[11] == 2 && BinaryPrimitives.ReadUInt16LittleEndian(information.AsSpan(14)) == 1 &&
                information[18] == 0x0B && map[7] == 1,
                "深渊派对：已完成 6080 放行，DUNGEON_INFO 带深渊房间坐标/hellPartyEnabled/Value2，START_MAP 模式字节=1");
            // 实验性：起点房间不是深渊房间，START_MAP 只有普通 actor 行（无波次隐藏行），也不发 NOTI 679（Barrier 断言之后没有多余包）；
            // 深渊房间（(2,2)）的波次行/679/波次死亡登记由单元用例覆盖，TCP 未走到该房间（需逐房间击杀，成本高，未自动化）。
            That(map.Length == 23 + map[18] * 22, "深渊派对起点房间 START_MAP：actor 行数与长度一致，无隐藏波次行");
            Load(peer); peer.Expect(0,36); peer.Barrier();
        }
    }
}