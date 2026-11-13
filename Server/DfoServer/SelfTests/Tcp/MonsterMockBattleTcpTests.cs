using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.MonsterMockBattle;
using DfoServer.GameWorld.MonsterMockBattle;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class MonsterMockBattleTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var catalog = MonsterMockBattleCatalog.Load(resources.Archive);
        That(catalog.Cards.Count == 20 && catalog.Worlds.Count == 45 && catalog.Achievements.Length == 21 &&
            catalog.Positions.Length == 18 && catalog.ItemDraws[490013681].Draws.All(row => row.Weight == 0),
            "本版20种卡片、45关卡、21成就及指定召唤池完整读取");
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        foreach (var world in catalog.Worlds.Values)
            That(catalog.Battlefield(dungeons.Get(world.DungeonId)).Mazes is [{ StaticTopology.Supported: true }],
                "全部活动关卡的原清图目标与单战场资源完整");
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-11-05T04:00:00Z"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "monster-mock-battle"), resources: resources, clock: clock,
            monsterMockBattle: true);
        string settings = Path.Combine(fixture.Options.DataDirectory, "MonsterMockBattle");
        Directory.CreateDirectory(settings);
        var configuration = new MockBattleSettings { SpecialCalendarStart = new(2026, 11, 5) };
        File.WriteAllText(Path.Combine(settings, "settings.json"), JsonSerializer.Serialize(configuration));
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        byte[] stateBody = [];
        string beforeFailedEntry = "";
        int avatarId, originalWorn;
        short avatarSlot;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("MockRunner", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
            var primary = repository.Create(owner, candidate);
            repository.Create(owner, candidate with { Name = "MockRunnerB" });
            var items = resources.ItemResources;
            avatarId = resources.Characters.Get(0).DefaultAvatarEquipment[3];
            var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
            var granted = inventory.Grant(primary, [new(avatarId, 1)]);
            That(granted.Accepted, "模拟战刷新夹具发放当前职业可穿时装");
            primary = InventoryService.Commit(repository, owner, primary, granted);
            avatarSlot = primary.Inventory.Items.Single(row => row.Position.List == InventoryListType.Avatar && row.Core.ItemId == avatarId).Position.Index;
            originalWorn = primary.Inventory.Find(new(InventoryListType.Equipment, 3))?.Core.ItemId ?? 0;
            // 实际旧首版空档：只有revision=1、零体力与三小时前时点，不预置新补给字段。
            var oldEmpty = MockBattleAccount.Create(clock.GetUtcNow().AddHours(-3), primary.Name);
            repository.CommitMockBattle(owner, primary, new(oldEmpty, new(oldEmpty, []),
                new(primary.Inventory, primary.Inventory, "mock-legacy-empty-fixture"),
                new ServerEventRepository(fixture.Database).Read(2532), clock.GetUtcNow()));
            fixture.Sql("UPDATE account_monster_mock_battle SET state=json_remove(state,'$.SupplyInitialized','$.CoinMailDay') WHERE account=$p0", owner.Account);
            fixture.Sql("INSERT INTO account_wallets(account) VALUES($p0) ON CONFLICT DO NOTHING", owner.Account);
            fixture.Sql("INSERT INTO account_material_balances VALUES($p0,490013685,5000,0,0,'{}')", owner.Account);
            fixture.Sql("INSERT INTO account_material_balances VALUES($p0,490013681,2,0,0,'{}')", owner.Account);
            fixture.Sql("INSERT INTO account_material_balances VALUES($p0,490013680,1,0,0,'{}')", owner.Account);
        }
        using (var supplyOwner = owners.Acquire("mock-supply", () => { }))
        {
            new AuthenticationService(new AuthenticationRepository(fixture.Database)).Create(supplyOwner.Account, NativeLoginWire.Digest);
            var supplyRole = repository.Create(supplyOwner, CharacterCreation.Prepare("MockSupply", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
            var items = resources.ItemResources;
            var service = new MonsterMockBattleService(repository, catalog, configuration, items.Items, items.GoldLimits,
                items.EpicPieces, new ServerEventService(new ServerEventRepository(fixture.Database), clock), clock);
            var plan = service.PrepareRefresh(supplyOwner, supplyRole)!;
            string beforeSupply = EntryState();
            fixture.Sql("""
                CREATE TRIGGER mock_supply_failure BEFORE INSERT ON account_monster_mock_battle
                WHEN NEW.account='mock-supply'
                BEGIN SELECT RAISE(ABORT,'mock supply failure'); END
                """);
            Reject<SqliteException>(() => service.Commit(supplyOwner, supplyRole, plan));
            That(EntryState() == beforeSupply && repository.ReadMockBattle(supplyOwner) is null,
                "硬币钱包真实写后卡册SQL失败，补给资格、全部资产和UID序列共同回滚");
            fixture.Sql("DROP TRIGGER mock_supply_failure");
            var supplied = service.Commit(supplyOwner, supplyRole, plan);
            That(supplied.Account is { SupplyInitialized: true, Hearts: 30, Coins: 100, Revision: 1 } &&
                supplied.Character.Inventory.AccountWallet.Material(490013685).Balance == 100 &&
                fixture.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_account=$p0", supplyOwner.Account) is [[0L]],
                "同owner重试仅产生30点体力与当天100币，不补发初始硬币或生成邮件");
            That(service.PrepareRefresh(supplyOwner, supplied.Character) is null,
                "同owner初始化重放不再次提交补给或硬币");
        }
        using (var rivalOwner = owners.Acquire("mock-rival", () => { }))
        {
            var rival = repository.Create(rivalOwner, CharacterCreation.Prepare("MockRival", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
            var state = MockBattleAccount.Create(clock.GetUtcNow(), rival.Name);
            state = state with { Snapshot = state.Snapshot with { Cards = state.Snapshot.Cards.SetItem(0, new(0, 1, 4, 0, 0)) } };
            repository.CommitMockBattle(rivalOwner, rival, new(state, new(state, []),
                new(rival.Inventory, rival.Inventory, "mock-opponent-fixture"),
                new ServerEventRepository(fixture.Database).Read(2532), clock.GetUtcNow()));
            fixture.Sql("INSERT INTO accounts(account_id,salt,password_hash,iterations) VALUES($p0,zeroblob(16),zeroblob(32),1)", rivalOwner.Account);
            fixture.Sql("""
                INSERT INTO account_adventures(account,revision,name,created_date,level,experience,remainder,
                  capsule_experience,brave_experience,points,month)
                VALUES($p0,0,'RivalGroup',20261105,1,0,'0',0,0,zeroblob(20),202611)
                """, rivalOwner.Account);
        }
        await fixture.StartAsync();
        using (var first = OpenBattle())
        {
            first.Select();
            CommonMaterial(first.InitialAccountMaterialLists, 13, 490013681, 2);
            var initialSupply = Request(first, 1736, Command(7, 1));
            That(stateBody.Length == 980 && U32(stateBody) == 5100 && Material(490013685) == 5100 &&
                U32(stateBody, 4) == 30 && U32(stateBody, 8) == 0 && U32(stateBody, 16) == 0 && DailyMails() == 0,
                "旧空档入城补足30体力及当日100币，钱包5100且无额外初始500或邮件");
            CoinRefresh(initialSupply, 5100);
            That(fixture.Query("SELECT json_extract(state,'$.SupplyInitialized'),json_extract(state,'$.CoinMailDay') FROM account_monster_mock_battle WHERE account=$p0",
                fixture.Options.DevelopmentAccount) is [[1L, long day]] && day == Day(), "旧空档迁移保存补给资格和当天日戳");
            var duplicate = Request(first, 1736, Command(7, 1));
            That(Material(490013685) == 5100 && DailyMails() == 0 && duplicate.All(row => row.Opcode != 563),
                "同日再次刷新不重复增加硬币或发余额差量");
            That(duplicate.Count(row => row is { Kind: 0, Opcode: 1766 }) == 1,
                "状态相同的显式面板操作仍完整刷新一次1766");
            var beforeAppearance = stateBody.ToArray();
            for (int repeat = 0; repeat < 2; repeat++)
            {
                MoveAvatar(first, 1, avatarSlot, avatarId, 3, 3, originalWorn);
                MoveAvatar(first, 3, 3, avatarId, 1, avatarSlot, originalWorn);
            }
            Bytes(stateBody, beforeAppearance, "反复穿脱时装不改变模拟战卡册和硬币体力");
        }
        using (var alternate = OpenBattle())
        {
            alternate.Select(1);
            CommonMaterial(alternate.InitialAccountMaterialLists, 13, 490013681, 2);
            Request(alternate, 1736, Command(7, 1));
            That(DailyMails() == 0 && U32(stateBody, 4) == 30 && Material(490013685) == 5100,
                "同日换角恢复账号补给，不重新发邮件或增加体力");
        }
        using (var peer = OpenBattle())
        {
            peer.Select();
            var matching = Request(peer, 1736, Command(7, 1));
            That(U32(Ack(matching), 1) == 7 && matching.All(row => row.Opcode is not (1768 or 1769)),
                "玩家匹配暂不开放，失败应答不发布对手列表");
            var emptyMatch = Request(peer, 1736, Command(7, 25));
            That(U32(Ack(emptyMatch), 1) == 7 && emptyMatch.All(row => row.Opcode is not (1768 or 1769)),
                "任何星数的玩家匹配均不开放");
            Request(peer, 1736, Command(7, 1));
            That(stateBody.Length == 980 && U32(stateBody) == 5100 && Material(490013685) == 5100 && DailyMails() == 0,
                "同日重连保留已领取共享硬币，不重复发放日补给");
            That(U32(stateBody, 4) == 30 && U32(stateBody, 8) == 0 && U32(stateBody, 16) == 0,
                "卡册保留30点满额体力及原生零状态标记");
            SummonReceipt(Request(peer, 1736, Command(0, 1)), 0, 0);
            SummonReceipt(Request(peer, 1736, Command(0, 5)), 0, 1, 2, 3, 4, 5);
            That(Material(490013685) == 4550 && Cards() == 6 && U32(stateBody, 8) == 2,
                "真实单抽与五连扣除账号硬币并保存全部六卡，首次召唤完成位同步置2");
            Request(peer, 1736, Command(4, 0, 1));
            var rejected = Request(peer, 1736, Command(2, 0));
            That(U32(Ack(rejected), 1) == 7 && Material(490013685) == 4550 && Cards() == 6,
                "锁卡放归被拒且资产、卡册保持");
            Request(peer, 1736, Command(4, 0, 0));
            var firstTicket = Request(peer, 1736, Command(1, 490013681, 3));
            SummonReceipt(firstTicket, 1, 6);
            ushort ticketSlot = CommonMaterial(firstTicket, 1681, 490013681, 1);
            var lastTicket = Request(peer, 1736, Command(1, 490013681, 3));
            SummonReceipt(lastTicket, 1, 7);
            CommonMaterial(lastTicket, 1681, -1, 0, ticketSlot);
            That(Material(490013681) == 0 && stateBody[138 + 6 * 8] == 7 && stateBody[138 + 7 * 8] == 7 &&
                stateBody[139 + 6 * 8] == 1 && stateBody[139 + 7 * 8] == 1,
                "指定券实际消费两份共享材料并按池内第3项获得哥布林卡");
            Request(peer, 1736, Command(3, 6, 7));
            That(Material(490013685) == 4450 && stateBody[139 + 6 * 8] == 2 && stateBody[138 + 7 * 8] == 255,
                "真实合成扣100硬币并删除源卡，目标升二星");
            byte[] attribute = new byte[20]; attribute[0] = 1;
            Request(peer, 1736, Command(6, 6, 2, attribute));
            That(U32(stateBody, 190) == 8, "真实属性选择落入第二星右侧属性位");
            byte[] formation = Enumerable.Repeat((byte)255, 20).ToArray();
            formation[0] = 6; formation[1] = 4; formation[17] = formation[18] = formation[19] = 0;
            Request(peer, 1736, Command(5, payload: formation));
            That(stateBody[140 + 6 * 8] == 4, "原生阵型正文实际保存卡槽6至阵位4");
            var inLineupRelease = Request(peer, 1736, Command(2, 6));
            That(U32(Ack(inLineupRelease), 1) == 7 && stateBody[138 + 6 * 8] == 7 && stateBody[140 + 6 * 8] == 4,
                "上阵卡片放归被拒且阵型与卡槽保持");
            var summonAchievement = Request(peer, 1736, Command(9, 0));
            long claimed = Material(490013685);
            That(claimed == 4550 && stateBody[939] == 1, "成就硬币与领取位同事务直接进入钱包");
            CoinRefresh(summonAchievement, claimed);
            Request(peer, 1736, Command(9, 0));
            That(Material(490013685) == claimed &&
                fixture.Query("SELECT count(*) FROM mailbox_messages WHERE delivery_key=$p0", $"monster-mock-battle-achievement:{fixture.Options.DevelopmentAccount}:0") is [[0L]],
                "重复成就领取不再次发币或投递邮件");
            SummonReceipt(Request(peer, 1736, Command(1, 490013680, 1)), 1, 7);
            That(stateBody[974] == 1, "获得使徒卡片立即推进使徒成就，独立于搜索切磋胜负");
            var released = Request(peer, 1736, Command(2, 0));
            Bytes(Ack(released), [1, 0, 0, 0, 0, 2, 0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 0, 0],
                "放归成功回执返回零结果且不携带获得卡槽");
            That(Material(490013685) == claimed && Cards() == 7 && stateBody[138] == 255 && released.All(row => row.Opcode != 563),
                "解锁且未上阵卡片放归清空目标卡槽，硬币与共享材料保持不变");
            var repeatRelease = Request(peer, 1736, Command(2, 0));
            That(U32(Ack(repeatRelease), 1) == 7 && Cards() == 7 && stateBody[138] == 255,
                "已放归空槽重复放归被拒且卡册保持");
            Request(peer, 1736, Command(9, 18));
            uint apostleAchievement = Mail($"monster-mock-battle-achievement:{fixture.Options.DevelopmentAccount}:18");
            That(Material(490013681) == 0 && stateBody[975] == 1, "使徒成就先保存领取位和邮件，不直接写入共享材料");
            MailContents(apostleAchievement, [(490013681, 1)], peer.Id);
            var ticketClaim = Claim(peer, apostleAchievement);
            CommonMaterial(ticketClaim, 14, 490013681, 1, ticketSlot);
            That(Material(490013681) == 1, "使徒成就邮件实际领取共通指定券");

            uint dungeon = checked((uint)catalog.Worlds[0].DungeonId);
            var entered = Enter(peer, dungeon, uint.MaxValue);
            var map = entered.Single(row => row.Opcode == 29).Body;
            That(U32(entered.Single(row => row.Opcode == 28).Body) == dungeon && map[18] == 2,
                "剧情入场沿原28/29链只发送资源敌人，不混入召唤卡或支援兵");
            Request(peer, 46, DungeonSettlementProtocolTests.Report(peer.Id, 99));
            That(Settlements() == 0, "未加载、未击杀不能以46伪造通关");
            var loaded = Request(peer, 37, new byte[8]);
            var summoned = entered.Single(row => row.Opcode == 1767).Body;
            That(Array.FindIndex(entered, row => row.Opcode == 28) < Array.FindIndex(entered, row => row.Opcode == 1766) &&
                Array.FindIndex(entered, row => row.Opcode == 1766) < Array.FindIndex(entered, row => row.Opcode == 1767) &&
                Array.FindIndex(entered, row => row.Opcode == 1767) < Array.FindIndex(entered, row => row.Opcode == 29) &&
                loaded.All(row => row.Opcode != 1767), "28后地图加载前完成卡组预登记以启用谋略战过渡，37不重复生成");
            That(U32(stateBody, 4) == 30 && U32(stateBody, 16) == 0,
                "首次挑战免费，沿用真实初始化30体力并保持满额零倒计时");
            That(summoned.Length == 19 && summoned[0] == 1 && U32(summoned, 10) == 0 && U32(summoned, 14) == 0 && summoned[18] == 4,
                "1767预登记友方卡及阵位，普通生成品级0不能填等级95");
            That(stateBody[138 + 6 * 8] == 7 && U32(summoned, 1) == catalog.Cards[7].MonsterId && U32(summoned, 1) != 7,
                "1766保留卡片7，1767首字段映射为该卡的怪物资源编号，不能发送卡片号或卡槽6");
            for (int index = 0; index < map[18]; index++)
            {
                int offset = 19 + index * 22;
                Request(peer, 39, FormalDungeonTcpTests.Death(U16(map, offset + 6), peer.Id, map[offset + 12]));
            }
            var cleared = Request(peer, 46, DungeonSettlementProtocolTests.Report(peer.Id, 99));
            That(cleared.Any(row => row.Opcode == 35) && Settlements() == 1 && Material(490013685) == claimed + 120 &&
                stateBody[37] == 1 && fixture.Query("SELECT state FROM account_monster_mock_battle_runs WHERE account=$p0", fixture.Options.DevelopmentAccount) is [[1L]],
                "完整死亡事实后通关：硬币直接到账，非币邮件、关卡位与本局回执同事务保存");
            CoinRefresh(cleared, claimed + 120);
            uint firstVictory = VictoryMail();
            MailContents(firstVictory, catalog.Worlds[0].FirstReward.Items.Select(row => (row.ItemId, row.Count))
                .ToArray(), peer.Id);
            long mails = VictoryMails();
            That(mails == 1 && fixture.Query("SELECT count(*) FROM character_dungeon_card_rewards WHERE character_id=$p0", peer.Id) is [[0L]],
                "活动奖励走真实邮件，不额外发普通副本翻牌");
            Request(peer, 46, DungeonSettlementProtocolTests.Report(peer.Id, 99));
            That(Material(490013685) == claimed + 120 && Settlements() == 1 && VictoryMails() == mails,
                "同实例46重放不重复发币或邮件");
            Request(peer, 42, []);
            var firstVictoryClaim = Claim(peer, firstVictory);
            CommonMaterial(firstVictoryClaim, 14, MonsterMockBattleService.RewardMaterialItemId,
                catalog.Worlds[0].FirstReward.Items.Single(row => row.ItemId == MonsterMockBattleService.RewardMaterialItemId).Count);
            That(Material(490013685) == claimed + 120 &&
                Material(MonsterMockBattleService.RewardMaterialItemId) == catalog.Worlds[0].FirstReward.Items.Single(row => row.ItemId == MonsterMockBattleService.RewardMaterialItemId).Count,
                "回城后真实CMD95整封领取首次120硬币、谋略家印章及原资源物品");

            byte[] name = new byte[20]; System.Text.Encoding.UTF8.GetBytes("RivalGroup").CopyTo(name, 0);
            var search = Request(peer, 1736, Command(8, payload: name));
            That(U32(Ack(search), 1) == 7 && search.All(row => row.Opcode is not (1768 or 1769)),
                "玩家搜索切磋暂不开放且不发布对手");
            foreach (int world in new[] { 35, 40 })
            {
                uint disabled = checked((uint)catalog.Worlds[world].DungeonId);
                That(Request(peer, 15, Build(writer => writer.Write(disabled))).Any(row => row is { Kind: 1, Opcode: 15, Body: [0, ..] }) &&
                    Request(peer, 16, Selection(disabled, 1)).Any(row => row is { Kind: 1, Opcode: 16, Body: [0, ..] }) &&
                    U32(stateBody, 4) == 30, "PvP和切磋均拒绝15及绕行16，不扣体力或建立对战");
            }
            var failedStory = Enter(peer, dungeon, uint.MaxValue);
            var failedMap = failedStory.Single(row => row.Opcode == 29).Body;
            var friendly = failedStory.Single(row => row.Opcode == 1767).Body;
            Request(peer, 37, new byte[8]);
            That(U32(stateBody, 4) == 22 && U32(stateBody, 16) == 300, "剧情重复挑战扣8体力并启动五分钟恢复");
            long beforeDefeat = Material(490013685);
            Request(peer, 39, FormalDungeonTcpTests.Death(checked((ushort)U32(friendly, 5)), peer.Id, 95));
            for (int index = 0; index < failedMap[18]; index++)
                Request(peer, 39, FormalDungeonTcpTests.Death(U16(failedMap, 19 + index * 22 + 6), peer.Id, 95));
            Request(peer, 46, DungeonSettlementProtocolTests.Report(peer.Id, 99));
            That(Material(490013685) == beforeDefeat && Settlements() == 1 && U32(stateBody, 20) == 0,
                "剧情友军全灭后迟到敌方死亡及46不能变成胜利或发奖");
            Request(peer, 42, []);
            for (uint hearts = 23; hearts <= 24; hearts++)
            {
                await clock.WaitForOneShotTimerAsync(clock.GetUtcNow().AddMinutes(5));
                byte[] expectedRecovery = stateBody.ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(expectedRecovery.AsSpan(4), hearts);
                // 不发送请求或屏障，直接接收独立时钟连续两次五分钟的恢复通知。
                clock.Advance(TimeSpan.FromMinutes(5));
                stateBody = peer.Expect(0, 1766, expectedRecovery).Body;
                That(U32(stateBody, 4) == hearts && U32(stateBody, 16) == 300 &&
                    fixture.Query("SELECT json_extract(state,'$.Snapshot.Parameters6[1]'),json_extract(state,'$.HeartAt') FROM account_monster_mock_battle WHERE account=$p0",
                        fixture.Options.DevelopmentAccount) is [[long stored, long heartAt]] && stored == hearts && heartAt == clock.GetUtcNow().ToUnixTimeSeconds(),
                    "无客户端请求每五分钟只恢复一点体力，完整1766与持久卡册时点一致");
            }
            await clock.WaitForOneShotTimerAsync(clock.GetUtcNow().AddMinutes(5));
            string recoveredState = fixture.Snapshot("SELECT revision,state FROM account_monster_mock_battle WHERE account=$p0", fixture.Options.DevelopmentAccount);
            var repeatedRecovery = Request(peer, 1736, Command(7, 1));
            That(repeatedRecovery.Count(row => row is { Kind: 0, Opcode: 1766 }) == 1 && U32(stateBody, 4) == 24 &&
                fixture.Snapshot("SELECT revision,state FROM account_monster_mock_battle WHERE account=$p0", fixture.Options.DevelopmentAccount) == recoveredState,
                "连续恢复后已排下次五分钟时钟，同一时点主动读取不重复恢复或增加revision");
            That(fixture.Query("SELECT level,experience FROM characters WHERE id=$p0", peer.Id) is [[1L, 0L]],
                "卡片战斗不授予普通角色经验");

            var special = catalog.Worlds[20];
            uint lockedSpecial = checked((uint)catalog.Worlds[21].DungeonId);
            long beforeSpecial = Material(490013685);
            var rejectedSelection = Request(peer, 15, Build(writer => writer.Write(lockedSpecial)));
            var rejectedEntry = Request(peer, 16, Selection(lockedSpecial, uint.MaxValue));
            That(rejectedSelection.Any(row => row is { Kind: 1, Opcode: 15, Body: [0, ..] }) &&
                rejectedSelection.All(row => row.Opcode != 27) &&
                rejectedEntry.Any(row => row is { Kind: 1, Opcode: 16, Body: [0, ..] }) &&
                U32(stateBody, 4) == 24 && Settlements() == 1,
                "未开放或前置不足的关卡在15阶段直接拒绝且不发27选图通知，不把角色留在普通选图界面");
            var specialMap = Enter(peer, checked((uint)special.DungeonId), uint.MaxValue).Single(row => row.Opcode == 29).Body;
            Request(peer, 37, new byte[8]);
            for (int index = 0; index < specialMap[18]; index++)
            {
                int offset = 19 + index * 22;
                Request(peer, 39, FormalDungeonTcpTests.Death(U16(specialMap, offset + 6), peer.Id, specialMap[offset + 12]));
            }
            var specialCleared = Request(peer, 46, DungeonSettlementProtocolTests.Report(peer.Id, 99));
            That(Settlements() == 2 && Material(490013685) == beforeSpecial + special.RepeatReward.Coins &&
                stateBody[33 + 20 * 5 + 4] == 1 && stateBody[938 + 9 * 2] == 1,
                "特殊关卡真实清怪结算，硬币立即到账并推进进度和成就");
            CoinRefresh(specialCleared, beforeSpecial + special.RepeatReward.Coins);
            uint specialVictory = VictoryMail();
            MailContents(specialVictory, special.RepeatReward.Items.Select(row => (row.ItemId, row.Count))
                .ToArray(), peer.Id);
            Request(peer, 42, []);
            Claim(peer, specialVictory);
            That(Material(490013685) == beforeSpecial + special.RepeatReward.Coins && U32(stateBody, 4) == 14,
                "特殊关非硬币邮件领取不重复加币，10点体力费用保持");
            var events = new ServerEventService(new ServerEventRepository(fixture.Database), clock);
            events.Override(2532, false);
            long closedCoins = Material(490013685);
            That(U32(Ack(Request(peer, 1736, Command(0, 1))), 1) != 0 && Material(490013685) == closedCoins,
                "GM关闭活动后拒绝抽卡且不扣共享材料");
            That(Request(peer, 15, Build(writer => writer.Write(dungeon))).Any(row => row is { Kind: 1, Opcode: 15, Body: [0, ..] }),
                "关闭活动同时拒绝专用选图");
            events.Override(2532, true);
            beforeFailedEntry = EntryState();
            Request(peer, 15, Build(writer => writer.Write(dungeon)));
            fixture.Sql("CREATE TRIGGER mock_entry_failure BEFORE INSERT ON account_monster_mock_battle_runs BEGIN SELECT RAISE(ABORT,'mock entry failure'); END");
            peer.Send(16, Selection(dungeon, uint.MaxValue));
            peer.Closed();
            That(EntryState() == beforeFailedEntry, "卡册扣体力与尝试号真实写后SQL失败，入场回执和全部资产一起回滚");
        }
        fixture.Sql("DROP TRIGGER mock_entry_failure");
        await fixture.StopAsync();
        File.Delete(Path.Combine(settings, "settings.json"));
        clock.Advance(TimeSpan.FromHours(2));
        await fixture.StartAsync("cold-service");
        using (var fresh = OpenBattle())
        {
            fresh.Select(1);
            CommonMaterial(fresh.InitialAccountMaterialLists, 13, 490013681, 1);
            That(!File.Exists(Path.Combine(settings, "settings.json")), "无独立设置文件时活动默认开放，仍从原库恢复卡册");
            Request(fresh, 1736, Command(7, 1));
            That(stateBody[138] == 255 && stateBody[139 + 6 * 8] == 2 && stateBody[140 + 6 * 8] == 4 && U32(stateBody, 190) == 8 && stateBody[37] == 1 &&
                U32(stateBody, 20) == 0 && U32(stateBody, 4) == 30 && U32(stateBody, 8) == 2 &&
                U32(stateBody, 16) == 0 && DailyMails() == 0,
                "服务停止两小时后换角恢复完整卡册，离线体力封顶30且同日不重复日邮件");
            long beforeNextDay = Material(490013685);
            clock.Advance(TimeSpan.FromDays(1));
            var nextDay = Request(fresh, 1736, Command(7, 1));
            That(DailyMails() == 0 && Material(490013685) == beforeNextDay + 100 && U32(stateBody, 4) == 30,
                "次日100直接到账，不发送邮件或累积满额体力");
            CoinRefresh(nextDay, beforeNextDay + 100);
            Request(fresh, 1736, Command(7, 1));
            That(DailyMails() == 0 && Material(490013685) == beforeNextDay + 100,
                "次日日币同日再次读卡册不重复发放");
            var fallbackSpecial = Enter(fresh, checked((uint)catalog.Worlds[20].DungeonId), uint.MaxValue);
            That(fallbackSpecial.Any(row => row.Opcode == 28) && fallbackSpecial.Any(row => row.Opcode == 29) &&
                fallbackSpecial.Any(row => row.Opcode == 1767) && U32(stateBody, 4) == 20,
                "无settings.json且处于2026年时，剧情模式最后一项异次元裂缝直接进入谋略战战场而非停在选图");
            Request(fresh, 37, new byte[8]);
            Request(fresh, 42, []);
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "完整活动链后数据库完整");

        TcpPeer OpenBattle()
        {
            var peer = Open(fixture);
            peer.ExpectedActiveEvents = [..peer.ExpectedActiveEvents, 2532];
            peer.ExpectedEventValues = new Dictionary<ushort, IReadOnlyList<uint>>
            {
                [2532] = [0, 30, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
            };
            return peer;
        }
        void CoinRefresh(WirePacket[] packets, long balance)
        {
            var update = packets.LastOrDefault(row => row is { Kind: 0, Opcode: 563 });
            That(update is not null, "硬币直接到账必须发送账号材料余额更新563");
            Bytes(update!.Body, Build(writer =>
            {
                writer.Write((byte)1); writer.Write((byte)1); writer.Write(490013685); writer.Write(checked((int)balance));
            }), "563完整硬币余额更新，模板及int32余额逐字节正确");
            CommonMaterial(packets, 14, 490013685, checked((int)balance));
            That(U32(stateBody) == balance, "563活动道具背包余额与1766活动面板即时一致");
        }
        void SummonReceipt(WirePacket[] packets, uint operation, params byte[] slots)
        {
            byte[] expected = new byte[19];
            expected[0] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(5), operation);
            expected.AsSpan(9, 5).Fill(255);
            for (int index = 0; index < slots.Length; index++)
            {
                expected[9 + index] = slots[index];
                expected[14 + index] = stateBody[139 + slots[index] * 8];
                That(expected[14 + index] is >= 1 and <= 5, "实际获得卡片有合法一至五星");
            }
            Bytes(Ack(packets), expected, "召唤完整回执的展示星级与此前1766实际卡册一致，空位不显示新卡");
            That(Array.FindIndex(packets, row => row is { Kind: 0, Opcode: 1766 }) <
                Array.FindIndex(packets, row => row is { Kind: 1, Opcode: 1736 }), "完整卡册先安装，再开启获得画面");
        }
        ushort CommonMaterial(WirePacket[] packets, ushort opcode, int itemId, int balance, ushort? expectedSlot = null)
        {
            var body = packets.Last(row => row.Kind == 0 && row.Opcode == opcode && row.Body is [45, ..]).Body;
            That(body.Length == 3 + U16(body, 1) * 123, "活动道具背包使用完整123B原生材料行");
            int offset = Enumerable.Range(0, U16(body, 1)).Select(index => 3 + index * 123)
                .Single(at => U32(body, at + 2) == unchecked((uint)itemId) &&
                    (!expectedSlot.HasValue || U16(body, at) == expectedSlot.Value));
            ushort slot = U16(body, offset);
            byte[] expected = new byte[123];
            BinaryPrimitives.WriteUInt16LittleEndian(expected, slot);
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(2), itemId);
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(6), balance);
            Bytes(body.AsSpan(offset, 123).ToArray(), expected, "券和硬币投影真实绝对余额，耗尽清原槽且不生成物理UID");
            int walletUpdate = Array.FindIndex(packets, row => row is { Kind: 0, Opcode: 563 });
            That(opcode == 13 || walletUpdate >= 0 && walletUpdate <
                Array.FindIndex(packets, row => ReferenceEquals(row.Body, body)), "账号钱包先更新，随后同步活动背包显示");
            return slot;
        }
        WirePacket[] Request(TcpPeer peer, ushort opcode, byte[] body)
        {
            peer.Send(opcode, body); peer.SendBarrier();
            var rows = new List<WirePacket>();
            for (int index = 0; index < 200; index++)
            {
                var packet = peer.Read();
                if (packet is { Kind: 1, Opcode: 1347 }) { Bytes(packet.Body, [1], "活动请求后续屏障"); return rows.ToArray(); }
                That(packet.Kind == 0 || packet.Kind == 1 && (packet.Opcode == opcode || opcode == 46 && packet.Opcode == 905),
                    "活动请求仅包含本请求应答及结算沿用的恶魔服务状态");
                if (packet is { Kind: 0, Opcode: 1766 }) { That(packet.Body.Length == 980, "1766完整状态长度"); stateBody = packet.Body; }
                rows.Add(packet);
            }
            throw new InvalidOperationException("活动响应超过有界预算");
        }
        void MoveAvatar(TcpPeer peer, byte sourceList, short sourceSlot, int sourceId, byte targetList, short targetSlot, int targetId)
        {
            var packets = Request(peer, 19, Build(writer =>
            {
                writer.Write(sourceList); writer.Write(sourceSlot); writer.Write(sourceId); writer.Write(1);
                writer.Write(targetList); writer.Write(targetSlot); writer.Write(targetId);
                writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
            }));
            That(packets.Single(row => row is { Kind: 1, Opcode: 19 }).Body[0] == 1,
                "真实CMD19时装穿脱成功");
            short bagSlot = sourceList == 1 ? sourceSlot : targetSlot;
            int returnedId = sourceList == 1 ? targetId : sourceId;
            var deltas = packets.Where(row => row is { Kind: 0, Opcode: 1681 }).ToArray();
            That(deltas.Length == (returnedId == 0 ? 1 : 2), "时装穿脱只清变化槽，需要返回物品时再完整回填一次");
            for (int index = 0; index < deltas.Length; index++)
            {
                byte[] body = deltas[index].Body;
                That(body.Length == 168 && body[0] == 1 && U16(body, 1) == 1 && U16(body, 3) == bagSlot &&
                    U32(body, 5) == (index == 0 ? uint.MaxValue : checked((uint)returnedId)) &&
                    U32(body, 126) == 30 && U32(body, 160) == 4,
                    "时装差量严格匹配Avatar、单槽、清空/返回模板及完整165B含双RAW条目");
            }
            var appearance = packets.Where(row => row is { Kind: 0, Opcode: 2 }).ToArray();
            That(packets.All(row => row is not { Kind: 0, Opcode: 13 or 14 }) &&
                appearance.Length == 2 && appearance[0].Body[0] == 0 && appearance[1].Body[0] == 1 &&
                Array.IndexOf(packets, packets.Single(row => row is { Kind: 1, Opcode: 19 })) < Array.IndexOf(packets, deltas[0]) &&
                Array.IndexOf(packets, deltas[^1]) < Array.IndexOf(packets, appearance[0]),
                "时装穿脱按ACK、静默局部差量、Actor、Player顺序发布，不重建全量或发送普通获得差量");
            That(packets.All(row => row is not { Kind: 0, Opcode: 1766 }),
                "背包快照变化但完整模拟战状态相同，不重复发送1766");
        }
        WirePacket[] Enter(TcpPeer peer, uint dungeon, uint opponent)
        {
            var selection = Request(peer, 15, Build(writer => writer.Write(dungeon)));
            That(selection.Any(row => row is { Kind: 1, Opcode: 15, Body: [1] }), "活动15建立真实选图上下文");
            return Request(peer, 16, Selection(dungeon, opponent));
        }
        byte[] Selection(uint dungeon, uint opponent) => Build(writer =>
        {
            writer.Write(dungeon); writer.Write((byte)1); writer.Write((ushort)0); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write(ushort.MaxValue); writer.Write(0u); writer.Write((byte)0); writer.Write(0u); writer.Write((byte)0); writer.Write(opponent);
        });
        string EntryState() => JsonSerializer.Serialize(new
        {
            Cards = fixture.Query("SELECT * FROM account_monster_mock_battle"),
            Runs = fixture.Query("SELECT * FROM account_monster_mock_battle_runs ORDER BY opened_at,run_id"),
            Attempts = fixture.Query("SELECT * FROM character_dungeon_state ORDER BY character_id"),
            Characters = fixture.Query("SELECT revision,level,experience FROM characters ORDER BY id"),
            Materials = fixture.Query("SELECT * FROM account_material_balances ORDER BY account,item_id"),
            Mails = fixture.Query("SELECT * FROM mailbox_messages ORDER BY mail_id"),
            Attachments = fixture.Query("SELECT * FROM mailbox_attachments ORDER BY attachment_id"),
            Items = fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid"),
            Slots = fixture.Query("SELECT * FROM character_inventory_slots ORDER BY item_uid"),
            Sequences = fixture.Query("SELECT * FROM sqlite_sequence ORDER BY name")
        });
        int Day() => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(8)).DateTime.AddHours(-6)).DayNumber;
        long DailyMails() => Convert.ToInt64(fixture.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_account=$p0 AND delivery_key LIKE 'monster-mock-battle-daily:%'",
            fixture.Options.DevelopmentAccount).Single()[0]);
        long VictoryMails() => Convert.ToInt64(fixture.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_account=$p0 AND delivery_key LIKE 'monster-mock-battle:%'",
            fixture.Options.DevelopmentAccount).Single()[0]);
        uint Mail(string key) => Convert.ToUInt32(fixture.Query("SELECT mail_id FROM mailbox_messages WHERE delivery_key=$p0", key).Single()[0]);
        uint VictoryMail() => Convert.ToUInt32(fixture.Query("SELECT mail_id FROM mailbox_messages WHERE recipient_account=$p0 AND delivery_key LIKE 'monster-mock-battle:%' ORDER BY mail_id DESC LIMIT 1",
            fixture.Options.DevelopmentAccount).Single()[0]);
        void MailContents(uint mailId, IReadOnlyList<(int Item, int Count)> expected, ushort recipient)
        {
            That(fixture.Query("SELECT recipient_id,expires_at-created_at FROM mailbox_messages WHERE mail_id=$p0", mailId) is [[long actual, 1296000L]] && actual == recipient,
                "活动奖励真实邮件发给当前角色且保存15日");
            var attachments = fixture.Query("""
                SELECT json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value')
                FROM mailbox_attachments a JOIN character_inventory_items i ON i.item_uid=a.item_uid
                WHERE a.mail_id=$p0 AND a.claimed=0 ORDER BY a.ordinal
                """, mailId).Select(row => (Item: Convert.ToInt32(row[0]), Count: Convert.ToInt32(row[1]))).OrderBy(row => row.Item);
            That(attachments.SequenceEqual(expected.OrderBy(row => row.Item)), "活动邮件完整附件与官方奖励数量一致");
        }
        WirePacket[] Claim(TcpPeer peer, uint mailId)
        {
            var ids = fixture.Query("SELECT attachment_id FROM mailbox_attachments WHERE mail_id=$p0 AND claimed=0 ORDER BY ordinal", mailId)
                .Select(row => Convert.ToUInt32(row[0])).ToArray();
            var result = Request(peer, 95, Build(writer => { writer.Write(1); writer.Write(mailId); }));
            Bytes(result.Single(row => row is { Kind: 1, Opcode: 95 }).Body, Build(writer =>
            {
                writer.Write((byte)1); writer.Write(ids.Length);
                foreach (uint id in ids) { writer.Write(mailId); writer.Write(0x40000000u | id); writer.Write(0); }
            }), "真实CMD95整封领取返回全部附件对象的完整成功回执");
            That(fixture.Query("SELECT count(*) FROM mailbox_attachments WHERE mail_id=$p0 AND claimed=0", mailId) is [[0L]],
                "整封领取后所有活动附件均已持久标为领取");
            Request(peer, 1736, Command(7, 1));
            return result;
        }
        long Material(int id) => fixture.Query("SELECT balance FROM account_material_balances WHERE account=$p0 AND item_id=$p1",
            fixture.Options.DevelopmentAccount, id) is [[long balance]] ? balance : 0;
        int Cards() => Enumerable.Range(0, 100).Count(index => stateBody[138 + index * 8] != 255);
        long Settlements() => Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts").Single()[0]);
        byte[] Ack(WirePacket[] packets) => packets.Single(row => row is { Kind: 1, Opcode: 1736 }).Body;
    }

    static byte[] Command(uint operation, uint argument0 = 0, uint argument1 = 0, byte[]? payload = null)
    {
        byte[] body = new byte[45];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), operation);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(17), argument0);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(21), argument1);
        payload?.CopyTo(body, 25);
        return body;
    }
}
