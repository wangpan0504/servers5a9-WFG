using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSpecificCardRewardsTests
{
    public static void RunResources(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        var dungeons = DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var growth = DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var prices = DfoServer.Testing.SelfTestCatalogs.CardPrices(pvf);
        var policy = new DungeonCardRewardPolicy(DfoServer.Testing.SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            items.Items, items.GoldLimits, items.EpicPieces, prices: prices);
        var rows = prices.SpecificRewards[530];
        var withoutTable = new DungeonCardRewardPolicy(DfoServer.Testing.SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            items.Items, items.GoldLimits, items.EpicPieces);
        Reject<NotSupportedException>(() => withoutTable.Prepare(dungeons.Get(530), 3, 1, draw: _ => 0),
            "缺专表不能悄悄发普通随机装备");
        That(rows.Select(row => row.Count).SequenceEqual([50, 55, 60, 65, 70, 75, 100]) &&
            rows.Take(6).All(row => row.ItemId == 10305892) && rows[^1].ItemId == 10309039,
            "实际黎明裂缝翻牌表保留全部七行，不将最后一行相邻物品改写成结晶");
        foreach (int storyId in new[] { 227, 233, 234, 235, 236, 237, 238 })
        {
            var story = dungeons.Get(storyId);
            That(story.IsOrdinaryAnton && story.OrdinaryEntryTicket is null && !prices.SpecificRewards.ContainsKey(storyId),
                "安徒恩故事副本有团本标记，但没有入场券和专表行：" + storyId);
            var card = policy.Prepare(story, 0, 1, draw: _ => 0);
            That(card.Gold >= 1 && card.ResourceSource.StartsWith(DungeonDropRuleCatalog.CommonPath, StringComparison.Ordinal) &&
                card.ResourceSource.Contains(story.Path, StringComparison.Ordinal),
                "安徒恩故事副本按普通翻牌结算，不因缺专表断开：" + storyId);
        }
        int offset = 0;
        foreach (var row in rows)
        {
            foreach (int roll in new[] { offset, offset + row.Weight - 1 })
            {
                var reward = policy.Prepare(dungeons.Get(530), 3, 1, draw: bound => bound == 10000 ? roll : 0);
                That(reward.Item is { } core && core.ItemId == row.ItemId && core.Value == row.Count,
                    "专用翻牌按真实权重边界生成声明物品及数量");
            }
            offset += row.Weight;
        }
        var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 7322));
        var done = new HashSet<int>(area.Quests) { 6030, 6082 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        using var fixture = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
            done.ToArray(), position: position);
        var entry = fixture.Session.EnterFormalDungeon(7322, 0, 0, dungeons, rooms, pieces: items.EpicPieces);
        That(entry is { Entry.StartMap.Id: 311393, Entry.StartFatigue: 8 },
            "最新主线断头谷在任务及疲劳满足时进入真实起点：" + fixture.Session.LastFormalDungeonRejection);
        foreach (uint dungeonIndex in new uint[] { 0, 291100308 })
            VerifyClearedHellExit(towns, pvf, characters, items, quests, skills, dungeonIndex);
    }

    static void VerifyClearedHellExit(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, uint dungeonIndex)
    {
        var dungeons = DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var growth = DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var kills = DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf);
        var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 291100308));
        var done = new HashSet<int>(area.Quests) { 6080 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
        using var fixture = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
            done.ToArray(), position: position, items: [(3330, 30)]);
        var receipt = fixture.Session.EnterFormalDungeon(291100308, 0, 0, dungeons, rooms, pieces: items.EpicPieces, hellParty: true)!;
        That(receipt is not null && !receipt.HellPartyRoomCleared && fixture.Session.BeginClearedHellSelection() is null,
            "入场尚未打完深渊时不开放清房出口");
        That(OpenSelection().Count == 0 && fixture.Session.DungeonSnapshot is not null,
            "真实15处理器不能在尚未清完深渊时结束实例");
        var path = QuestStoryRouteTests.Path(receipt!.Entry.Maze.StaticTopology, receipt.Entry.StartMap.Position, receipt.Entry.HellParty!.Room);
        foreach (var point in path)
        {
            receipt = fixture.Session.CompleteFormalDungeonLoading()!;
            foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray())
            {
                var death = fixture.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth);
                That(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null,
                    "深渊普通怪及波次逐一沿原死亡提交：" + monster.MonsterId);
                receipt = death.Receipt!;
            }
            if (point == receipt.Entry.HellParty!.Room) break;
            var next = path[Array.IndexOf(path, point) + 1];
            That(fixture.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y) is not null, "沿真实拓扑进入深渊房间");
        }
        That(receipt.HellPartyRoomCleared && fixture.Session.Phase == DfoServer.Game.Session.SessionPhase.Dungeon &&
            fixture.Session.EnableFormalDungeonSettlement() is null,
            "深渊房间清完只开放出口，不伪造Boss通关或翻牌");
        var before = fixture.Read();
        foreach (uint otherDungeon in new uint[] { 291100268, uint.MaxValue })
            That(OpenSelection(otherDungeon).Count == 0 && fixture.Session.DungeonSnapshot is not null &&
                fixture.Session.CurrentDungeonSelection is null && fixture.Read().Revision == before.Revision,
                "清完深渊也不接受其他副本编号，不退场、不保存或发布成功");
        var frames = OpenSelection();
        That(frames.Select(frame => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1))).SequenceEqual(new ushort[] { 15, 3, 27 }) &&
            frames[0].AsSpan(16).SequenceEqual(new byte[] { 1 }) && frames[2].Length == 58 &&
            fixture.Session.Phase == DfoServer.Game.Session.SessionPhase.MapSelect && fixture.Session.DungeonSnapshot is null &&
            fixture.Read().Inventory.StaminaUntil == before.Inventory.StaminaUntil,
            "真实15处理器按ACK15、状态3、完整27返回选图，无撤退虚弱；旧实例和地面状态被回收");
        That(fixture.Session.BeginClearedHellSelection() is null, "重复出口不再次退场或发奖");

        List<byte[]> OpenSelection(uint? requestedDungeon = null)
        {
            string directory = Path.Combine(Path.GetTempPath(), "S5A9-hell-selection-" + Guid.NewGuid().ToString("N"));
            try { return Send().GetAwaiter().GetResult(); }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            async Task<List<byte[]>> Send()
            {
                using var log = new ServerLog(directory);
                using var stop = new CancellationTokenSource();
                using var output = new SelectionOutput();
                await using var sender = new DfoServer.Network.GameConnectionSender(fixture.Owner, output, stop, 0, (_, _, _, _, _) => { });
                var context = new DfoServer.Network.Protocol.GameConnectionContext(new(177, System.Net.IPAddress.Loopback, output, stop),
                    fixture.Owner, fixture.Session, sender);
                var commands = new DfoServer.Network.Protocol.GameCommandRegistry();
                var projection = new DfoServer.Network.Projection.TownProjection(towns, new CharacterStatComputer(characters), null, items.EpicPieces);
                new DfoServer.Network.Handlers.DungeonSelectionHandler(projection, log, dungeons: dungeons).Register(commands);
                commands.Seal();
                byte[] body = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body, requestedDungeon ?? dungeonIndex);
                await commands.DispatchAsync(context, new DfoServer.Network.Packet(1, 15, body));
                return output.Frames;
            }
        }
    }

    sealed class SelectionOutput : MemoryStream
    {
        public List<byte[]> Frames { get; } = [];
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Frames.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    public static void RunCore()
    {
        string[] strings = ["[dungeon_index specific reward rate]", "[/dungeon_index specific reward rate]",
            "[dungeon type]", "[/dungeon type]"];
        PvfToken[] tokens = [new(3, 0), new(3, 2), new(0, 72),
            new(0, 530), new(0, 0), new(0, 10305892), new(0, 5000), new(0, 50),
            new(0, 530), new(0, 0), new(0, 10305892), new(0, 5000), new(0, 60), new(3, 3), new(3, 1)];
        var rows = DungeonSpecificCardRewards.Parse(tokens, index => strings[index]);
        That(rows[530].SequenceEqual([new DungeonSpecificCardItem(10305892, 5000, 50), new(10305892, 5000, 60)]),
            "黎明裂缝专表保留同副本多行权重和数量，不被通用装备池覆盖");
        var other = tokens.ToArray(); other[2] = new(0, 53);
        That(DungeonSpecificCardRewards.Parse(other, index => strings[index]).IsEmpty,
            "其它类型不能借黎明裂缝的表形状发放奖励");
        foreach (int type in new[] { 45,71,75,76 })
        {
            var supported = tokens.ToArray(); supported[2] = new(0,type);
            That(DungeonSpecificCardRewards.Parse(supported,index => strings[index])[530].SequenceEqual(rows[530]),
                "安徒恩、泰波尔斯、超时空与代号希望表沿同一严格五整数布局保留全部权重和数量");
            supported[4] = new(0,1);
            Reject<NotSupportedException>(() => DungeonSpecificCardRewards.Parse(supported,index => strings[index]),
                "专用翻牌非零选择字段不作默认奖励");
        }
        That(DfoServer.Network.Builders.DungeonPackets.HellPartyCleared().Length == 0,
            "匹配客户端NOTI789无正文，不夹带猜测字段");
        var notice = DfoServer.Network.Builders.DungeonPackets.SelectionNotice(291100308);
        That(notice.Length == 42 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(notice.AsSpan(9)) == 291100308 &&
            notice.Where((_, at) => at < 9 || at >= 13).All(value => value == 0),
            "星空目标只写空列表后的u32，其余已确认的空列表字段不变");
        var day = new DateTimeOffset(2026, 10, 21, 1, 0, 0, TimeSpan.FromHours(8));
        That(DungeonLegendSelection.DailyIndex(6, 49, day) == DungeonLegendSelection.DailyIndex(6, 49, day.AddHours(20)) &&
            Enumerable.Range(0, 90).Select(offset => DungeonLegendSelection.DailyIndex(6, 49, day.AddDays(offset))).ToHashSet().Count == 6,
            "本地日内稳定且长期覆盖六个目标，不因重开选择界面改变目标");
        foreach (int at in new[] { 4, 5, 6, 7 })
        {
            var invalid = tokens.ToArray();
            invalid[at] = new(0, at == 4 ? 1 : 0);
            Reject<NotSupportedException>(() => DungeonSpecificCardRewards.Parse(invalid, index => strings[index]),
                "拒绝未支持选择字段、非法物品、权重或数量");
        }
    }
}
