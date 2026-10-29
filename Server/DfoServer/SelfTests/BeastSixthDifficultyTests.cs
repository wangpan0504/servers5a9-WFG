using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using DfoServer.Testing.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 第六难度按钮的 op16 形状：难度4 + 特殊模式2（用户 2026-10-28 确认 SPECIAL MODE=2 决定这个模态）。
// 只走生产 DungeonHandler 与生产 Session，证明入口分派，不代表客户端点击或实机战斗。
internal static class BeastSixthDifficultyTests
{
    // 25B：DungeonIndex4 / Parameter1 / [5]2 / [7]1 / [8]1 / [9]2 / [11]4 / [15]1 / [16]4 / [20]1 / [21]4。
    static byte[] Selection(uint dungeon, byte difficulty, byte flag7 = 0)
    {
        byte[] body = new byte[25];
        BinaryPrimitives.WriteUInt32LittleEndian(body, dungeon);
        body[4] = difficulty;
        body[7] = flag7;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(9), ushort.MaxValue);
        return body;
    }

    public static async Task RunAsync(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-beast-sixth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var owners = new AccountOwners();
        var catalog = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var drops = SelfTestCatalogs.MonsterDrops(pvf);
        var learning = SelfTestCatalogs.SkillLearning(pvf);
        var growth = SelfTestCatalogs.Growth(pvf, learning);
        var repository = new CharacterRepository(Path.Combine(root, "game.db"), SelfTestCatalogs.Quests(pvf));
        var dropPolicy = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(pvf), itemResources.DungeonDropItems,
            drops.Get, itemResources.Items.Get, itemResources.DungeonSpecialDrops);
        var computer = new CharacterStatComputer(characters);
        try
        {
            using var log = new ServerLog(Path.Combine(root, "logs"));
            var handler = new DungeonHandler(log, catalog, rooms, growth, SelfTestCatalogs.KillExperience(pvf),
                new TownProjection(towns, computer, null, itemResources.EpicPieces), new CharacterLevelProjection(computer),
                drops: drops, items: itemResources.Items, pieces: itemResources.EpicPieces,
                prepareDrops: entry => dropPolicy.PrepareEntry(entry));
            var commands = new GameCommandRegistry();
            handler.Register(commands);
            commands.Seal();

            // 魔兽入口区域：310 出现在该区域世界地图的选项里，位置取已解锁条件格的内部点。
            var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 310));
            var completed = new HashSet<int>(area.Quests) { 5927 };
            if (area.AnyQuests.Length != 0) completed.Add(area.AnyQuests[0]);
            completed.UnionWith(area.Map(completed).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
            var bounds = area.Map(completed).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(completed.Contains)).Bounds;
            var position = new Location(area.Town, area.Area, (ushort)(bounds.X + bounds.Width / 2), (ushort)(bounds.Y + bounds.Height / 2));
            var at = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(8));
            while (!BeastDungeonRotation.Contains(BeastDungeonRotation.ServiceDay(at), 310)) at = at.AddDays(1);

            foreach (var (name, difficulty, flag7, beast, enter) in new[]
            {
                // 第六难度：难度4 + MAZE2，必须进入魔兽拓扑。
                (Name: "sixth", Difficulty: (byte)4, Flag7: (byte)2, Beast: true, Enter: true),
                // 第五按钮的普通噩梦（实机 2026-10-25T14:49:06 的真实形状）：难度4、MAZE0，不能进魔兽。
                (Name: "nightmare", Difficulty: (byte)4, Flag7: (byte)0, Beast: false, Enter: true),
                // 低难度保持普通拓扑。
                (Name: "plain", Difficulty: (byte)0, Flag7: (byte)0, Beast: false, Enter: true),
                // MAZE1 是深渊拓扑，必须留给深渊分支：票和资格另算，这里只证明它不会被当成魔兽。
                (Name: "abyss", Difficulty: (byte)4, Flag7: (byte)1, Beast: false, Enter: false)
            })
            {
                using var lease = owners.Acquire("beast-sixth-" + name, () => { });
                // 角色名受 CharacterName.IsValid 限制：CP936 下最多 12 字节，前缀必须短。
                var candidate = CharacterCreation.Prepare("B" + name, characters.Get(0), position, position, learning.Get);
                var synced = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(95) }, learning);
                repository.Create(lease, candidate with
                    { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = completed.ToArray(), InitialLearning = synced });
                var clock = new DailyBoosterTestClock(at);
                var session = new GameSession(repository, towns, lease, questCatalog: SelfTestCatalogs.Quests(pvf), timeProvider: clock,
                    items: itemResources.Items, goldLimits: itemResources.GoldLimits, pieces: itemResources.EpicPieces);
                session.Select(0, resumeTown: true);
                session.MarkReady();
                using var lifetime = new CancellationTokenSource();
                using var bytes = new MemoryStream();
                await using var sender = new GameConnectionSender(lease, bytes, lifetime, 0, (_, _, _, _, _) => { });
                var context = new GameConnectionContext(new(1, IPAddress.Loopback, bytes, lifetime), lease, session, sender);
                var result = await commands.DispatchAsync(context, new Packet(1, (ushort)CmdPacketType.SELECT_DUNGEON,
                    Selection(310, difficulty, flag7)));
                var scene = session.CurrentFormalDungeonScene();
                if (beast && catalog.Get(310).BeastLayouts.Any(layout => layout.ResourceError is not null))
                {
                    That(result == CommandResult.Handled && scene is null &&
                        session.LastFormalDungeonRejection == "entry-plan:beast-layout-resource-unavailable" &&
                        session.Phase is not (SessionPhase.Loading or SessionPhase.Dungeon) &&
                        repository.LoadBeastDungeonUsed(lease,session.Selected!.Id,BeastDungeonRotation.ServiceDay(at),310) == 0,
                        "第六难度缺布局资源时明确拒绝且不扣次数：" + session.LastFormalDungeonRejection);
                    continue;
                }
                if (!enter)
                {
                    That(scene?.Entry.BeastMode != true, $"深渊拓扑 MAZE1 不会被当成魔兽：{name} / 拒绝={session.LastFormalDungeonRejection}");
                    continue;
                }
                // 选图提交后一律先进 Loading，靠 CompleteFormalDungeonLoading 才落到 Dungeon。
                That(result == CommandResult.Handled && scene is not null && scene.Entry.BeastMode == beast &&
                    (scene.Entry.Maze.BeastLayout is not null) == beast &&
                    session.Phase is SessionPhase.Loading or SessionPhase.Dungeon,
                    $"第六难度入口分派：{name} / 难度{difficulty} / [7]={flag7} / phase={session.Phase} / " +
                    $"beast={scene?.Entry.BeastMode} / 拒绝={session.LastFormalDungeonRejection}");
                if (!beast)
                {
                    That(scene!.Entry.Maze.MapSpecification!.All(map => map.Kind != "Beast"), "普通难度进入 310 时不带任何魔兽格");
                    continue;
                }

                That(scene!.Entry.Maze.MapSpecification!.Count == 15 &&
                    scene.Entry.Maze.MapSpecification!.Count(map => map.Kind == "Beast") > 0,
                    "魔兽入口使用十五格魔兽拓扑：" + name);
                That(scene.Entry.BaseBossMap.Kind == "Beast" || scene.Entry.Maze.MapSpecification!.Contains(scene.Entry.BaseBossMap),
                    "魔兽入口的Boss格来自魔兽拓扑：" + name);
                That(session.CompleteFormalDungeonLoading() is not null, "魔兽入口通过正式加载屏障：" + name);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine($"PASS {Count - before} 第六难度按钮入口分派断言（非实机）");
    }
}
