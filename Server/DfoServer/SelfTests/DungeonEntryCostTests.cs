using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using PvfLib;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// #510/#511：选图准入的具体拒绝原因、普通入场票（[required item]「物品 1 1」）与入场一次性疲劳（[use fatigue only start dungeon] N）。
// 扣票、扣疲劳与尝试号预留同一事务；明确不足原因由请求处理器映射到匹配客户端的拒绝提示。
internal static class DungeonEntryCostTests
{
    const int Tayberrs = 291100320, TayberrsSecond = 291100322, RefinedStone = 10158964;

    internal static void RunCore()
    {
        int checks = Count;
        System.Collections.Immutable.ImmutableArray<CosmoPassCost> Parse(params int[] values) => CosmoPassCost.Parse(
            [new(3,1),.. values.Select(v=>new PvfToken(0,v)),new(3,2)],
            id=>id==1?"[hell cosmo freepass item]":"[/hell cosmo freepass item]");
        That(Parse(100,1,-1,0,200,1,-1,2).SequenceEqual([
                new CosmoPassCost(100,1,0),new(200,1,2),
                new(CosmoPassCost.DailyAttendanceStarPass,1,2),new(CosmoPassCost.DailyAttendanceCosmoPass,1,0)]) &&
            Parse(CosmoPassCost.DailyAttendanceStarPass,1,-1,2,CosmoPassCost.DailyAttendanceCosmoPass,1,-1,0).SequenceEqual([
                new CosmoPassCost(CosmoPassCost.DailyAttendanceStarPass,1,2),new(CosmoPassCost.DailyAttendanceCosmoPass,1,0)]),
            "超星空通行证四元组保留券数量和额外灵石，补充日签到星空/超星空通行证且不重复添加");
        foreach(var malformed in new[] {new[]{100,1,-1},new[]{100,0,-1,0},new[]{100,1,0,0},
            new[]{100,1,-1,-1},new[]{100,1,-1,0,100,1,-1,2}})
            Reject<InvalidDataException>(()=>Parse(malformed),"通行证残缺、零数量、未确认附加条件及重复条目拒绝");
        PartyEntryTransaction();
        DaybreakPass();
        DazzlementTowerAdmissionTests.RunCore();
        Console.WriteLine($"PASS {Count-checks} 副本入场费用声明与抵料边界断言");
    }

    static void DaybreakPass()
    {
        var now = DateTimeOffset.Parse("2026-11-09T00:00:00Z");
        var pieces = new EpicPieceCatalog("[piece list]\n1 2\n[/piece list]");
        var required = new DungeonRequiredItem(10161610, 20, 1);
        var pass = new InventoryItem(71, new(InventoryListType.Main, 121), new(490011739, ItemKind.Material, 2));
        var buds = new InventoryItem(72, new(InventoryListType.Main, 122), new(10161610, ItemKind.Material, 20));
        var mixed = new InventorySnapshot([pass, buds]);
        var selected = DaybreakEntryCost.Select(530, required, mixed, pieces, now)!;
        var payment = InventoryMaterialConsumptionService.Plan(mixed, [new(selected.ItemId, selected.Count)], pieces, now);
        That(selected == new DungeonRequiredItem(490011739, 1, 1) && payment.Plan is { } plan &&
            plan.After.Items.SequenceEqual([pass with { Core = pass.Core with { Value = 1 } }, buds]) &&
            plan.ChangedSlots.SequenceEqual([pass.Position]) && mixed.Items.SequenceEqual([pass, buds]),
            "黎明券优先于材料，规划只扣一张原UID券，保留芽及原始快照");
        That(DaybreakEntryCost.Select(530, required, new([pass]), pieces, now) == selected,
            "只有有效通行证时仍能选择抵料费用");
        That(!InventoryMaterialConsumptionService.Plan(new([buds]), [new(selected.ItemId, selected.Count)], pieces, now).Accepted &&
            !InventoryMaterialConsumptionService.Plan(new([pass with { Core = pass.Core with { ExpireTime = (int)now.ToUnixTimeSeconds() } }, buds]),
                [new(selected.ItemId, selected.Count)], pieces, now).Accepted,
            "已冻结的券费用在提交时缺券或到期则拒绝，不悄悄改扣普通材料");
        foreach (var unavailable in new[]
        {
            pass with { Core = pass.Core with { EquipmentLockId = 1 } },
            pass with { Core = pass.Core with { ExpireTime = (int)now.ToUnixTimeSeconds() } },
            pass with { Position = new(InventoryListType.PersonalCargo, 0) },
        })
            That(DaybreakEntryCost.Select(530, required, new([unavailable, buds]), pieces, now) == required,
                "锁定、恰好到期或仓库券不抵料，仍回退普通材料");
        That(DaybreakEntryCost.Select(530, required, new([buds]), pieces, now) == required &&
            DaybreakEntryCost.Select(531, required, mixed, pieces, now) == required &&
            DaybreakEntryCost.Select(530, required with { Flag = 0 }, mixed, pieces, now)?.Flag == 0 &&
            DaybreakEntryCost.Select(530, required with { Count = 21 }, mixed, pieces, now)?.Count == 21 &&
            DaybreakEntryCost.Select(530, required, mixed, null, now) == required,
            "无券、其它副本、持有型或变更资源合同及缺目录时不猜测抵料");
        var definition = new DungeonDefinition(530, "fixture", null, null, [],
            [new("required item", [new(0, 10161610), new(0, 20), new(0, 1)])]);
        That(ChannelTownDungeonRequirement.Check(definition, new([pass]), pieces, now) is null &&
            ChannelTownDungeonRequirement.Check(definition, InventorySnapshot.Empty, pieces, now) is not null,
            "频道城镇持料门禁复用同一抵料规则，无券无芽仍拒绝");
    }

