using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TeleportTests
{
    const string PotionText = "[stackable type]\n`[teleport potion]` 0\n[minimum level]\n60\n[cool time]\n10000\n[int data]\n1 2\n[/int data]";
    const string AuraText = "[equipment type]\n`[aurora avatar]` 0\n[aura ability]\n`[solo teleport]` 420\n[/aura ability]";

    public static void RunCore()
    {
        using (var store = new Fixture())
        {
            store.Sql("INSERT INTO character_expert_job(character_id,account,job_type,expertness) SELECT id,account,3,51 FROM characters");
            store.Session.Select(0); store.Session.MarkReady();
            var quote = new DfoServer.Game.ExpertJob.ExpertJobStoreRecord(store.Session.Selected!.Id, 3, 0, "", 1, 0, 0, 0, 1, 300);
            That(store.Session.OpenExpertJobStore(), "传送夹具开设副职业店");
            store.Session.QuoteExpertJobStore(quote);
            var result = store.Session.TeleportPotion(store.Request, store.Service);
            That(result.Receipt is not null && !store.Session.ExpertJobStoreOpen && !store.Session.MatchesExpertJobStoreQuote(quote),
                "Town内传送也清除旧店铺与旧报价");
        }
        PendingQuestTransactions();
        PotionGateModes();
        AuraVariants();
        SingleMemberParty();
        var potion = TeleportRequests.Potion(Convert.FromHexString("41004EAC27000026002F05EA0001"));
        That(potion == new PotionTeleportRequest(65, 2600014, 0, new(38, 0, 1327, 234, 1)), "原生237请求完整保留源槽、模板、模式和目的地");
        That(TeleportRequests.Solo(Convert.FromHexString("FFFFFFFF0700000027030F03CD0005")) ==
            new SoloTeleportRequest(-1, 7, new(39, 3, 783, 205, 5)), "原生470区分玩家ID与NPC ID而非把i32截断成u16");
        That(TeleportRequests.Solo(Convert.FromHexString("ffffffffffffffff28049d014b0105")) ==
            new SoloTeleportRequest(-1, -1, new(40, 4, 413, 331, 5)), "实机个人传送470的角色和NPC都是-1，坐标在后7字节");
        Reject<InvalidDataException>(() => TeleportRequests.Potion(new byte[13]), "237短包不能被当成功");
        Reject<InvalidDataException>(() => TeleportRequests.Solo(new byte[16]), "470未知追加字段拒绝");
        That(TeleportCatalog.ReadPotion(new(PotionText)) is { MinimumLevel: 60, CoolTime: 10000 } &&
            TeleportCatalog.ReadAura(new(AuraText)) == new AuraAbilityDefinition(AuraAbilityKind.SoloTeleport, 420), "资源药剂毫秒与光环秒分别保留");
        That(TeleportCatalog.ReadAura(new(AuraText.Replace("[solo teleport]", "[none]"))) is null,
            "无传送能力的光环不能获得传送资格");
        That(TeleportRequests.Party(Convert.FromHexString("27030F03CD000501")) == new PartyTeleportRequest(new(39, 3, 783, 205, 5), 1) &&
            Convert.ToHexString(TeleportPackets.PartyRejected(new(new(39, 3, 783, 205, 5), 1))) == "001300270000000001",
            "本版434请求8B，失败正文保留公共u16错误、town、剩余秒和请求来源");
        Reject<InvalidDataException>(() => TeleportRequests.Party(new byte[7]), "旧版7B组队请求不能照搬到本版");
        That(TeleportPackets.AuraEffect(65000, arrival: false).SequenceEqual(new byte[] { 1, 1, 232, 253 }) &&
            TeleportPackets.AuraEffect(65000, arrival: true).SequenceEqual(new byte[] { 2, 1, 232, 253 }),
            "原生379仅含阶段、角色数量和完整u16 ID，不混用物品ID或坐标");
        That(TeleportCatalog.ReadPotion(new(PotionText + "\n[need material]\n1 2")) is { MinimumLevel:60, CoolTime:10000 } exchangePotion &&
            exchangePotion.DungeonMaps.SetEquals([1,2]),
            "商店兑换材料不构成瞬移药的使用门禁");
        using var fixture = new Fixture();
        var session = fixture.Session;
        That(!session.TeleportPotion(fixture.Request, fixture.Service).Accepted, "未选角不能消费");
        session.Select(0); session.MarkReady();
        var before = fixture.Read();
        var onlineBefore = session.Selected;
        fixture.Sql("CREATE TRIGGER teleport_fail BEFORE INSERT ON character_teleport_cooldowns BEGIN SELECT RAISE(ABORT,'rollback'); END");
        Reject<SqliteException>(() => session.TeleportPotion(fixture.Request, fixture.Service), "扣药后冷却末段失败");
        That(System.Text.Json.JsonSerializer.Serialize(fixture.Read()) == System.Text.Json.JsonSerializer.Serialize(before) &&
            ReferenceEquals(session.Selected, onlineBefore) && session.Selected!.Revision == before.Revision &&
            fixture.Read().Inventory.Find(new(InventoryListType.Main, 65))!.Core.Count == 3, "真实SQL失败同时回滚位置、数量、冷却和在线快照");
        That(fixture.Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name='teleport_potion'") == 0, "失败扣药审计也回滚");
        fixture.Sql("DROP TRIGGER teleport_fail");
        var committed = session.TeleportPotion(fixture.Request, fixture.Service);
        var actual = fixture.Read();
        That(committed.Accepted && actual.Position == fixture.Request.Target && actual.Inventory.Find(new(InventoryListType.Main, 65))!.Core.Count == 2 &&
            actual.TeleportCooldowns.PotionUntil == fixture.Clock.Now + 10000 && session.CanPublishTravel(committed.Receipt!.Travel),
            "提交后真实UID库存、位置和冷却一致，回执属于已安装快照");
        That(!session.TeleportPotion(fixture.Request, fixture.Service).Accepted, "重复药剂请求冷却内不二次消费");
        session.ReturnToRoster(); session.Select(0); session.MarkReady();
        That(!session.CanPublishTravel(committed.Receipt!.Travel) && !session.TeleportPotion(fixture.Request, fixture.Service).Accepted,
            "重选旧回执失效且持久冷却保留");
        fixture.Clock.Now += 10000;
        That(session.TeleportPotion(fixture.Request, fixture.Service).Accepted, "冷却到期后同区域也可再次传送");
        var character = session.Selected!;
        That(fixture.Service.Potion(character, fixture.Request with { Target = new(3, 0, 10, 10) }, fixture.Clock.Now + 10000).Reason == "target-quest-required" &&
            fixture.Service.Potion(character with { Level = 59 }, fixture.Request, fixture.Clock.Now + 10000).Plan is null,
            "绕过普通邻接不绕过目的地任务与药剂等级");
        That(fixture.Service.Solo(character, new(-1, 999, new(2, 0, 10, 10)), null, fixture.Clock.Now).Plan is null &&
            fixture.Service.Solo(character, new(-1, 7, new(1, 0, 10, 10)), null, fixture.Clock.Now).Plan is null &&
            fixture.Service.Solo(character, new(-1, -1, new(1, 0, 10, 10, 5)), null, fixture.Clock.Now).Reason == "private-room-target" &&
            fixture.Service.Solo(character, new(-1, -1, new(2, 0, 30, 30, 5)), null, fixture.Clock.Now).Reason == "ready",
            "未知NPC与私有房间目标拒绝；双-1按请求坐标传送");
        var npcLanding = new DfoServer.GameWorld.Towns.Location(2, 0, 20, 101, 5);
        That(!fixture.Towns.IsPosition(npcLanding) && fixture.Service.Solo(character, new(-1, 7, npcLanding), null, fixture.Clock.Now)
            .Plan?.Position == npcLanding && fixture.Service.Solo(character, new(-1, 7, npcLanding with { Town = 3 }), null, fixture.Clock.Now)
            .Reason == "target-quest-required",
            "NPC固定落点可在通用步行矩形外，坐标原样保留，但目的地任务权限不放宽");
        That(fixture.Service.Solo(character, new(-1, -1, npcLanding), null, fixture.Clock.Now).Reason == "target-position-unavailable" &&
            fixture.Service.Potion(character, fixture.Request with { Target = npcLanding }, fixture.Clock.Now + 10000).Reason == "target-position-unavailable" &&
            TownTravel.Walk(character, new(20, 101, 0, 0, 0, 0), fixture.Towns, new HashSet<int>()) is null,
            "NPC传送豁免不扩大到普通地图点选、药剂或城镇行走");
        var aura = session.TeleportSolo(new(-1, 7, new(2, 0, 30, 30)), fixture.Service, null);
        That(aura.Accepted && fixture.Read().TeleportCooldowns.AuraUntil == fixture.Clock.Now + 420000 &&
            TeleportPackets.Cooldown(aura.Receipt!.Travel.Character, fixture.Clock.Now).SequenceEqual(new byte[] { 1, 2, 164, 1, 0, 0 }),
            "光环7分钟持久冷却投影为type2及420秒");
        using var replacement = fixture.Owners.Acquire(fixture.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => session.TeleportPotion(fixture.Request, fixture.Service), "旧generation不能传送");
        That(new CharacterRepository(fixture.Database).LoadCharacter(replacement, 0).TeleportCooldowns.AuraUntil == fixture.Clock.Now + 420000,
            "新仓储和新owner恢复已提交光环冷却");
    }

    static void AuraVariants()
    {
        foreach (var (tag, kind) in new[] { ("party teleport", AuraAbilityKind.PartyTeleport),
            ("solo teleport", AuraAbilityKind.SoloTeleport), ("upgrade solo teleport", AuraAbilityKind.UpgradeSoloTeleport) })
        {
            foreach (int seconds in new[] { 0, 300, 420, 600 })
                That(TeleportCatalog.ReadAura(new(AuraText.Replace("[solo teleport]` 420", "[" + tag + "]` " + seconds))) == new AuraAbilityDefinition(kind, seconds),
                    "三类能力分别解析PVF冷却，不硬编码个人420或组队600");
        }
        foreach (string body in new[] { "`[unknown]` 300", "`[upgrade solo teleport]` -1", "`[solo teleport]` 2147484", "`[none]` 0", "`[solo teleport]`" })
            That(TeleportCatalog.ReadAura(new("[equipment type]\n`[aurora avatar]` 0\n[aura ability]\n" + body + "\n[/aura ability]")) is null,
                "未知、负值、原生毫秒溢出及缺失参数不取得传送能力");
        using var fixture = new Fixture();
        var session = fixture.Session;
        session.Select(0); session.MarkReady();
        var request = new SoloTeleportRequest(-1, -1, new(2, 0, 30, 30, 5));
        That(session.TeleportSolo(request, fixture.Service, null).Accepted, "普通个人传送仍使用原入口");
        var ordinary = session.Selected!;
        var replacement = ordinary.Inventory with { Items = ordinary.Inventory.Items.Select(item => item.Position == new InventorySlot(InventoryListType.Equipment, 9)
            ? item with { Core = item.Core with { ItemId = 65051 } } : item).ToImmutableArray() };
        fixture.Repository.CommitInventory(fixture.Owner, ordinary, new(ordinary.Inventory, replacement, "aura_fixture_swap"));
        session.ReturnToRoster(); session.Select(0); session.MarkReady();
        var before = session.Selected!;
        fixture.Sql("CREATE TRIGGER upgraded_teleport_fail BEFORE UPDATE ON character_teleport_cooldowns BEGIN SELECT RAISE(ABORT,'upgraded rollback'); END");
        Reject<SqliteException>(() => session.TeleportSolo(request, fixture.Service, null), "升级个人传送冷却末段SQL失败");
        That(ReferenceEquals(before, session.Selected) && fixture.Read().TeleportCooldowns == before.TeleportCooldowns &&
            fixture.Read().Position == before.Position && fixture.Read().Revision == before.Revision,
            "升级传送失败回滚位置、旧普通冷却、新冷却及revision，不发布新快照");
        fixture.Sql("DROP TRIGGER upgraded_teleport_fail");
        That(session.CanPrecheckTeleport(fixture.Service), "普通个人仍在冷却不占用ARAD的升级个人type3计时器");
        var upgraded = session.TeleportSolo(request, fixture.Service, null);
        var after = fixture.Read();
        That(upgraded.Accepted && upgraded.Receipt!.AuraKind == AuraAbilityKind.UpgradeSoloTeleport &&
            after.TeleportCooldowns.UpgradeAuraUntil == fixture.Clock.Now + 300000 && after.TeleportCooldowns.AuraUntil == ordinary.TeleportCooldowns.AuraUntil &&
            TeleportPackets.Cooldown(after, fixture.Clock.Now).SequenceEqual(new byte[] { 2, 2, 164, 1, 0, 0, 3, 44, 1, 0, 0 }),
            "升级个人以PVF300秒启动type3，恢复时保留type2和type3独立计时器");
        That(!session.TeleportSolo(request, fixture.Service, null).Accepted && !session.CanPrecheckTeleport(fixture.Service),
            "升级请求重放和玩家位置预检查都受本类型持久冷却限制");
        var boundary = after with { TeleportCooldowns = new(0, fixture.Clock.Now + 1, long.MaxValue) };
        That(TeleportPackets.Cooldown(boundary, fixture.Clock.Now).SequenceEqual(new byte[] { 2, 2, 1, 0, 0, 0, 3, 155, 196, 32, 0 }) &&
            TeleportPackets.Cooldown(after with { TeleportCooldowns = new() }, fixture.Clock.Now, AuraAbilityKind.UpgradeSoloTeleport)
                .SequenceEqual(new byte[] { 1, 3, 0, 0, 0, 0 }),
            "不足1秒向上取整，历史超长截止值钳至原生毫秒范围，零冷却也按实际type3刷新");
        session.ReturnToRoster(); session.Select(0); session.MarkReady();
        That(session.Selected!.TeleportCooldowns == after.TeleportCooldowns && !session.TeleportSolo(request, fixture.Service, null).Accepted,
            "重新选角不丢升级个人冷却，也不覆盖普通个人冷却");
        var party = after with { Inventory = after.Inventory with { Items = after.Inventory.Items.Select(item => item.Position == new InventorySlot(InventoryListType.Equipment, 9)
            ? item with { Core = item.Core with { ItemId = 65052 } } : item).ToImmutableArray() } };
        That(fixture.Service.Aura(party, fixture.Clock.Now)?.Kind == AuraAbilityKind.PartyTeleport &&
            fixture.Service.Solo(party, request, null, fixture.Clock.Now).Reason == "personal-aura-unavailable",
            "组队声明已注册，但不能借个人470绕过队伍上下文");
        var expired = after with { Inventory = after.Inventory with { Items = after.Inventory.Items.Select(item => item.Avatar is not null
            ? item with { Avatar = item.Avatar with { ExpireTime = checked((int)(fixture.Clock.Now / 1000)) } } : item).ToImmutableArray() } };
        That(fixture.Service.Aura(expired, fixture.Clock.Now) is null, "已过期升级光环没有资格");
        fixture.Clock.Now += 300000;
        That(session.TeleportSolo(request, fixture.Service, null).Accepted, "升级个人300秒到期即可再次使用，不套用普通420秒");
    }

    static void SingleMemberParty()
    {
        using var fixture = new Fixture();
        var session = fixture.Session;
        var request = new PartyTeleportRequest(new(2, 0, 30, 30, 5), 1);
        That(!session.TeleportSingleMemberParty(request, fixture.Service).Accepted, "未选角不接受组队光环传送");
        session.Select(0); session.MarkReady();
        That(session.TeleportSingleMemberParty(request, fixture.Service).Reason == "party-context-unavailable", "无队伍不能把自己当成整队");
        var settings = new DfoServer.Game.Parties.SoloPartyRequest(0, new(3, null, 4, 0, 5, 0, 0, 0, 0, 7));
        session.SetSoloParty(settings);
        That(session.TeleportSingleMemberParty(request, fixture.Service).Reason == "party-aura-unavailable", "已建队仍须穿戴实际组队光环");
        var original = session.Selected!;
        var equipped = original.Inventory with { Items = original.Inventory.Items.Select(item => item.Position == new InventorySlot(InventoryListType.Equipment, 9)
            ? item with { Core = item.Core with { ItemId = 65052 } } : item).ToImmutableArray() };
        fixture.Repository.CommitInventory(fixture.Owner, original, new(original.Inventory, equipped, "party_aura_fixture"));
        session.ReturnToRoster(); session.Select(0); session.MarkReady(); session.SetSoloParty(settings);
        var before = session.Selected!;
        foreach (var invalid in new[] { request with { Source = 0 }, request with { Target = new(1, 0, 20, 20, 5) },
            request with { Target = new(3, 0, 20, 20, 5) }, request with { Target = new(2, 0, 200, 200, 5) } })
            That(!session.TeleportSingleMemberParty(invalid, fixture.Service).Accepted && ReferenceEquals(before, session.Selected),
                "来源、私有房间、任务与坐标不符合时不移动或写入冷却");
        That(!session.TeleportSingleMemberParty(request, fixture.Service, blocked: true).Accepted, "交互占用时不启动组队传送");
        fixture.OnAuraRead = () =>
        {
            fixture.OnAuraRead = null;
            session.LeaveSoloParty(); session.SetSoloParty(settings);
        };
        That(session.TeleportSingleMemberParty(request, fixture.Service).Reason == "party-context-changed" && ReferenceEquals(before, session.Selected),
            "锁外资源规划期间退队重建，旧队伍授权不能移动新队伍");
        fixture.Sql("CREATE TRIGGER party_teleport_fail BEFORE INSERT ON character_teleport_cooldowns BEGIN SELECT RAISE(ABORT,'party rollback'); END");
        Reject<SqliteException>(() => session.TeleportSingleMemberParty(request, fixture.Service), "组队光环冷却末段失败");
        That(ReferenceEquals(before, session.Selected) && fixture.Read().Position == before.Position && fixture.Read().Revision == before.Revision &&
            fixture.Read().TeleportCooldowns == before.TeleportCooldowns, "组队传送SQL失败时位置、冷却和在线角色一起回滚");
        fixture.Sql("DROP TRIGGER party_teleport_fail");
        var party = session.ReadSoloParty();
        var result = session.TeleportSingleMemberParty(request, fixture.Service);
        var actual = fixture.Read();
        That(result.Accepted && result.Receipt!.AuraKind == AuraAbilityKind.PartyTeleport && actual.Position == request.Target &&
            actual.Revision == before.Revision + 1 && actual.TeleportCooldowns == new TeleportCooldowns(0, 0, 0, fixture.Clock.Now + 600000) &&
            ReferenceEquals(party, session.ReadSoloParty()) && session.GetState() == UserState.Town,
            "唯一队长已同意后整队移动，队伍不解散，Town状态不变，独立600秒冷却与位置同事务");
        That(Convert.ToHexString(TeleportPackets.PartyReady(actual with { Id = 65000 })) ==
            "0201010000E8FD01FFFF00FFFF00FFFF00FFFF00FFFF00FFFF00FFFF0000" &&
            Convert.ToHexString(TeleportPackets.PrepareAuraEffect(65000)) == "0301E8FD" &&
            Convert.ToHexString(TeleportPackets.Cooldown(actual, fixture.Clock.Now)) == "010058020000" &&
            Convert.ToHexString(TeleportPackets.PartyRejected(request, 600, 22)) == "001600025802000001",
            "原生377固定八行且队长槽0，379准备阶段3，378独立type0，434冷却失败保留实际剩余秒");
        session.LeaveSoloParty(); session.SetSoloParty(settings);
        That(session.TeleportSingleMemberParty(request, fixture.Service).Reason == "aura-cooldown", "退队重建不能重置角色组队光环冷却");
        session.ReturnToRoster(); session.Select(0); session.MarkReady(); session.SetSoloParty(settings);
        That(session.Selected!.TeleportCooldowns == actual.TeleportCooldowns &&
            session.TeleportSingleMemberParty(request, fixture.Service).Reason == "aura-cooldown", "重选恢复组队冷却，不因队伍上下文重建而清零");
        fixture.Clock.Now += 600000;
        That(session.TeleportSingleMemberParty(request, fixture.Service).Accepted, "PVF600秒到期后单人队伍可再次传送");
    }

    // 合成入口区域：world map 49 不在药剂 int data（1 2）内。默认开放，Pvf 模式恢复白名单拒绝；其余准入照常。
    static void PotionGateModes()
    {
        TownArea Area(byte town, byte area, int[] quests, int? world) => new(town, area, 1, quests, [], [], new(town, area, 10, 10),
            new("test.map", [new(new(0, 0, 100, 100), [], [])], [], false, []), [], []) { DungeonWorldMap = world };
        var towns = new TownCatalog(new() { [(2, 0)] = Area(2, 0, [], null), [(54, 2)] = Area(54, 2, [], 49), [(54, 3)] = Area(54, 3, [42], 49) },
            new(2, 0, 10, 10), new(2, 0, 10, 10), new(0, [], []), [], new Dictionary<uint, uint[]>());
        var definitions = new TeleportCatalog(_ => PotionText, new Dictionary<int, string>(), new Dictionary<int, string> { [2600014] = "potion.stk" });
        var character = new Character(1, 0, "Gate", 1, new(2, 0, 10, 10), null) { Level = 60,
            Inventory = new([new(1, new(InventoryListType.Main, 65), new(2600014, ItemKind.Consumable, 3))]) };
        var request = new PotionTeleportRequest(65, 2600014, 0, new(54, 2, 20, 20, 1));
        TeleportService Service(TeleportPotionGateMode mode) => new(towns, definitions, _ => throw new InvalidOperationException(), null, mode);
        That(Service(TeleportPotionGateMode.Open).Potion(character, request, 0).Reason == "ready" &&
            Service(TeleportPotionGateMode.Pvf).Potion(character, request, 0).Reason == "potion-dungeon-map-unavailable",
            "默认开放白名单外的地下城入口，Pvf模式恢复int data拒绝");
        That(Service(TeleportPotionGateMode.Open).Potion(character, request with { Target = new(54, 3, 20, 20, 1) }, 0).Reason == "target-quest-required" &&
            Service(TeleportPotionGateMode.Open).Potion(character with { Level = 59 }, request, 0).Plan is null &&
            Service(TeleportPotionGateMode.Open).Potion(character, request with { Target = new(54, 2, 500, 500, 1) }, 0).Reason == "target-position-unavailable",
            "开放入口仍保留任务、药剂等级和可走位置检查");
    }

    // 真实PVF：黑市入口 54/2（[dungeon gate] 49 哈林）不在 2600014 的 int data 内。
    public static void RunResources(TownCatalog towns, DfoServer.Infrastructure.ItemResources itemResources)
    {
        foreach (var (id, expected) in new[]
        {
            (101590218, new AuraAbilityDefinition(AuraAbilityKind.None, 0)),
            (101590229, new AuraAbilityDefinition(AuraAbilityKind.PartyTeleport, 600)),
            (101590011, new AuraAbilityDefinition(AuraAbilityKind.SoloTeleport, 420)),
            (101590301, new AuraAbilityDefinition(AuraAbilityKind.UpgradeSoloTeleport, 600)),
            (401590036, new AuraAbilityDefinition(AuraAbilityKind.UpgradeSoloTeleport, 300))
        })
            That(itemResources.Teleports.Aura(id) == expected, "实际EQU四类光环能力及不同冷却按PVF声明读取：" + id);
        var character = new Character(1, 0, "Gate", 1, new(54, 1, 1599, 567, 1), null) { Level = 90, CompletedQuests = [5936],
            Inventory = new([new(1, new(InventoryListType.Main, 65), new(2600014, ItemKind.Consumable, 3))]) };
        var request = new PotionTeleportRequest(65, 2600014, 0, new(54, 2, 892, 230, 1));
        TeleportService Service(TeleportPotionGateMode mode) => new(towns, itemResources.Teleports, itemResources.Items.Get, null, mode);
        That(towns.Areas[(54, 2)].DungeonWorldMap == 49 && itemResources.Teleports.Potion(2600014) is { } potion && !potion.DungeonMaps.Contains(49),
            "真实资源：54/2为哈林地下城入口且不在药剂白名单");
        string open = Service(TeleportPotionGateMode.Open).Potion(character, request, 0).Reason,
            strict = Service(TeleportPotionGateMode.Pvf).Potion(character, request, 0).Reason;
        That(open == "ready" && strict == "potion-dungeon-map-unavailable", $"真实资源黑市入口默认可达、Pvf模式拒绝（{open}/{strict}）");
        That(Service(TeleportPotionGateMode.Open).Potion(character with { CompletedQuests = [] }, request, 0).Plan is null,
            "真实资源开放入口仍要求黑市前置任务");
    }

    static void PendingQuestTransactions()
    {
        foreach (bool loseResult in new[] { false, true })
        {
            using var fixture = new Fixture(loseResult);
            var session = fixture.Session;
            session.Select(0); session.MarkReady();
            var original = session.Selected!;
            if (loseResult)
                Reject<IOException>(() => fixture.AcceptQuest(), "接取实际提交后丢失返回值");
            else
            {
                fixture.Sql("CREATE TRIGGER teleport_quest_fail AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'rollback'); END");
                Reject<SqliteException>(() => fixture.AcceptQuest(), "接取晚期失败保留原冻结计划");
                fixture.Sql("DROP TRIGGER teleport_quest_fail");
            }
            AssertBlocked(fixture, original);
            var restored = fixture.AcceptQuest();
            That(restored?.Commit is { ProjectionCurrent: true } commit && commit.Duplicate == loseResult &&
                fixture.Scalar("SELECT count(*) FROM character_quest_lifecycle_receipts") == 1,
                "传送拒绝后原接取可恢复，返回丢失不重复提交");
            That(session.Move(new(35, 35, 0, 0, 0, 0)) && session.Travel(fixture.TravelRequest),
                "原任务恢复后合法步行和相邻切图均可提交");
            if (session.Selected!.Position.Town != 1)
                That(session.Travel(fixture.TravelRequest), "恢复后可沿真实入口返回房间");
            That(session.LeaveRoom(), "恢复后真实房间返回可提交");
            That(session.CanPrecheckTeleport(fixture.Service) && session.TeleportPotion(fixture.Request, fixture.Service).Accepted,
                "原任务明确恢复后解除传送门禁");
        }

        using var manual = new Fixture();
        manual.Session.Select(0); manual.Session.MarkReady();
        var prepared = manual.Session.PrepareManualQuestSkip(manual.Quests);
        That(prepared?.Prepared is { IsManual: true } && manual.Session.Move(new(35, 35, 0, 0, 0, 0)),
            "未提交手册不阻止合法步行");
        That(manual.Session.PrepareManualQuestSkip(manual.Quests)?.Prepared is { IsManual: true } &&
            manual.Session.Travel(manual.TravelRequest), "未提交手册不阻止相邻切图");
        That(manual.Session.PrepareManualQuestSkip(manual.Quests)?.Prepared is { IsManual: true } &&
            manual.Session.CanPrecheckTeleport(manual.Service) &&
            manual.Session.TeleportPotion(manual.Request, manual.Service).Accepted,
            "尚未提交的手册不阻止正常传送");
        That(manual.Session.FinishManualQuestSkip() is null && manual.Read().CompletedQuests.Length == 0,
            "传送替换快照后旧手册自然失效，不写任务完成");
        manual.Clock.Now += 10000;
        That(manual.Session.PrepareManualQuestSkip(manual.Quests)?.Prepared is { IsManual: true }, "新位置重新准备真实手册");
        var before = manual.Session.Selected!;
        manual.Sql("CREATE TRIGGER teleport_skip_fail AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'rollback'); END");
        Reject<SqliteException>(() => manual.Session.FinishManualQuestSkip(), "手册提交晚期失败保留恢复上下文");
        manual.Sql("DROP TRIGGER teleport_skip_fail");
        AssertBlocked(manual, before);
        That(manual.Session.FinishManualQuestSkip()?.Commit is { ProjectionCurrent: true } &&
            manual.Read().CompletedQuests.Contains(100) && manual.Session.TeleportSolo(new(-1, 7, new(2, 0, 30, 30)), manual.Service, null).Accepted,
            "原手册恢复后光环传送重新可用");

        var question = QuestEligibilityTests.Quest(100) with
        {
            Condition = "[question]", Definition = new([new("question info",
                [new(6, 0, "标题"), new(6, 0, "正文"), new(6, 0, "继续")], [])])
        };
        var next = QuestEligibilityTests.Quest(101) with { Definition = new([]) { RequiredAnswers = [new(100, 0)] } };
        using var answered = new Fixture(quests: new(new() { [100] = question, [101] = next }));
        answered.Session.Select(0); answered.Session.MarkReady();
        That(answered.Session.PrepareManualQuestSkip(answered.Quests)?.Prepared?.PendingQuestions.SequenceEqual([100]) == true &&
            answered.Session.AnswerManualQuestSkip([new(100, 0)], answered.Quests)?.Commit is { ProjectionCurrent: true },
            "最终手册回答已提交并保留重复回答缓存");
        That(answered.Session.CanPrecheckTeleport(answered.Service) &&
            answered.Session.Move(new(35, 35, 0, 0, 0, 0)) && answered.Session.Travel(answered.TravelRequest) &&
            answered.Session.TeleportPotion(answered.Request, answered.Service).Accepted &&
            answered.Session.FinishManualQuestSkip() is null &&
            answered.Scalar("SELECT count(*) FROM character_quest_skip_receipts") == 1,
            "已提交回答缓存不挡步行、切图和传送，旧最终动作不能重复任务事务");

        static void AssertBlocked(Fixture fixture, Character original)
        {
            string stored = System.Text.Json.JsonSerializer.Serialize(fixture.Read());
            var completed = QuestEligibility.Completed(original);
            That(TownTravel.Walk(original, new(35, 35, 0, 0, 0, 0), fixture.Towns, completed) is not null &&
                TownTravel.Plan(original, fixture.TravelRequest, fixture.Towns, completed).Accepted,
                "被门禁拒绝的步行坐标和双向城镇路线原本合法");
            That(!fixture.Session.CanPrecheckTeleport(fixture.Service) &&
                !fixture.Session.TeleportPotion(fixture.Request, fixture.Service).Accepted &&
                !fixture.Session.TeleportSolo(new(-1, 7, new(2, 0, 30, 30)), fixture.Service, null).Accepted &&
                !fixture.Session.Move(new(35, 35, 0, 0, 0, 0)) && !fixture.Session.Travel(fixture.TravelRequest) &&
                ReferenceEquals(fixture.Session.Selected, original) &&
                stored == System.Text.Json.JsonSerializer.Serialize(fixture.Read()),
                "未决任务拒绝预检查、传送、步行及切图，位置、完整库存、冷却、revision和原在线快照不变");
            if (fixture.Towns.IsRoom(original.Position, completed))
                That(TownTravel.LeaveRoom(original, fixture.Towns, completed).Accepted && !fixture.Session.LeaveRoom() &&
                    ReferenceEquals(fixture.Session.Selected, original) && stored == System.Text.Json.JsonSerializer.Serialize(fixture.Read()),
                    "合法房间返回也不能覆盖未决任务快照");
        }
    }

    sealed class Clock : TimeProvider
    {
        public long Now = 2000000000000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }
    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "s5a9-teleport-" + Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(root, "game.db");
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public GameSession Session { get; }
        public TeleportService Service { get; }
        public Clock Clock { get; } = new();
        public QuestCatalog Quests { get; }
        public TownCatalog Towns { get; }
        public Action? OnAuraRead { get; set; }
        public AreaRequest TravelRequest => new(Session.Selected!.Position.Town == 1 ? (byte)2 : (byte)1, 0,
            20, 20, 0, Session.Selected.Position.Town, Session.Selected.Position.Area, 0);
        readonly GoldLimitCatalog limits = new("[gold limit from level]\n0 100000\n1 100000\n[/gold limit from level]");
        readonly EpicPieceCatalog pieces = new("[piece list]\n1 2\n[/piece list]");
        public PotionTeleportRequest Request { get; } = new(65, 2600014, 0, new(2, 0, 20, 20, 1));
        public Fixture(bool loseLifecycleResult = false, QuestCatalog? quests = null)
        {
            Quests = quests ?? new(new() { [100] = QuestEligibilityTests.Quest(100) });
            Directory.CreateDirectory(root); Owner = Owners.Acquire("teleport", () => { }); Repository = new(Database, Quests);
            TownArea Area(byte town, bool room, int[] quests) => new(town, 0, 1, quests, [], [], new(town, 0, 10, 10),
                new("test.map", [new(new(0, 0, 100, 100), [], [])],
                    [new(new(0, 0, 40, 40), town == 1 ? (byte)2 : (byte)1, 0, [], [])], room, [new(7, 20, 20)]), [], []);
            var towns = new TownCatalog(new() { [(1, 0)] = Area(1, true, []), [(2, 0)] = Area(2, false, []), [(3, 0)] = Area(3, false, [42]) },
                new(1, 0, 10, 10), new(2, 0, 10, 10), new(0, [], []), [], new Dictionary<uint, uint[]>());
            Towns = towns;
            var definitions = new TeleportCatalog(path => path.StartsWith("stackable", StringComparison.Ordinal) ? PotionText :
                path.EndsWith("upgrade.equ", StringComparison.Ordinal) ? AuraText.Replace("[solo teleport]` 420", "[upgrade solo teleport]` 300") :
                path.EndsWith("party.equ", StringComparison.Ordinal) ? AuraText.Replace("[solo teleport]` 420", "[party teleport]` 600") : AuraText,
                new Dictionary<int, string> { [101590011] = "aura.equ", [65051] = "upgrade.equ", [65052] = "party.equ" }, new Dictionary<int, string> { [2600014] = "potion.stk" });
            Service = new(towns, definitions, id =>
            {
                OnAuraRead?.Invoke();
                return new(id, ItemKind.Avatar, 9, 1, 0, 1, 0, 2, 1, "free", ushort.MaxValue, 0, "Aura");
            }, Clock);
            Repository.Create(Owner, new("Teleport", new(2, 0, 10, 10), towns.InitialReturn)
            {
                Level = 60,
                Inventory = new([new(0, new(InventoryListType.Main, 65), new(2600014, ItemKind.Consumable, 3)),
                    new(0, new(InventoryListType.Equipment, 9), new(101590011, ItemKind.Avatar, 0)) { Avatar = new() }])
            });
            Session = new(loseLifecycleResult
                ? new QuestCompletionRepositoryTests.LostResultRepository(Repository) { LoseLifecycleResult = true }
                : Repository, towns, Owner, questCatalog: Quests);
        }
        // 合成会面无赠品，不读取物品资源；复用真实任务事务及恢复路径。
        public QuestLifecycleReceipt? AcceptQuest() => Session.ChangeQuest(100, QuestLifecycleAction.Accept, Quests, null!, limits, pieces);
        public Character Read() => Repository.LoadCharacter(Owner, 0);
        public long Scalar(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Database + ";Pooling=False"); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = sql; return (long)cmd.ExecuteScalar()!;
        }
        public void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Database + ";Pooling=False"); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
        }
        public void Dispose() { Owner.Dispose(); Directory.Delete(root, recursive: true); }
    }
}
