using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSpecificCardRewardsTests
{
    public static void RunOrdinarySelectionResources(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters,
        ItemResources items, QuestCatalog quests, SkillLearningCatalog skills)
    {
        var dungeons = Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        var growth = Testing.SelfTestCatalogs.Growth(pvf, skills);
        var kills = Testing.SelfTestCatalogs.KillExperience(pvf);
        var clear = Testing.SelfTestCatalogs.ClearExperience(pvf);
        var drops = Testing.SelfTestCatalogs.MonsterDrops(pvf);
        var cards = new DungeonCardRewardPolicy(Testing.SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            items.Items, items.GoldLimits, items.EpicPieces, prices: Testing.SelfTestCatalogs.CardPrices(pvf));
        var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 291100319));
        var done = new HashSet<int>(area.Quests) { 6080 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        foreach (var (activeQuest, tired) in new[] { (false, false), (true, false), (false, true) })
        {
            using var fixture = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
                done.ToArray(), position: position, fatigueRemaining: tired ? (ushort)1 : null);
            if (activeQuest)
            {
                var candidate = quests.All.OrderBy(row => row.Id).First(row => row.Id < 30000 &&
                    QuestEligibility.Evaluate(fixture.Session.Selected!, row, quests).Eligible &&
                    QuestCoverage.HasOrdinaryDeclarations(row));
                That(fixture.Session.ChangeQuest((ushort)candidate.Id, QuestLifecycleAction.Accept, quests,
                    items.Items, items.GoldLimits, items.EpicPieces)?.Commit is not null &&
                    fixture.Session.Selected!.ActiveMainQuests.Length > 0, "普通哈林选图回归沿真实资格接取活动任务");
            }
            var receipt = fixture.Session.EnterFormalDungeon(291100319, 0, 4, dungeons, rooms, drops, items.Items,
                pieces: items.EpicPieces, prepareDrops: _ => _ => new("普通哈林选图回归无合成地面奖励", []))!;
            That(receipt is { Entry: { IsCosmo: false, IsHellLegend: false, HellParty: null, HellPartyMode: 0 } },
                "普通搏击俱乐部不带三个深渊的入场模式");
            That(fixture.Session.OpenFormalDungeonOtherSelection() is null && fixture.Session.BeginClearedHellSelection() is null,
                "普通未清图既不能从结果菜单退出，也不能借用深渊祭坛出口");
            var path = QuestStoryRouteTests.Path(receipt.Entry.Maze.StaticTopology, receipt.Entry.StartMap.Position, receipt.Entry.BossMap.Position);
            foreach (var point in path)
            {
                receipt = fixture.Session.CompleteFormalDungeonLoading()!;
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly)
                    .OrderBy(row => row.IsBoss).ToArray())
                {
                    var death = fixture.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth);
                    That(death is { Outcome: DungeonRunOutcome.Accepted, Receipt: not null },
                        $"普通哈林沿自身路线及成长规则提交死亡: map={receipt.Entry.StartMap.Id}, monster={monster.MonsterId}, key={monster.ObjectKey}, outcome={death.Outcome}");
                    receipt = death.Receipt!;
                }
                if (point == receipt.Entry.BossMap.Position) break;
                var next = path[Array.IndexOf(path, point) + 1];
                That(fixture.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y) is not null, "普通哈林沿实际拓扑到领主房");
            }
            string directory = Path.Combine(Path.GetTempPath(), "S5A9-ordinary-selection-" + Guid.NewGuid().ToString("N"));
            try { Run().GetAwaiter().GetResult(); }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            async Task Run()
            {
                using var log = new ServerLog(directory);
                using var stop = new CancellationTokenSource();
                using var output = new SelectionOutput();
                await using var sender = new GameConnectionSender(fixture.Owner, output, stop, 0, (_, _, _, _, _) => { });
                var context = new GameConnectionContext(new(179, System.Net.IPAddress.Loopback, output, stop), fixture.Owner, fixture.Session, sender);
                var stats = new CharacterStatComputer(characters);
                var commands = new GameCommandRegistry();
                new DungeonSettlementHandler(log, dungeons, clear, growth, new(towns, stats, null, items.EpicPieces),
                    rooms, drops, items.Items, new(stats), pieces: items.EpicPieces, cards: cards).Register(commands);
                commands.Seal();
                await DungeonSettlementProjection.EnableAsync(context);
                var result = fixture.Session.SettleFormalDungeon(receipt.Character.Id, 99, 99,
                    DungeonSettlementProtocolTests.Report(receipt.Character.Id, 99), dungeons, clear, growth, cards);
                That(result is { Outcome: DungeonRunOutcome.Accepted, Card: not null, Options: not null } &&
                    fixture.Session.AdvanceFormalDungeonCards(false) is not null && fixture.Session.AdvanceFormalDungeonCards(true) is not null &&
                    fixture.Session.FormalCardState(result.Receipt!) is not null,
                    "普通哈林保留通关经验及免费付费翻牌，不套用超星空无翻牌机制");
                That(!output.Frames.Any(frame => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 261) &&
                    fixture.Session.BeginClearedHellSelection() is null, "普通哈林清图后也不开放深渊祭坛或超星空资格包");
                output.Frames.Clear();
                var before = fixture.Read();
                if (tired)
                {
                    That(before.Fatigue?.Remaining == 0, "真实普通路线首次扣尽疲劳，仍可完成本次结算");
                    await Dispatch([1, 0]);
                    That(output.Frames.Count >= 3 && output.Frames[0][0] == 1 &&
                        BinaryPrimitives.ReadUInt16LittleEndian(output.Frames[0].AsSpan(1)) == 72 &&
                        output.Frames[0].AsSpan(16).SequenceEqual(new byte[] { 1, 1, 2 }) &&
                        output.Frames[1][0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(output.Frames[1].AsSpan(1)) == 3 &&
                        output.Frames[2][0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(output.Frames[2].AsSpan(1)) == 24 &&
                        fixture.Session.Phase == SessionPhase.Town && fixture.Session.DungeonSnapshot is null,
                        "疲劳耗尽的普通再挑战提交退场后回复回城选项，不等待不存在的重开成功");
                    var after = fixture.Read();
                    That(after.Fatigue == before.Fatigue && after.DungeonState == before.DungeonState &&
                        JsonSerializer.Serialize(after.Inventory) == JsonSerializer.Serialize(before.Inventory) &&
                        after.ActiveMainQuests.SequenceEqual(before.ActiveMainQuests),
                        "重开拒绝及回城不重复扣疲劳、预留尝试、改资产或任务");
                    int returnedFrames = output.Frames.Count;
                    await Dispatch([1, 0]);
                    That(output.Frames.Count == returnedFrames && fixture.Session.DungeonSnapshot is null,
                        "退场后迟到的重复再挑战不能创建新实例或重复发布");
                    return;
                }
                await Dispatch([2, 1]); await Dispatch([1, 1]);
                That(output.Frames.Select(frame => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)))
                    .SequenceEqual(new ushort[] { 72, 72, 15, 3, 27 }) && fixture.Session.Phase == SessionPhase.MapSelect &&
                    fixture.Session.DungeonSnapshot is null && JsonSerializer.Serialize(fixture.Read().Inventory) == JsonSerializer.Serialize(before.Inventory) &&
                    fixture.Read().Fatigue == before.Fatigue && fixture.Read().DungeonState == before.DungeonState &&
                    fixture.Read().ActiveMainQuests.SequenceEqual(before.ActiveMainQuests),
                    "普通哈林有或无活动任务均从72直接完整选图，不新增深渊奖励、门票消耗或剧情连图");
                int frames = output.Frames.Count;
                await Dispatch([1, 1]); await Dispatch([1, 3]);
                That(output.Frames.Count == frames && fixture.Session.Phase == SessionPhase.MapSelect,
                    "普通哈林迟到结算尾包不能退出新选图");
                Task Dispatch(byte[] body) => commands.DispatchAsync(context, new Packet(1, 72, body));
            }
        }
    }
}