    static void PartyEntryTransaction()
    {
        string root = Path.Combine(Path.GetTempPath(), "ServerS5A9-party-entry-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new DfoServer.Persistence.CharacterRepository(path);
            var auth = new DfoServer.Game.Auth.AuthenticationService(new DfoServer.Persistence.AuthenticationRepository(path));
            auth.Create("entry-a", "fixture-password"); auth.Create("entry-b", "fixture-password");
            var owners = new DfoServer.Game.Accounts.AccountOwners();
            using var a = owners.Acquire("entry-a", () => { });
            using var b = owners.Acquire("entry-b", () => { });
            Character Create(DfoServer.Game.Accounts.AccountOwners.Lease owner, string name) => repository.Create(owner,
                new NewCharacter(name, new(38, 1, 450, 234), null)
                { DungeonState = CharacterDungeonState.NewOrdinary, Inventory = InventorySnapshot.Empty with { Gold = 100 } });
            var first = Create(a, "EntryA"); var second = Create(b, "EntryB");
            var period = new DungeonIncountPeriod(20261025, 20261022);
            var row = new DungeonIncountRow(0, [3], 1, false, 2);
            var instance = Guid.NewGuid();
            PartyDungeonReservation Reserve(DfoServer.Game.Accounts.AccountOwners.Lease owner, Character character) =>
                new(owner, character, 20, new(new(character.Inventory, character.Inventory with { Gold = 90 }, "party_entry_test"),
                    8, new(character.Id, 0, character.DungeonState!.LastAttempt + 1, instance, 1), 1),
                    new(3, period, row, [new(3, period.Day, 1, true)], true));
            var reservations = new[] { Reserve(a, first), Reserve(b, second) };
            void Sql(string sql)
            {
                using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path);
                db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            Sql($"CREATE TRIGGER fail_party_entry BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id={second.Id} BEGIN SELECT RAISE(ABORT,'party-entry-failure'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => repository.CommitPartyDungeonEntry(reservations), "后续成员提交失败");
            That(QuestCompletionRepositoryTests.Same(first, repository.LoadCharacter(a, 0)!) &&
                QuestCompletionRepositoryTests.Same(second, repository.LoadCharacter(b, 0)!) &&
                repository.LoadDungeonIncount(a, first.Id, 3, period) == default &&
                repository.LoadAccountDungeonIncount(a, 3, period.Day) == 0,
                "全员费用、疲劳、角色/账号次数、revision与尝试号同事务回滚");
            Sql("DROP TRIGGER fail_party_entry");
            var committed = repository.CommitPartyDungeonEntry(reservations);
            That(committed.All(r => r.Character.Inventory.Gold == 90 && r.Character.Fatigue?.Remaining == 148 &&
                r.Character.DungeonState?.LastAttempt == 1 && r.EnterCounts.Single().Daily == 0) &&
                repository.LoadAccountDungeonIncount(a, 3, period.Day) == 1 && repository.LoadAccountDungeonIncount(b, 3, period.Day) == 1,
                "失败后重试只扣一次，分别返回各成员的实际余额、次数与尝试号");
            var batch = repository.LoadDungeonIncounts(a, first.Id);
            That(batch is [{ Dungeon: 3, Period: var savedPeriod, Usage: { DailyUsed: 1, WeeklyUsed: 1 } }] && savedPeriod == period &&
                repository.LoadDungeonIncounts(b, first.Id).IsEmpty &&
                DungeonIncountPolicy.Current(batch[0].Usage, batch[0].Period, period with { Day = period.Day + 1 }) ==
                    repository.LoadDungeonIncount(a, first.Id, 3, period with { Day = period.Day + 1 }),
                "批量只读按角色和账号隔离，跨日结果与原单项查询一致");
            var exhausted = new[] { Reserve(a, committed[0].Character), Reserve(b, committed[1].Character) };
            Reject<InvalidOperationException>(() => repository.CommitPartyDungeonEntry(exhausted), "次数耗尽时整队拒绝");
            That(repository.LoadCharacter(a, 0)!.Revision == committed[0].Character.Revision &&
                repository.LoadCharacter(b, 0)!.Revision == committed[1].Character.Revision,
                "次数拒绝不再扣任何成员费用或推进尝试号");
            var exits = committed.Select((r, i) => new PartyDungeonExitReservation(i == 0 ? a : b,
                r.Character, r.Character.Position with { X = 500 }, null)).ToArray();
            Sql($"CREATE TRIGGER fail_party_exit BEFORE UPDATE ON characters WHEN NEW.id={second.Id} BEGIN SELECT RAISE(ABORT,'party-exit-failure'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => repository.CommitPartyDungeonExit(exits), "后续成员退场保存失败");
            That(QuestCompletionRepositoryTests.Same(committed[0].Character, repository.LoadCharacter(a, 0)!) &&
                QuestCompletionRepositoryTests.Same(committed[1].Character, repository.LoadCharacter(b, 0)!),
                "全员退场位置、资产与revision一起回滚，不能留下半队回城");
            Sql("DROP TRIGGER fail_party_exit");
            var returned = repository.CommitPartyDungeonExit(exits);
            That(returned.All(r => r.Position.X == 500) && returned.Select((r, i) =>
                r.Revision == committed[i].Character.Revision + 1).All(value => value),
                "重试退场全员保存一次");
            var unchanged = returned.Select((r, i) => new PartyDungeonExitReservation(i == 0 ? a : b, r, r.Position, null)).ToArray();
            Sql("CREATE TRIGGER reject_unchanged_exit BEFORE UPDATE ON characters BEGIN SELECT RAISE(ABORT,'no-op-exit-write'); END");
            That(repository.CommitPartyDungeonExit(unchanged).SequenceEqual(returned), "无持久变化的全员退场只核对身份，不执行写入");
            Reject<InvalidOperationException>(() => repository.CommitPartyDungeonExit(exits), "旧revision不能重复退场");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, true);
        }
    }

    internal static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        That(dungeons.Get(530).RequiredItem == new DungeonRequiredItem(10161610, 20, 1) &&
            items.Items.Get(490011739).Kind == ItemKind.Material,
            "黎明真实资源仍为20诞生之芽消耗型费用，疲劳商店券保持材料类型");
        DaybreakEntryTransaction(towns, characters, skills, growth, quests, dungeons, rooms, items, DungeonIncountCatalog.Load(pvf));
        ImmortalCorridor(towns, characters, skills, growth, quests, dungeons, rooms);

        var passIds = items.Items.CosmoPasses();
        That(passIds.Length == 50 && passIds.Count(row=>row.AdditionalStones==0)==37 && passIds.Count(row=>row.AdditionalStones==2)==13 &&
            passIds.Contains(new(CosmoPassCost.DailyAttendanceStarPass, 1, 2)) &&
            passIds.Contains(new(CosmoPassCost.DailyAttendanceCosmoPass, 1, 0)) &&
            passIds.All(row=>row.Count==1) && !passIds.Any(row=>row.ItemId is 490703708 or 490703706),
            "按freepassitem与签到活动声明匹配50种：37种完整券和13种需额外2石的星空券，不按名称扩大旧券或礼盒");
        var passNow = DateTimeOffset.Parse("2026-10-23T00:00:00Z");
        InventoryItem Pass(long uid, short slot, int id = 490705255) =>
            new(uid, new(InventoryListType.Main, slot), new(id, ItemKind.Material, 1));
        var lockedPass = Pass(1, 121) with { Core = Pass(1, 121).Core with { EquipmentLockId = 1 } };
        var expiredPass = Pass(2, 122) with { Core = Pass(2, 122).Core with { ExpireTime = (int)passNow.ToUnixTimeSeconds() } };
        var cargoPass = Pass(3, 123) with { Position = new(InventoryListType.PersonalCargo, 0) };
        InventorySnapshot unavailablePasses = new([lockedPass, expiredPass, cargoPass]);
        That(HellPartyEntryCost.CosmoPass(unavailablePasses, passIds, items.EpicPieces, passNow).Plan is null,
            "锁定、恰好到期及仓库通行证不能抵扣入场");
        var validPasses = unavailablePasses with { Items = unavailablePasses.Items.Add(Pass(4, 125)).Add(Pass(5, 124, 490013690)) };
        That(HellPartyEntryCost.CosmoPass(validPasses, passIds, items.EpicPieces, passNow).Ticket is { ItemId: 490013690, Count: 1 },
            "通行证按有效背包槽位顺序选择，一次只消耗一张");

        var starOnly = new InventorySnapshot([Pass(1,121,490703917)]);
        That(HellPartyEntryCost.CosmoPass(starOnly,passIds,items.EpicPieces,passNow).Plan is null,
            "星空券用于超星空必须有额外2灵石，不能按同为通行证免费放行");
        var starPaid = starOnly with {Items=starOnly.Items.Add(new(2,new(InventoryListType.Main,122),new(10305821,ItemKind.Material,2)))};
        That(HellPartyEntryCost.CosmoPass(starPaid,passIds,items.EpicPieces,passNow) is
            {Ticket:{ItemId:490703917,Count:1},Stone:{ItemId:10305821,Count:2},Plan.After.Items.Length:0},
            "资源声明的星空券与额外2灵石作为完整组合规划");
        var attendanceStarMixed = new InventorySnapshot([
            Pass(1, 152, CosmoPassCost.DailyAttendanceStarPass),
            new(2, new(InventoryListType.Main, 154), new(HellPartyEntryCost.DailyAttendanceLegendStone, ItemKind.Material, 1)),
            new(3, new(InventoryListType.Main, 155), new(HellPartyEntryCost.LegendStone, ItemKind.Material, 1)),
        ]);
        That(HellPartyEntryCost.CosmoPass(attendanceStarMixed, passIds, items.EpicPieces, passNow).Plan is null,
            "签到星空裂缝通行证的2枚附加灵石不跨不同物品ID混扣");
        var attendanceStarPaid = new InventorySnapshot([
            Pass(1, 152, CosmoPassCost.DailyAttendanceStarPass),
            new(2, new(InventoryListType.Main, 154), new(HellPartyEntryCost.DailyAttendanceLegendStone, ItemKind.Material, 2)),
            new(3, new(InventoryListType.Main, 155), new(HellPartyEntryCost.LegendStone, ItemKind.Material, 2)),
        ]);
        That(HellPartyEntryCost.CosmoPass(attendanceStarPaid, passIds, items.EpicPieces, passNow) is
            { Ticket: { ItemId: CosmoPassCost.DailyAttendanceStarPass, Count: 1 }, Stone: { ItemId: HellPartyEntryCost.DailyAttendanceLegendStone, Count: 2 }, Plan.After.Items: [{ Core: { ItemId: HellPartyEntryCost.LegendStone, Count: 2 } }] },
            "签到星空裂缝通行证优先搭配2枚[活动]星空深渊灵石规划入场");
        var attendanceCosmoPaid = new InventorySnapshot([Pass(1, 152, CosmoPassCost.DailyAttendanceCosmoPass)]);
        That(HellPartyEntryCost.CosmoPass(attendanceCosmoPaid, passIds, items.EpicPieces, passNow) is
            { Ticket: { ItemId: CosmoPassCost.DailyAttendanceCosmoPass, Count: 1 }, Stone: null, Plan.After.Items.Length: 0 },
            "签到超·星空裂缝通行证单张免灵石规划入场");
        var fullFallback=starOnly with {Items=starOnly.Items.Add(Pass(3,122,490705255))};
        That(HellPartyEntryCost.CosmoPass(fullFallback,passIds,items.EpicPieces,passNow).Ticket is {ItemId:490705255},
            "前方星空券缺灵石时仍可使用后方完整超星空券");
        That(HellPartyEntryCost.CosmoPass(new([Pass(1,121,10161613)]),passIds,items.EpicPieces,passNow).Ticket is null,
            "普通深渊券不混入超星空资源范围");

        // 资源形状：只有唯一一行「物品 1 1」才是入场票；入场疲劳只认唯一一个正整数。
        var tayberrs = dungeons.Get(Tayberrs);
        That(tayberrs.OrdinaryEntryTicket == new DungeonRequiredItemRow(RefinedStone, 1, 1) && tayberrs.StartFatigue == 10 &&
            dungeons.Get(TayberrsSecond).OrdinaryEntryTicket == new DungeonRequiredItemRow(RefinedStone, 1, 1) && dungeons.Get(TayberrsSecond).StartFatigue == 10,
            "泰波尔斯 291100320/291100322：[required item] 10158964 1 1 是入场票，[use fatigue only start dungeon] 10");
        var guide = dungeons.Get(5349);
        That(guide.DeclaresRequiredItem && guide.OrdinaryEntryTicket is null && guide.StartFatigue == 10,
            "5349 的 [required item] 不是「物品 1 1」：不当作入场票，入场疲劳 10 照常解析");
        That(dungeons.Get(7301).DeclaresRequiredItem && dungeons.Get(7301).OrdinaryEntryTicket is null,
            "强袭 7301 的 [required item]（数量 10）不是入场票，仍走强袭自己的消耗");
        That(dungeons.Get(3600).OrdinaryEntryTicket?.ItemId == 10004322 && dungeons.Get(7100).OrdinaryEntryTicket?.ItemId == 3332 &&
            dungeons.Get(11008).OrdinaryEntryTicket?.ItemId == 4473,
            "受影响的其他「物品 1 1」副本按资源解析入场票（3600/7100/11008）");
        var cosmo = dungeons.Get(7191);
        That(cosmo.IsCosmoDungeon && cosmo.CosmoInvitationCount == 40 && cosmo.CosmoStoneCount == 4 && cosmo.CosmoQuestId == 10112 &&
            cosmo.StartFatigue == 8 && cosmo.TryGetDesignatedDifficulty(out byte cosmoDifficulty) && cosmoDifficulty == 4,
            "超星空准入从7191 PVF严格解析40邀请函、4灵石、10112、固定难度4与8疲劳");
        var twoSections = tayberrs with { RawSections = [.. tayberrs.RawSections,
            new DungeonRawSection("use fatigue only start dungeon", [new PvfToken(0, 3)])] };
        var twoItems = tayberrs with { RawSections = [.. tayberrs.RawSections.Where(row => row.Tag != "required item"),
            new DungeonRawSection("required item", [new PvfToken(0, RefinedStone), new PvfToken(0, 2), new PvfToken(0, 1)])] };
        That(twoSections.DeclaresStartFatigue && twoSections.StartFatigue is null && twoItems.DeclaresRequiredItem && twoItems.OrdinaryEntryTicket is null,
            "重复的入场疲劳段或数量不是 1 的 [required item] 不被猜测成入场费用");

        // #510 代号希望 8523：未完成 6170 时给出 selection-quest-required:6170。
        GateRejected(towns, characters, skills, growth, quests, dungeons, rooms, 8523, 6170,
            OperationHopeCatalog.Load(pvf,dungeons,quests,rooms));
        // #511 泰波尔斯：未完成 6116 时两个副本都给出 selection-quest-required:6116。
        GateRejected(towns, characters, skills, growth, quests, dungeons, rooms, Tayberrs, 6116);
        GateRejected(towns, characters, skills, growth, quests, dungeons, rooms, TayberrsSecond, 6116);

        var (cosmoDone, cosmoPosition) = Selection(towns, 7191);
        cosmoDone.UnionWith(towns.DungeonMaps[AreaDungeonWorldMap(towns)].HellQuests);
        using (var cosmoLocked = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,95,
            [.. cosmoDone.Where(id => id != cosmo.CosmoQuestId).Order()],position:cosmoPosition,
            items:[(3330,40),(10305821,4),(490705255,1)],fatigueRemaining:100))
        {
            var before = cosmoLocked.Read();
            That(cosmoLocked.Session.EnterFormalDungeon(7191,0,4,dungeons,rooms,items:items.Items,pieces:items.EpicPieces,hellParty:true) is null &&
                cosmoLocked.Session.LastFormalDungeonRejection is "hell-party-quest-required:10112" or "selection-quest-required:10112" &&
                QuestCompletionRepositoryTests.Same(before, cosmoLocked.Read()),
                "材料与通行证齐全也不能绕过超星空任务，缺少10112时保持全部资产与入场状态：" + cosmoLocked.Session.LastFormalDungeonRejection);
        }
        cosmoDone.Add(cosmo.CosmoQuestId);
        using (var cosmoPoor = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,95,[.. cosmoDone.Order()],position:cosmoPosition,
            items:[(3330,40)],fatigueRemaining:100))
        {
            var before = cosmoPoor.Read();
            That(cosmoPoor.Session.EnterFormalDungeon(7191,0,4,dungeons,rooms,pieces:items.EpicPieces,hellParty:true) is null &&
                cosmoPoor.Session.LastFormalDungeonRejection == "hell-party-item-required" && cosmoPoor.Read().Revision == before.Revision &&
                cosmoPoor.Read().Fatigue == before.Fatigue && cosmoPoor.Read().DungeonState == before.DungeonState &&
                ItemCount(cosmoPoor.Read(),3330) == 40,
                "超星空缺4个探测石时拒绝，先规划的40张邀请函与疲劳均不提交");
        }
        using (var cosmoTired = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,95,[.. cosmoDone.Order()],position:cosmoPosition,
            items:[(3330,40),(10305821,4),(490705255,1)],fatigueRemaining:7))
        {
            var before = cosmoTired.Read();
            That(cosmoTired.Session.EnterFormalDungeon(7191,0,4,dungeons,rooms,items:items.Items,pieces:items.EpicPieces,hellParty:true) is null &&
                cosmoTired.Session.LastFormalDungeonRejection == "start-fatigue-insufficient:8" && cosmoTired.Read().Revision == before.Revision &&
                cosmoTired.Read().Fatigue == before.Fatigue && cosmoTired.Read().DungeonState == before.DungeonState &&
                ItemCount(cosmoTired.Read(),3330) == 40 && ItemCount(cosmoTired.Read(),10305821) == 4 && ItemCount(cosmoTired.Read(),490705255) == 1,
                "超星空有通行证但疲劳不足8点时拒绝，材料与副本尝试状态不变");
        }
        using (var cosmoRich = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,95,[.. cosmoDone.Order()],position:cosmoPosition,
            items:[(3330,40),(10305821,4)],fatigueRemaining:100))
        {
            var before = cosmoRich.Read();
            That(cosmoRich.Session.EnterFormalDungeon(7191,0,4,dungeons,rooms,pieces:items.EpicPieces,hellParty:true,sealDoor:true) is null &&
                cosmoRich.Session.LastFormalDungeonRejection == "hell-party-mode-unsupported" &&
                QuestCompletionRepositoryTests.Same(before, cosmoRich.Read()),
                "超星空不能借免费密门模式进入，拒绝后资产与实例状态不变");
            var receipt = cosmoRich.Session.EnterFormalDungeon(7191,0,4,dungeons,rooms,pieces:items.EpicPieces,hellParty:true);
            var after = cosmoRich.Read();
            That(receipt is { Entry: { IsCosmo: true, Difficulty: 4, StartFatigue: 8, Maze.StaticTopology.Supported: true },
                    HellTicket: { ItemId: 3330,Count: 40 },LegendTicket: { ItemId: 10305821,Count: 4 },EntryFatigueCharged: 8 } &&
                receipt.Entry.Maze.MapSpecification?.Count == 4 && receipt.HellConsumedSlots.Length == 2 &&
                ItemCount(after,3330) == 0 && ItemCount(after,10305821) == 0 && after.Fatigue?.Remaining == before.Fatigue!.Remaining - 8 &&
                after.Fatigue?.Used == before.Fatigue.Used + 8 && after.DungeonState?.LastAttempt == before.DungeonState!.LastAttempt + 1,
                "超星空入场同一提交扣40张邀请函、4灵石和8疲劳，并建立四房实例");
            var info = DfoServer.Network.Builders.DungeonPackets.Information(receipt!.Entry);
            var startMapHeader = DfoServer.Network.Builders.DungeonPackets.StartMap(receipt.Entry,1);
            var startMap = DfoServer.Network.Builders.DungeonPackets.StartMap(receipt.Entry,receipt.Monsters,1);
            That(info[10] == byte.MaxValue && info[11] == byte.MaxValue && info[14] == 1 && info[18] == 0x0B &&
                startMapHeader[7] == 1 && startMapHeader[20] == 1 && startMap.AsSpan(0,18).SequenceEqual(startMapHeader.AsSpan(0,18)),
                "超星空发送深渊模式标记但不伪造密门坐标");
            var loaded = cosmoRich.Session.CompleteFormalDungeonLoading();
            That(loaded is { Entry.IsCosmo: true } && loaded.Snapshot.ConsumedFatigue == 8 && cosmoRich.Read().Fatigue == after.Fatigue,
                "超星空加载不重复扣疲劳，消耗计数仍为8");
        }

        foreach (var (passId, stoneId) in new[] { (490705255, 0), (490703917, 10305821), (CosmoPassCost.DailyAttendanceStarPass, HellPartyEntryCost.DailyAttendanceLegendStone), (CosmoPassCost.DailyAttendanceCosmoPass, 0) })
        {
            bool partialPass = stoneId != 0;
            using (var passOnly = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,95,
                [.. cosmoDone.Order()],position:cosmoPosition,items:partialPass?[(passId,2),(stoneId,4)]:[(passId,2)],fatigueRemaining:16))
            {
                var before = passOnly.Read();
                passOnly.Sql("CREATE TRIGGER cosmo_pass_fail BEFORE UPDATE ON character_dungeon_state WHEN NEW.last_attempt>OLD.last_attempt BEGIN SELECT RAISE(ABORT,'cosmo pass rollback'); END");
                Reject<Microsoft.Data.Sqlite.SqliteException>(() => passOnly.Session.EnterFormalDungeon(7191,0,4,dungeons,rooms,
                    drops:Testing.SelfTestCatalogs.MonsterDrops(pvf),items:items.Items,pieces:items.EpicPieces,hellParty:true), "通行证入场提交故障");
                That(QuestCompletionRepositoryTests.Same(before,passOnly.Read()) && passOnly.Session.DungeonSnapshot is null,
                    "通行证、疲劳和尝试号在同一入场事务回滚，失败不能安装实例");
                passOnly.Sql("DROP TRIGGER cosmo_pass_fail");
                var receipt = passOnly.Session.EnterFormalDungeon(7191,0,4,dungeons,rooms,
                    drops:Testing.SelfTestCatalogs.MonsterDrops(pvf),items:items.Items,pieces:items.EpicPieces,hellParty:true);
                That(receipt is { HellTicket.Count: 1, EntryFatigueCharged: 8 } && receipt.HellTicket.ItemId==passId &&
                    (partialPass?receipt.LegendTicket is {Count:2}&&receipt.LegendTicket.ItemId==stoneId:receipt.LegendTicket is null) &&
                    receipt.HellConsumedSlots.Length == (partialPass?2:1) && ItemCount(passOnly.Read(),passId) == 1 &&
                    (partialPass?ItemCount(passOnly.Read(),stoneId)==2:true),
                    "入场重试按资源准确消耗一张券，星空券额外2石（含签到星空券+活动灵石），超星空券无需灵石");
            }

        }

        var (done, position) = Selection(towns, Tayberrs);
        done.Add(6116);
        int[] completed = [.. done.Order()];
        // 泰波尔斯只开放指定难度；取资源允许的最低难度。
        byte difficulty = (byte)Enumerable.Range(0, 5).First(value => tayberrs.AllowsDifficulty((byte)value, false));
        // 缺入场票：以 entry-item-required 拒绝，不写库。
        using (var poor = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, completed, position: position))
        {
            var before = poor.Read();
            That(poor.Session.EnterFormalDungeon(Tayberrs, 0, difficulty, dungeons, rooms, pieces: items.EpicPieces) is null &&
                poor.Session.LastFormalDungeonRejection == "entry-item-required:" + RefinedStone &&
                poor.Read().Revision == before.Revision && poor.Read().Fatigue == before.Fatigue &&
                poor.Read().DungeonState == before.DungeonState,
                "没有精炼的时空石：拒绝原因 entry-item-required:10158964，revision、疲劳与尝试号不变：" + poor.Session.LastFormalDungeonRejection);
            That(poor.Session.EnterFormalDungeon(Tayberrs, 0, difficulty, dungeons, rooms) is null &&
                poor.Session.LastFormalDungeonRejection == "entry-item-catalog-unavailable",
                "无法规划入场票时保守拒绝，不免费放行");
        }
        // 疲劳不足 N：以 start-fatigue-insufficient 拒绝，票不动。
        using (var tired = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, completed, position: position,
            items: [(RefinedStone, 2)], fatigueRemaining: 9))
        {
            var before = tired.Read();
            That(before.Fatigue?.Remaining == 9 && tired.Session.EnterFormalDungeon(Tayberrs, 0, difficulty, dungeons, rooms, pieces: items.EpicPieces) is null &&
                tired.Session.LastFormalDungeonRejection == "start-fatigue-insufficient:10" && tired.Read().Revision == before.Revision &&
                Stones(tired.Read()) == 2 && tired.Read().Fatigue == before.Fatigue,
                "剩余疲劳 9 < 10：拒绝原因 start-fatigue-insufficient:10，票与疲劳不变：" + tired.Session.LastFormalDungeonRejection);
        }
        // 票够、疲劳够：入场提交一次扣 1 张票 + 10 点疲劳；重复请求与首次加载都不再扣。
        using (var rich = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, completed, position: position,
            items: [(RefinedStone, 2)], itemCatalog: items.Items, inventorySetup: role => new(role.Inventory, role.Inventory with
                { Items = role.Inventory.Items.Add(new(0, new(InventoryListType.Main, 3),
                    InventoryRewardGrantService.Create(items.Items.Get(400360000)) with { Durability = 7 })) }, "test_entry_energy")))
        {
            var before = rich.Read();
            rich.Sql("CREATE TRIGGER entry_energy_abort AFTER UPDATE ON character_fatigue BEGIN SELECT RAISE(ABORT,'entry energy rollback'); END");
            try
            {
                Reject<SqliteException>(() => rich.Session.EnterFormalDungeon(Tayberrs, 0, difficulty, dungeons, rooms,
                    pieces: items.EpicPieces), "入场能量、门票写后疲劳失败整笔回滚");
                var rejected = rich.Read();
                That(rejected.Revision == before.Revision && rejected.Fatigue == before.Fatigue &&
                    rejected.DungeonState == before.DungeonState && rejected.Inventory.Items.SequenceEqual(before.Inventory.Items),
                    "拒绝后不留半笔能量、门票、疲劳或尝试号");
            }
            finally { rich.Sql("DROP TRIGGER entry_energy_abort"); }
            var receipt = rich.Session.EnterFormalDungeon(Tayberrs, 0, difficulty, dungeons, rooms, pieces: items.EpicPieces);
            var after = rich.Read();
            That(receipt is not null && receipt.Entry.EntryItem == new DungeonRequiredItemRow(RefinedStone, 1, 1) && receipt.Entry.StartFatigue == 10 &&
                receipt.EntryFatigueCharged == 10 && !receipt.EntryItemConsumedSlots.IsDefaultOrEmpty && Stones(after) == 1 &&
                after.Fatigue?.Remaining == before.Fatigue!.Remaining - 10 && after.Fatigue?.Used == before.Fatigue.Used + 10 &&
                after.Revision == before.Revision + 2 && after.DungeonState?.LastAttempt == before.DungeonState!.LastAttempt + 1 &&
                receipt.Entry.CharacterRevision == after.Revision,
                "入场提交：扣 1 张 10158964、扣 10 点疲劳、尝试号 +1，同一事务：" + rich.Session.LastFormalDungeonRejection);
            var source = before.Inventory.Find(new(InventoryListType.Main, 3))!;
            That(after.Inventory.Find(source.Position) == source with { Core = source.Core with { Durability = 0 } } &&
                receipt!.FatigueEnergySlots.SequenceEqual([source.Position]),
                "一次性疲劳10消耗剩余能量7至0，不因能量不足阻止入场，复用原库存提交及角色版本");
            That(Receipts(rich, CharacterFatigueState.StartDungeonRuleId) == 1 && Receipts(rich, CharacterFatigueState.NormalRoomRuleId) == 0,
                "入场一次性疲劳写一条 local-start-dungeon-fatigue-v1 账本，不写逐房账本");
            That(rich.Session.EnterFormalDungeon(Tayberrs, 0, difficulty, dungeons, rooms, pieces: items.EpicPieces) is null &&
                Stones(rich.Read()) == 1 && rich.Read().Fatigue == after.Fatigue && rich.Read().Revision == after.Revision,
                "重复入场请求被拒，不再扣票或疲劳");
            var loaded = rich.Session.CompleteFormalDungeonLoading();
            That(loaded is not null && loaded.Snapshot.ConsumedFatigue == 10 && rich.Read().Fatigue == after.Fatigue &&
                loaded.FatigueEnergySlots.IsEmpty && rich.Read().Revision == after.Revision &&
                rich.Read().Inventory.Items.SequenceEqual(after.Inventory.Items) && Receipts(rich, CharacterFatigueState.NormalRoomRuleId) == 0,
                "首次房间加载不再逐房扣点，当局应耗疲劳计 10");
            That(rich.Session.CompleteFormalDungeonLoading() is null && rich.Read().Fatigue == after.Fatigue && Stones(rich.Read()) == 1,
                "重复加载不重复扣");
        }
        // 仓储原子性：疲劳 owner 在事务内被发现已变化时，已写入的扣票随整笔回滚。
        using (var race = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, completed, position: position,
            items: [(RefinedStone, 2)]))
        {
            var expected = race.Read();
            var plan = InventoryMaterialConsumptionService.Plan(expected.Inventory, [new ItemMaterialCost(RefinedStone, 1)], items.EpicPieces,
                DateTimeOffset.UtcNow).Plan!;
            var identity = new DungeonRunIdentity(expected.Id, 0, expected.DungeonState!.LastAttempt + 1, Guid.NewGuid(), 1);
            race.Sql("UPDATE character_fatigue SET remaining=remaining-1");
            Reject<InvalidOperationException>(() => race.Repository.CommitDungeonEntry(race.Owner, expected, expected.DungeonState.NextObjectKey,
                new DungeonEntryCharge(plan, 10, identity, 1)), "疲劳原owner变化时整笔拒绝");
            var current = race.Read();
            That(Stones(current) == 2 && current.Revision == expected.Revision && current.DungeonState == expected.DungeonState &&
                current.Fatigue?.Remaining == expected.Fatigue!.Remaining - 1 && Receipts(race, CharacterFatigueState.StartDungeonRuleId) == 0,
                "提交失败不留半扣：票、revision、尝试号与账本都保持原状");
            Reject<InvalidDataException>(() => race.Repository.CommitDungeonEntry(race.Owner, current, current.DungeonState!.NextObjectKey,
                new DungeonEntryCharge(null, 10, identity with { TaskAttempt = current.DungeonState.LastAttempt + 2 }, 1)),
                "入场费用必须绑定本次待安装的尝试号");
        }
        Console.WriteLine($"Dungeon entry cost checks passed: {Count - checks}");
    }

    static void DaybreakEntryTransaction(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog skills,
        CharacterGrowthCatalog growth, QuestCatalog quests, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
        ItemResources items, DungeonIncountCatalog incounts)
    {
        var (done, position) = Selection(towns, 530);
        done.UnionWith([6030, 6080, 6082, 10112]);
        using var fixture = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
            [.. done], position: position, items: [(490011739, 2), (10161610, 20)], fatigueRemaining: 16, incounts: incounts);
        That(fixture.Session.BeginDungeonSelection() is not null, "黎明抵料事务从真实选图入口准备");
        var before = fixture.Read();
        long IncountRows() => Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_dungeon_incount"));
        long beforeRows = IncountRows();
        fixture.Sql("CREATE TRIGGER daybreak_entry_fail AFTER UPDATE ON character_fatigue WHEN NEW.remaining<OLD.remaining AND NEW.used>OLD.used BEGIN SELECT RAISE(ABORT,'daybreak entry rollback'); END");
        Reject<SqliteException>(() => fixture.Session.EnterFormalDungeon(530, 0, 3, dungeons, rooms,
            pieces: items.EpicPieces), "黎明扣料、次数与预留提交后实际疲劳写的真实SQL故障");
        That(QuestCompletionRepositoryTests.Same(before, fixture.Read()) &&
            QuestCompletionRepositoryTests.Same(before, fixture.Session.Selected!) && IncountRows() == beforeRows &&
            fixture.Session.DungeonSnapshot is null,
            "晚期失败同时回滚券、芽、疲劳、次数、revision、对象键和attempt，不安装实例");
        fixture.Sql("DROP TRIGGER daybreak_entry_fail");
        var receipt = fixture.Session.EnterFormalDungeon(530, 0, 3, dungeons, rooms, pieces: items.EpicPieces);
        var after = fixture.Read();
        var counts = fixture.Repository.LoadDungeonIncounts(fixture.Owner, before.Id);
        That(receipt is { EntryFatigueCharged: 8, ChannelConsumedSlots.Length: 1, EntryItemConsumedSlots.IsEmpty: true } &&
            ItemCount(after, 490011739) == 1 && ItemCount(after, 10161610) == 20 && after.Fatigue?.Remaining == 8 &&
            after.DungeonState!.LastAttempt == before.DungeonState!.LastAttempt + 1 &&
            after.DungeonState.NextObjectKey > before.DungeonState.NextObjectKey &&
            counts is [{ Dungeon: 530, Usage: { DailyUsed: 1, WeeklyUsed: 1 } }],
            "移除故障后重新准备只扣一张券、一次次数及8疲劳，原频道差量只发布实际券槽");
        That(fixture.Session.EnterFormalDungeon(530, 0, 3, dungeons, rooms, pieces: items.EpicPieces) is null &&
            QuestCompletionRepositoryTests.Same(after, fixture.Read()) && IncountRows() == beforeRows + 1,
            "已安装实例的重复入场不再扣券、次数或预留对象键");
    }

    static void ImmortalCorridor(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog skills, CharacterGrowthCatalog growth,
        QuestCatalog quests, DungeonCatalog dungeons, DungeonRoomCatalog rooms)
    {
        var definition = dungeons.Get(7154);
        var (done, position) = Selection(towns, 7154);
        foreach (var option in towns.DungeonMaps[AreaDungeonWorldMap(towns, 7154)].Options.Where(row => row.DungeonId == 7154 && !row.InProgress && row.QuestId > 0))
            done.Add(option.QuestId);
        That(definition.Mazes.Count == 4 && definition.StartFatigue == 8,
            "不灭回廊保留真实四路线和入场八点疲劳");
        foreach (var maze in definition.Mazes)
        {
            using var f = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
                done.ToArray(), position: position, fatigueRemaining: 8);
            var before = f.Read();
            That(f.Session.BeginDungeonSelection() is not null, "不灭回廊从克洛诺斯岛正常打开选图");
            var entered = f.Session.EnterFormalDungeon(7154, 0, 0, dungeons, rooms, draw: count => maze.SourceOrdinal % count);
            That(entered is not null && entered.Entry.Maze.SourceOrdinal == maze.SourceOrdinal &&
                entered.Entry.StartMap.Id == 311290 && entered.Entry.StartMap.Path.EndsWith("c01..map", StringComparison.OrdinalIgnoreCase) &&
                f.Read().Fatigue!.Remaining == 0 && f.Read().DungeonState!.LastAttempt == before.DungeonState!.LastAttempt + 1,
                "不灭回廊每条路线真实建立实例并只扣八点疲劳：" + f.Session.LastFormalDungeonRejection);
            That(f.Session.CompleteFormalDungeonLoading() is not null && f.Read().Fatigue!.Remaining == 0 &&
                f.Session.EnterFormalDungeon(7154, 0, 0, dungeons, rooms) is null &&
                f.Read().DungeonState!.LastAttempt == before.DungeonState!.LastAttempt + 1,
                "合法连续点文件名可完成加载，重复入场不再扣费或预留尝试号");
        }
    }

    static void GateRejected(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog skills, CharacterGrowthCatalog growth,
        QuestCatalog quests, DungeonCatalog dungeons, DungeonRoomCatalog rooms, int dungeonId, int questId, OperationHopeCatalog? hope = null)
    {
        var (done, position) = Selection(towns, dungeonId);
        done.Remove(questId);
        using (var blocked = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, [.. done.Order()], position: position))
        {
            var before = blocked.Read();
            if (hope is not null)
                That(blocked.Session.BeginOperationHope(hope,dungeons,_ => 0),"希望先固定1598选择，仍须原16选图前置校验");
            That(blocked.Session.EnterFormalDungeon(checked((uint)dungeonId), 0, hope?.Difficulty ?? 0, dungeons, rooms) is null &&
                blocked.Session.LastFormalDungeonRejection == "selection-quest-required:" + questId && blocked.Read().Revision == before.Revision,
                dungeonId + " 未完成 " + questId + "：拒绝原因 selection-quest-required:" + questId + "（实际 " + blocked.Session.LastFormalDungeonRejection + "）");
        }
        done.Add(questId);
        using var open = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95, [.. done.Order()], position: position);
        if (hope is not null)
            That(open.Session.BeginOperationHope(hope,dungeons,_ => 0),"已完成前置后可固定同资源的希望选择");
        var entered = open.Session.EnterFormalDungeon(checked((uint)dungeonId), 0, hope?.Difficulty ?? 0, dungeons, rooms);
        if (hope is not null)
            That(entered?.Entry.Hope is not null,"完成6170后通过真实希望准入，不用其它专用门禁拒绝冒充选图检查通过");
        That(open.Session.LastFormalDungeonRejection?.StartsWith("selection-", StringComparison.Ordinal) != true,
            dungeonId + " 完成 " + questId + " 后不再被选图准入拒绝（实际 " + open.Session.LastFormalDungeonRejection + "）");
    }

    internal static (HashSet<int> Done, Location Position) Selection(TownCatalog towns, int dungeonId)
    {
        var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world && towns.DungeonMaps.TryGetValue(world, out var map) &&
            map.Error is null && map.Options.Any(option => option.DungeonId == dungeonId));
        var done = new HashSet<int>(area.Quests);
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        foreach (var gate in area.Map(done).DungeonGates.Where(gate => gate.Conditions.Length == 0).Take(1)) done.UnionWith(gate.Quests);
        var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        return (done, new Location(area.Town, area.Area, checked((ushort)(walk.X + walk.Width / 2)), checked((ushort)(walk.Y + walk.Height / 2))));
    }

    static int Stones(Character character) => character.Inventory.Items.Where(row => row.Core.ItemId == RefinedStone).Sum(row => row.Core.Count);
    static int ItemCount(Character character,int itemId) => character.Inventory.Items.Where(row => row.Core.ItemId == itemId).Sum(row => row.Core.Count);

    static int AreaDungeonWorldMap(TownCatalog towns, int dungeonId = 7191) => towns.Areas.Values.Single(row => row.DungeonWorldMap is { } world &&
        towns.DungeonMaps.TryGetValue(world,out var map) && map.Options.Any(option => option.DungeonId == dungeonId)).DungeonWorldMap!.Value;

    static long Receipts(QuestSceneTests.Fixture fixture, string rule) => fixture.Session.DungeonSnapshot is { } snapshot
        ? DungeonTransientReceipts.Find(fixture.Owner, snapshot.Identity)?.Fatigue.Values.Count(value => value.Committed && value.Rule == rule) ?? 0 : 0;
}
