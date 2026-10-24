using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSpecificCardRewardsTests
{
    public static void RunCosmoResources(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        // 显式复现旧活动期限；新版PVF已删除该日期，不修改共享目录或生产期限规则。
        var original = items.Items.Get(490014159);
        var expired = original with { Expiration = ItemExpiration.Parse(new(
            "[expiration date]\n`2020-02-20 06:00:00`\n")) };
        ItemTemplate ResolveDropItem(int id) => id == expired.Id ? expired : items.Items.Get(id);
        var candidates = DungeonItemDropCatalog.Create(items.Items.DropCandidates().Select(
            item => item.Id == expired.Id ? expired : item));
        var cards = new DungeonCardRewardPolicy(Testing.SelfTestCatalogs.DropRules(pvf), candidates,
            items.Items, items.GoldLimits, items.EpicPieces, prices: Testing.SelfTestCatalogs.CardPrices(pvf));
        foreach (int bossRoll in new[] { 0, 45, 90 })
            VerifyCosmoExit(towns, pvf, characters, items, quests, skills, cards, bossRoll, candidates, ResolveDropItem);
        That(ReferenceEquals(items.Items.Get(original.Id), original), "历史期限fixture不污染共享物品目录");
    }

    static void VerifyCosmoExit(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills, DungeonCardRewardPolicy cards, int bossRoll,
        DungeonItemDropCatalog candidates, Func<int, ItemTemplate> resolveDropItem)
    {
        var dungeons = Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        var growth = Testing.SelfTestCatalogs.Growth(pvf, skills);
        var kills = Testing.SelfTestCatalogs.KillExperience(pvf);
        var clear = Testing.SelfTestCatalogs.ClearExperience(pvf);
        var drops = Testing.SelfTestCatalogs.MonsterDrops(pvf);
        var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 7191));
        var done = new HashSet<int>(area.Quests) { 6080, 10112 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        var now = DateTimeOffset.Parse("2026-10-23T00:00:00Z");
        using var fixture = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
            done.ToArray(), position: position, items: bossRoll == 90 ? [(490705255, 1), (3330, 40), (10305821, 4)] :
                bossRoll == 45 ? [(490705255, 2)] : [(3330, 40), (10305821, 4)],
            fatigueRemaining: bossRoll == 0 ? (ushort)8 : (ushort)16,
            timeProvider: new Testing.Inventory.DailyBoosterTestClock(now));
        // 使用生产装配的完整奖励，覆盖三种领主与再挑战后的真实地面对象容量。
        var epicTable = DungeonHellEpicCatalog.Load(pvf);
        var harlem = towns.DungeonMaps.Values.Where(map => map.HellDungeon && map.HellQuests.Contains(6080))
            .SelectMany(map => map.Options).Select(option => checked((uint)option.DungeonId)).ToArray();
        var epics = new HarlemHellEpicDropPolicy(harlem, epicTable.Pool(54, 1, resolveDropItem),
            epicTable.Pool(54, 3, resolveDropItem), epicTable.Pool(54, 4, resolveDropItem),
            epicTable.Pool(54, 2, resolveDropItem), cosmo: DungeonCosmoRewardCatalog.Load(pvf, dungeons, rooms), superPool: epicTable.Pool(54, 5, resolveDropItem));
        var resourceDrops = new DungeonResourceDropPolicy(Testing.SelfTestCatalogs.DropRules(pvf), candidates,
            drops.Get, resolveDropItem, items.DungeonSpecialDrops, new EquipmentAmplificationDropPolicy(items.Amplifications.Rules),
            new HarlemMaterialDropPolicy(new(), harlem, resolveDropItem, epics), epics,
            new HarlemAbyssBonusDropPolicy(new(), epics, resolveDropItem));
        var hell = new DungeonHellRewardPolicy(DungeonHellDropRules.TryLoad(pvf)!, candidates);
        That(resolveDropItem(490014159).Expiration.Resolve(now) == 1582149600,
            "显式历史活动fixture保留原2020年截止，不以商城期限开关改写");
        int historicalCandidates = 0;
        Func<DungeonPlannedMonster, DungeonDropGenerationPlan> PrepareDrops(DungeonEntryPlan entry)
        {
            var prepare = resourceDrops.PrepareEntry(entry, hell);
            return monster =>
            {
                var plan = prepare(monster);
                foreach (var trial in plan.Trials)
                {
                    var pools = trial.Items is { } direct ? new[] { direct } : trial.RarityPools.IsDefaultOrEmpty
                        ? Array.Empty<DungeonItemDropPool>() : trial.RarityPools.SelectMany(row => row.CandidatePools);
                    foreach (var candidate in pools.SelectMany(pool => pool.Entries).Where(row => row.Template.Id == 490014159))
                    {
                        That(candidate.Template.Expiration.Resolve(now) == 1582149600,
                            "实际掉落准备计划包含历史到期候选，而非仅检查未使用的模板");
                        historicalCandidates++;
                    }
                }
                return plan;
            };
        }
        // 终点仍受控选择；其余抽样固定种子，保留历史过期活动候选参与抽样。
        var random = new Random(653 + bossRoll);
        bool forceEpic=false;
        int Draw(int bound)=>forceEpic ? bound==1000000 ? 993000 : bound==1000 ?
            (bossRoll==0 ? 200 : bossRoll==45 ? 0 : 100) : 0 : bound==100 ? bossRoll : random.Next(bound);
        var receipt = fixture.Session.EnterFormalDungeon(7191, 0, 4, dungeons, rooms, drops, items.Items,
            draw: Draw, pieces: items.EpicPieces, hellParty: true,
            prepareDrops: PrepareDrops)!;
        That(receipt is { Entry.IsCosmo: true }, "超星空结算回归沿真实准入：" + fixture.Session.LastFormalDungeonRejection);
        That(receipt.Entry.BossMap.Id == (bossRoll == 0 ? 92689 : bossRoll == 45 ? 92690 : 92697),
            "结算回归分别覆盖三个带权重终点");
        That(bossRoll == 0 || ItemCount(fixture.Read(), 490705255) == (bossRoll == 45 ? 1 : 0) &&
            ItemCount(fixture.Read(), 3330) == (bossRoll == 45 ? 0 : 40) &&
            ItemCount(fixture.Read(), 10305821) == (bossRoll == 45 ? 0 : 4),
            "仅有通行证也能入场，通行证优先且不混扣邀请函及灵石");
        string directory = Path.Combine(Path.GetTempPath(), "S5A9-cosmo-exit-" + Guid.NewGuid().ToString("N"));
        try
        {
            Run().GetAwaiter().GetResult();
            That(historicalCandidates > 0, "三个领主路线各自实际准备历史到期候选，避免空测");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        async Task Run()
        {
            using var log = new ServerLog(directory);
            using var stop = new CancellationTokenSource();
            using var output = new SelectionOutput();
            await using var sender = new GameConnectionSender(fixture.Owner, output, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(178, System.Net.IPAddress.Loopback, output, stop), fixture.Owner, fixture.Session, sender);
            var stats = new CharacterStatComputer(characters);
            var town = new TownProjection(towns, stats, null, items.EpicPieces);
            var commands = new GameCommandRegistry();
            new DungeonSelectionHandler(town, log, dungeons: dungeons).Register(commands);
            new DungeonSettlementHandler(log, dungeons, clear, growth, town, rooms, drops, items.Items,
                new CharacterLevelProjection(stats), pieces: items.EpicPieces, cards: cards).Register(commands);
            commands.Seal();
            await Dispatch(72, [1, 1]);
            That(output.Frames.Count == 0 && fixture.Session.BeginClearedHellSelection(7191) is null,
                "超星空未清图不得从菜单或祭坛退到选图");
            uint groundKey = 0;
            async Task ClearDungeon()
            {
                groundKey = 0;
                var path = QuestStoryRouteTests.Path(receipt.Entry.Maze.StaticTopology, receipt.Entry.StartMap.Position, receipt.Entry.BossMap.Position);
                foreach (var point in path)
                {
                    receipt = fixture.Session.CompleteFormalDungeonLoading()!;
                    foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray())
                    {
                        forceEpic=monster.IsBoss;
                        var death = fixture.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth);
                        forceEpic=false;
                        That(death is { Outcome: DungeonRunOutcome.Accepted, Receipt: not null }, "超星空真实路线逐怪提交");
                        receipt = death.Receipt!;
                        That(death.Drops.All(row => row.Content.Item is not { } item || !item.IsExpired(now.ToUnixTimeSeconds())),
                            "死亡提交只冻结有效资产，历史过期候选不进入地面账本");
                        if (monster.IsBoss)
                        {
                            var expectedPool=bossRoll==0 ? epics.Cosmo! : bossRoll==45 ? epics.Tayberrs! : epics.Super!;
                            var epic=death.Drops.FirstOrDefault(row=>row.Content.Item is { } item &&
                                expectedPool.Entries.Any(candidate=>candidate.Template.Id==item.ItemId));
                            That(epic is not null,"真实终点死亡经默认三分池生成实际地面奖励");
                            var beforePickup=fixture.Read();
                            // 运行态拾取回执已归缓存；在真实库存审计写入处注入晚期SQL失败，保留资产与地面回滚、原对象重试断言。
                            fixture.Sql("CREATE TRIGGER cosmo_epic_pickup_fail AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'cosmo epic pickup rollback'); END");
                            Reject<Microsoft.Data.Sqlite.SqliteException>(()=>fixture.Session.PickFormalDungeonItem(epic!.Reservation.GroundKey,
                                items.Items,items.EpicPieces,items.GoldLimits),"三分池史诗拾取SQL故障不提前移除地面对象");
                            That(QuestCompletionRepositoryTests.Same(beforePickup,fixture.Read()),"失败拾取整批资产保持不变");
                            fixture.Sql("DROP TRIGGER cosmo_epic_pickup_fail");
                            var picked=fixture.Session.PickFormalDungeonItem(epic!.Reservation.GroundKey,items.Items,items.EpicPieces,items.GoldLimits);
                            That(picked is {Outcome:DungeonRunOutcome.Accepted,Commit.Duplicate:false} &&
                                fixture.Read().Inventory.Items.Any(row=>row.Core.ItemId==epic.Content.Item!.ItemId),
                                "哈林、泰波尔斯、超界地面对象重试拾取实际入库");
                            var saved = fixture.Read();
                            var duplicate = fixture.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth);
                            That(duplicate.Outcome == DungeonRunOutcome.Duplicate && duplicate.Drops.IsEmpty &&
                                QuestCompletionRepositoryTests.Same(saved, fixture.Read()), "终点死亡重报不重抽或重复发放奖励");
                        }
                        if (death.Drops.FirstOrDefault(row => row.Content.Item?.ItemId == HarlemMaterialDropPolicy.SkyWishId) is { } wish)
                            groundKey = wish.Reservation.GroundKey;
                        var rows = death.Drops.Select(row => new DungeonDropPacketItem(row.Reservation.GroundKey,
                            row.Reservation.RecordSlot, row.Content.Item, 0, 0) { GoldAmount = row.GoldAmount }).ToArray();
                        _ = ExclusiveDropPackets.MonsterDeath(death.MonsterKey, rows, 7191, death.BossPositionUpdate);
                    }
                    if (point == receipt.Entry.BossMap.Position) break;
                    var next = path[Array.IndexOf(path, point) + 1];
                    That(fixture.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y) is not null, "超星空沿原四房拓扑前进");
                }
                That(fixture.Session.Phase == SessionPhase.Settlement && fixture.Session.FormalDungeonResultFocus() is null,
                    "真实清图尚未发布许可时不开放结果操作");
                await DungeonSettlementProjection.EnableAsync(context);
            }
            await ClearDungeon();
            That(fixture.Session.FormalDungeonResultFocus() is { Entry.IsCosmo: true }, "超星空清图许可后无需翻牌报告即可选择其他地下城");
            var eligibility = output.Frames.Last(frame => frame[0] == 0 && Opcode(frame) == 261);
            That(eligibility.AsSpan(16).SequenceEqual(DungeonSettlementPackets.RetryEligibility(bossRoll != 0)),
                "超星空按剩余票据和疲劳发布单字节再挑战资格");
            // 两个分支覆盖未收到46，以及收到普通46并重复的情况。
            if (bossRoll != 0)
            {
                byte[] body = new byte[53]; body[3] = 1;
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), receipt.Character.Id);
                body[10] = body[11] = 99;
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(49), ClientBodyChecksum.Compute(body.AsSpan(0, 49)));
                if (bossRoll == 45)
                {
                    fixture.Sql("CREATE TRIGGER cosmo_settlement_fail BEFORE INSERT ON character_dungeon_settlement_receipts BEGIN SELECT RAISE(ABORT,'cosmo settlement retry'); END");
                    var expected = fixture.Read();
                    Reject<Microsoft.Data.Sqlite.SqliteException>(() => fixture.Session.SettleFormalDungeon(
                        receipt.Character.Id, 99, 99, body, dungeons, clear, growth, cards), "超星空通关写库故障保留待提交结算");
                    That(QuestCompletionRepositoryTests.Same(expected, fixture.Read()) &&
                        fixture.Session.FormalDungeonResultFocus() is null && fixture.Session.BeginClearedHellSelection(7191) is null,
                        "超星空结算未提交不能以清图许可提前退出，失败事务不改变资产");
                    fixture.Sql("DROP TRIGGER cosmo_settlement_fail");
                }
                await Dispatch(46, body);
                var settled = fixture.Session.SettleFormalDungeon(receipt.Character.Id, 99, 99, body, dungeons, clear, growth, cards);
                That(settled is { Outcome: DungeonRunOutcome.Duplicate, Card: null, Options: null, BlackDiamond: null },
                    "真实46处理器已提交经验，且未生成普通、付费或黑钻牌");
                var before = fixture.Read();
                await Dispatch(46, body);
                That(QuestCompletionRepositoryTests.Same(before, fixture.Read()), "超星空报告重放不重复结算或发奖");
                var current = fixture.Session.FormalDungeonResultFocus()!;
                That(fixture.Session.AdvanceFormalDungeonCards(false) is null && fixture.Session.AdvanceFormalDungeonCards(true) is null &&
                    fixture.Session.SelectFormalDungeonFreeCard(0, 0) is null && fixture.Session.FormalCardState(current) is null,
                    "超星空不开放空牌、免费牌、自动翻牌或付费牌视图");
                int previousFrames = output.Frames.Count;
                foreach (ushort command in new ushort[] { 69, 70 }) await Dispatch(command, []);
                That(output.Frames.Count == previousFrames, "超星空拒绝普通评分滚动及翻牌权限请求");
            }
            That(!output.Frames.Any(frame => frame[0] == 0 && Opcode(frame) is 34 or 35),
                "超星空通关不发布普通结果页34或翻牌奖励35");
            That(groundKey != 0 && fixture.Session.PickFormalDungeonItem(groundKey, items.Items, items.EpicPieces, items.GoldLimits)
                is { Outcome: DungeonRunOutcome.Accepted, Commit.Duplicate: false }, "超星空通关后仍能拾取领主地面奖励");
            if (bossRoll == 45)
                That(fixture.Session.CanRetryCosmoDungeon(fixture.Session.FormalDungeonResultFocus()!),
                    "没有邀请函及灵石时，剩余通行证仍提供再挑战资格");
            if (bossRoll == 90)
            {
                var original = fixture.Session.FormalDungeonResultFocus()!;
                var before = fixture.Read();
                fixture.Sql("CREATE TRIGGER cosmo_retry_fail BEFORE UPDATE ON character_dungeon_state WHEN NEW.last_attempt>OLD.last_attempt BEGIN SELECT RAISE(ABORT,'cosmo retry rollback'); END");
                Reject<Microsoft.Data.Sqlite.SqliteException>(() => fixture.Session.RetryFormalDungeon(original,
                    dungeons, rooms, drops, items.Items, items.EpicPieces), "超星空再挑战入场事务失败");
                That(QuestCompletionRepositoryTests.Same(before, fixture.Read()) && fixture.Session.FormalDungeonResultFocus()?.Snapshot.Identity == original.Snapshot.Identity &&
                    fixture.Session.CanRetryCosmoDungeon(original), "再挑战失败回滚票据、疲劳及尝试号，原通关实例仍可操作");
                fixture.Sql("DROP TRIGGER cosmo_retry_fail");
                output.Frames.Clear();
                await Dispatch(72, [2, 0]);
                await Dispatch(72, [1, 0]);
                var retried = fixture.Session.CurrentFormalDungeonScene()!;
                That(retried is { Entry: { IsCosmo: true, Difficulty: 4, StartFatigue: 8 } } &&
                    retried.Snapshot.Identity != original.Snapshot.Identity && retried.Snapshot.Identity.TaskAttempt == original.Snapshot.Identity.TaskAttempt + 1 &&
                    retried.Monsters.Monsters.Count > 0 && retried.Monsters.Monsters.All(row => row.ObjectKey >= before.DungeonState!.NextObjectKey) &&
                    ItemCount(fixture.Read(), 3330) == 0 && ItemCount(fixture.Read(), 10305821) == 0 &&
                    fixture.Read().Fatigue!.Remaining == before.Fatigue!.Remaining - 8,
                    "超星空再挑战保留模式与指定难度，新尝试独立对象key，再扣40票4灵石8疲劳");
                That(output.Frames.Select(Opcode).SequenceEqual(new ushort[] { 72, 72, 27, 28, 29, 14 }),
                    "真实再挑战焦点和确认后，先结束旧场景再发新图及扣票背包变化");
                var after = fixture.Read();
                int frameCount = output.Frames.Count;
                await Dispatch(72, [1, 0]);
                await Dispatch(72, [1, 3]);
                That(QuestCompletionRepositoryTests.Same(after, fixture.Read()) && output.Frames.Count == frameCount &&
                    fixture.Session.CurrentFormalDungeonScene()?.Snapshot.Identity == retried.Snapshot.Identity,
                    "重放再挑战及旧菜单尾包不再次扣费、不消费新实例");
                receipt = retried;
                await ClearDungeon();
                That(fixture.Session.PickFormalDungeonItem(groundKey, items.Items, items.EpicPieces, items.GoldLimits)
                    is { Outcome: DungeonRunOutcome.Accepted, Commit.Duplicate: false }, "再挑战后的独立地面奖励可拾取");
            }
            if (bossRoll is 0 or 90)
            {
                var original = fixture.Session.FormalDungeonResultFocus()!;
                var before = fixture.Read();
                output.Frames.Clear();
                await Dispatch(72, [1, 0]);
                That(!fixture.Session.CanRetryCosmoDungeon(original) && QuestCompletionRepositoryTests.Same(before, fixture.Read()) &&
                    fixture.Session.FormalDungeonResultFocus()?.Snapshot.Identity == original.Snapshot.Identity &&
                    output.Frames.Select(Opcode).SequenceEqual(new ushort[] { 261 }) && output.Frames[0][16] == 0,
                    "票据及疲劳耗尽时拒绝重开，只同步禁用资格，仍保留原通关选图入口");
            }
            byte[] entry = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, 291100308);
            int count = output.Frames.Count;
            await Dispatch(15, entry);
            That(output.Frames.Count == count && fixture.Session.DungeonSnapshot is not null, "超星空清图后也拒绝其他副本编号");
            output.Frames.Clear();
            var saved = fixture.Read();
            if (bossRoll == 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entry, 7191);
                await Dispatch(15, entry);
                That(output.Frames.Select(Opcode).SequenceEqual(new ushort[] { 15, 3, 27 }), "超星空祭坛15当前编号仍可完整返回选图");
            }
            else
            {
                await Dispatch(72, [2, 1]);
                await Dispatch(72, [1, 1]);
                That(output.Frames.Select(Opcode).SequenceEqual(new ushort[] { 72, 72, 15, 3, 27 }),
                    "实机72焦点与确认直接完整返回选图，不伪造客户端后续15");
            }
            That(fixture.Session.Phase == SessionPhase.MapSelect && fixture.Session.DungeonSnapshot is null &&
                fixture.Read().Inventory.StaminaUntil == saved.Inventory.StaminaUntil,
                "超星空完整返回选图且不施加撤退虚弱");
            var exited = fixture.Read();
            int exitFrames = output.Frames.Count;
            await Dispatch(72, [1, 1]);
            await Dispatch(72, [1, 3]);
            That(output.Frames.Count == exitFrames && QuestCompletionRepositoryTests.Same(exited, fixture.Read()),
                "选图之后迟到菜单确认不重复退场或修改资产");
            That(fixture.Session.BeginClearedHellSelection(7191) is null &&
                fixture.Session.PickFormalDungeonItem(groundKey, items.Items, items.EpicPieces, items.GoldLimits).Outcome == DungeonRunOutcome.Rejected,
                "超星空旧出口及旧地面key不能重复消费");
            Task Dispatch(ushort opcode, byte[] body) => commands.DispatchAsync(context, new Packet(1, opcode, body));
        }
        static ushort Opcode(byte[] frame) => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1));
        static int ItemCount(Character character, int itemId) => character.Inventory.Items.Where(row => row.Core.ItemId == itemId).Sum(row => row.Core.Count);
    }
}
