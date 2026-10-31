using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 退役入口仍须经真实Handler拒绝，普通310的难度4 + MAZE0继续入场。
// 只走生产 DungeonHandler 与生产 Session，证明入口分派，不代表客户端点击或实机战斗。
internal static class DungeonRetiredEntryTests
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
        string root = Path.Combine(Path.GetTempPath(), "S5A9-retired-entry-" + Guid.NewGuid().ToString("N"));
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

            // 普通中央公园区域：310 出现在该区域世界地图的选项里，位置取已解锁条件格的内部点。
            var area = towns.Areas.Values.First(row => row.Town == 31 && row.DungeonWorldMap == 39 && row.DungeonWorldMap is { } world &&
                towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 310));
            var completed = new HashSet<int>(area.Quests) { 5927 };
            if (area.AnyQuests.Length != 0) completed.Add(area.AnyQuests[0]);
            completed.UnionWith(area.Map(completed).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
            var bounds = area.Map(completed).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(completed.Contains)).Bounds;
            var position = new Location(area.Town, area.Area, (ushort)(bounds.X + bounds.Width / 2), (ushort)(bounds.Y + bounds.Height / 2));
            foreach (var (name, dungeon, difficulty, flag7, enter) in new[]
            {
                ("retired310",310u,(byte)4,(byte)2,false),
                ("retired314",314u,(byte)4,(byte)2,false),
                ("gateway",5335u,(byte)2,(byte)0,false),
                ("nightmare",310u,(byte)4,(byte)0,true),
                ("plain",310u,(byte)0,(byte)0,true)
            })
            {
                using var lease = owners.Acquire("retired-entry-" + name, () => { });
                // 角色名受 CharacterName.IsValid 限制：CP936 下最多 12 字节，前缀必须短。
                var candidate = CharacterCreation.Prepare("R" + name, characters.Get(0), position, position, learning.Get);
                var synced = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(95) }, learning);
                repository.Create(lease, candidate with
                    { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = completed.ToArray(), InitialLearning = synced });
                var session = new GameSession(repository, towns, lease, questCatalog: SelfTestCatalogs.Quests(pvf),
                    items: itemResources.Items, pieces: itemResources.EpicPieces,
                    entry: new(new(2,1),22,"[normal]"));
                session.Select(0, resumeTown: true);
                session.MarkReady();
                That(session.BeginDungeonSelection() is not null, "所有请求先取得真实选图上下文");
                using var lifetime = new CancellationTokenSource();
                using var bytes = new MemoryStream();
                var sent = new List<(byte Kind, ushort Opcode, byte[] Body)>();
                await using var sender = new GameConnectionSender(lease, bytes, lifetime, 0,
                    (kind, opcode, _, body, _) => sent.Add((kind, opcode, body.ToArray())));
                var context = new GameConnectionContext(new(1, IPAddress.Loopback, bytes, lifetime), lease, session, sender);
                long revision = session.Selected!.Revision;
                var result = await commands.DispatchAsync(context, new Packet(1, (ushort)CmdPacketType.SELECT_DUNGEON,
                    Selection(dungeon, difficulty, flag7)));
                var scene = session.CurrentFormalDungeonScene();
                if (!enter)
                {
                    That(result == CommandResult.Handled && scene is null && session.Phase == SessionPhase.MapSelect &&
                        session.Selected!.Revision == revision && repository.LoadCharacter(lease,0).Revision == revision,
                        "退役入口拒绝且不安装实例、不提交角色或资产：" + name);
                    await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    That(sent.Count == 1 && sent[0].Kind == 1 && sent[0].Opcode == 16 &&
                        sent[0].Body.AsSpan().SequenceEqual(new byte[] { 0, 9, 0, 0 }),
                        "退役选择沿已证ACK16拒绝包，不发送成功入场或额外状态");
                    continue;
                }
                That(result == CommandResult.Handled && scene is not null && session.Phase == SessionPhase.Loading &&
                    scene.Entry.DungeonId == 310 && scene.Entry.Difficulty == difficulty &&
                    scene.Entry.Maze.MapSpecification!.All(map => map.Kind != "Beast"),
                    "普通310五档保持原拓扑与入场：" + name + " / " + session.LastFormalDungeonRejection);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"PASS {Count - before} 退役入口拒绝与普通五档分派断言（非实机）");
    }
}
