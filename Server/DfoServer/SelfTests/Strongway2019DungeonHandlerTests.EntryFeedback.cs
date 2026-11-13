using System.Buffers.Binary;
using System.Net;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class Strongway2019DungeonHandlerTests
{
    public static async Task RunEntryFeedbackResourcesAsync(PvfArchive archive, TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(archive);
        var growth = CharacterGrowthCatalog.Load(archive);
        var dungeons = DungeonCatalog.Load(archive);
        var rooms = DungeonRoomCatalog.Load(archive);
        var drops = DungeonMonsterDropCatalog.Load(archive);
        var kills = DungeonKillExperienceCatalog.Load(archive);
        var stats = new CharacterStatComputer(CharacterCatalog.Load(archive, resources.Items));
        foreach (string scenario in new[] { "low0", "low3999", "party-low3999", "solo", "party", "changed", "closed", "matching", "epoch", "owner" })
        {
            int antiEvil = scenario == "low0" ? 0 : scenario is "low3999" or "party-low3999" ? 3999 : 4000;
            using var f = new Strongway2019MatchingTests.Fixture(towns, activity, growth);
            var session = f.Team(count: 1, antiEvil: _ => antiEvil)[0];
            if (!scenario.StartsWith("party", StringComparison.Ordinal)) f.Parties.Detach(session);
            using var log = new ServerLog(Path.Combine(Path.GetTempPath(), "strongway-entry-feedback-" + Guid.NewGuid().ToString("N")));
            var town = new TownProjection(towns, stats, null, resources.EpicPieces);
            var partyDungeons = new DfoServer.Game.Dungeons.PartyDungeonService(f.Parties, f.Repository);
            var handler = new DungeonHandler(log, dungeons, rooms, growth, kills, town, new CharacterLevelProjection(stats),
                drops, resources.Items, resources.EpicPieces, partyDungeons: partyDungeons, partyStats: stats,
                parties: f.Parties, strongwayRoute: new(activity, dungeons, rooms));
            var commands = new GameCommandRegistry();
            new DungeonSelectionHandler(town, log, dungeons: dungeons, partyDungeons: partyDungeons).Register(commands);
            handler.Register(commands); commands.Seal();
            using var stop = new CancellationTokenSource();
            using var output = new MemoryStream();
            var frames = new List<(byte Kind, ushort Opcode, byte[] Body)>();
            Action? afterRejection = null;
            await using var sender = new GameConnectionSender(session.CaptureSocialActor()!.Owner, output, stop, 0,
                (kind, opcode, _, body, _) =>
                {
                    frames.Add((kind, opcode, body.ToArray()));
                    if (kind == 1 && opcode == 16 && body.Span[0] == 0)
                    {
                        var pending = afterRejection; afterRejection = null; pending?.Invoke();
                    }
                });
            var context = new GameConnectionContext(new(5363, IPAddress.Loopback, output, stop),
                session.CaptureSocialActor()!.Owner, session, sender);
            handler.PreparePartyContext(context);

            await Open(5363);
            if (scenario is "solo" or "party")
            {
                await Select(5363);
                await sender.DrainAsync();
                That(session.Phase == SessionPhase.Loading && session.CurrentFormalDungeonScene() is { Entry.DungeonId: 5363 } &&
                    frames.Any(row => row.Kind == 0 && row.Opcode == 28 && BinaryPrimitives.ReadUInt32LittleEndian(row.Body) == 5363) &&
                    frames.Any(row => row.Kind == 0 && row.Opcode == 29) && !frames.Any(row => row.Kind == 1 && row.Opcode == 16),
                    "4000抗魔英雄经真实15/27后16发布28/29，不要求额外队员：" + scenario);
                continue;
            }
            if (scenario == "changed") antiEvil = 3999;
            if (scenario == "closed") f.SetStrongway2019Active(false);
            if (scenario is "epoch" or "owner")
            {
                antiEvil = 3999;
                afterRejection = () =>
                {
                    if (scenario == "owner") { f.Takeover(session); return; }
                    session.ReturnToRoster();
                    session.Select(0, resumeTown: true); session.MarkReady();
                    That(session.BeginDungeonSelection() is not null, "旧ACK16写出后重选并建立新选图身份");
                };
            }
            frames.Clear();
            try { await Select(scenario == "matching" ? 5360u : 5363u); }
            catch (InvalidOperationException error) when (scenario == "owner" && error.Message == "Stale account generation")
            {
                // 分派器末尾的旧租约检查仍中断旧连接，不要求放宽账号所有权检查。
            }
            await sender.DrainAsync();
            That(frames.Any(row => row.Kind == 1 && row.Opcode == 16 && row.Body[0] == 0) &&
                !frames.Any(row => row.Kind == 0 && row.Opcode is 28 or 29) &&
                (scenario == "owner" || session.CurrentFormalDungeonScene() is null),
                "不满足准入或直接伪造匹配时只拒绝，不创建Run或发布加载：" + scenario);
            if (scenario is "epoch" or "owner")
            {
                That(session.Phase == SessionPhase.MapSelect && session.CurrentDungeonSelection is not null &&
                    !frames.Any(row => row.Opcode is 132 or 23 or 24 or (ushort)NotiPacketType.AAS_TIPS),
                    "ACK16后旧owner或旧选角回执不能退出新选图或发布旧提示：" + scenario);
                continue;
            }
            if (scenario == "matching")
            {
                That(session.Phase == SessionPhase.MapSelect && !frames.Any(row => row.Opcode == 132),
                    "匹配5360不能借普通英雄失败恢复逻辑取得入场或误返城");
                continue;
            }
            int rejected = frames.FindIndex(row => row.Kind == 1 && row.Opcode == 16);
            int returned = frames.FindIndex(row => row.Kind == 1 && row.Opcode == 132 && row.Body is [1]);
            int area = frames.FindIndex(row => row.Kind == 0 && row.Opcode == 24);
            int notice = frames.FindIndex(row => row.Kind == 0 && row.Opcode == (ushort)NotiPacketType.AAS_TIPS);
            That(session.Phase == SessionPhase.Town && session.CurrentDungeonSelection is null &&
                returned > rejected && area > returned && notice > area &&
                session.Selected!.Position is { Town: 65, Area: 1 },
                "英雄失败先结束等待，再恢复活动准备区，最后显示提示而非留空选图：" + scenario);
            string text = Encoding.UTF8.GetString(frames[notice].Body);
            That(scenario == "closed" ? text.Contains("活动", StringComparison.Ordinal) :
                text.Contains("4000", StringComparison.Ordinal) && text.Contains(antiEvil.ToString(), StringComparison.Ordinal),
                "可见提示保留活动原因或英雄4000门槛及实际服务端抗魔：" + scenario);
            if (scenario.StartsWith("low", StringComparison.Ordinal))
            {
                antiEvil = 4000;
                frames.Clear();
                await Open(5359); await Select(5359);
                await sender.DrainAsync();
                That(session.Phase == SessionPhase.Loading && session.CurrentFormalDungeonScene() is { Entry.DungeonId: 5359 } &&
                    frames.Any(row => row.Kind == 0 && row.Opcode == 28 && BinaryPrimitives.ReadUInt32LittleEndian(row.Body) == 5359) &&
                    frames.Any(row => row.Kind == 0 && row.Opcode == 29),
                    "英雄抗魔不足恢复后，将角色抗魔补至4000，同一连接仍能进入简单模式：" + scenario);
            }

            async Task Open(uint dungeon)
            {
                frames.Clear();
                var body = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(body, dungeon);
                That(await commands.DispatchAsync(context, new Packet(1, 15, body)) == CommandResult.Handled,
                    "真实CMD15处理首期指定副本：" + dungeon);
                await sender.DrainAsync();
                int ack = frames.FindIndex(row => row.Kind == 1 && row.Opcode == 15 && row.Body is [1]);
                int state = frames.FindIndex(row => row.Kind == 0 && row.Opcode == 3 && row.Body.Length == 4 && row.Body[3] == 1);
                int scene = frames.FindIndex(row => row.Kind == 0 && row.Opcode == 27 && row.Body.Length == 42);
                That(session.Phase == SessionPhase.MapSelect && ack >= 0 && state > ack && scene > state,
                    "指定15沿原顺序ACK/state1/27切场景，资格由自然随后16复验：" + scenario);
            }
            Task<CommandResult> Select(uint dungeon)
            {
                var body = Convert.FromHexString("000000000200000000FFFF00000000000000000000FFFFFFFF");
                BinaryPrimitives.WriteUInt32LittleEndian(body, dungeon);
                return commands.DispatchAsync(context, new Packet(1, 16, body));
            }
        }
        Console.WriteLine($"PASS {Count - before} 首期真实15/16英雄准入反馈、失败返城、简单重试与旧身份隔离");
    }
}
