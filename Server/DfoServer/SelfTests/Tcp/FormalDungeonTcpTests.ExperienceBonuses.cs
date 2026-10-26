using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunExperienceBonusesAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        ExperienceBonusTests.RunCore();
        DatabaseSchemaTests.RunExperienceBonuses();
        DatabaseSchemaTests.RunSettlementProficiency();
        DungeonSettlementTests.RunExperienceBonusStorage(SelfTestCatalogs.Growth(resources.Archive));
        var rules = ExperienceBonusCatalog.Load(resources.Archive);
        That(rules.BlackDiamondPercent == 5 && rules.InternetCafePercent == 20,
            "实际S5A9特权资源黑钻5%、网吧20%，编号与比例来自本版资源");
        await using var fixture = new TcpTestFixture(options, Path.Combine(root,"experience-bonuses"), resources: resources);
        await fixture.StartAsync();
        const int missingItem = int.MaxValue;
        const string missingQuery = """
            SELECT i.item_uid,i.item_core,s.list_type,s.slot_index FROM character_inventory_items i
            JOIN character_inventory_slots s USING(item_uid)
            WHERE i.character_id=$p0 AND json_extract(i.item_core,'$.ItemId')=$p1
            """;
        That(!resources.ItemResources.Items.Contains(missingItem), "缺失模板回归使用未注册的正物品编号，不修改PVF");
        var saved = new List<(string Account, ushort Id, ulong Experience, string MissingState)>();
        foreach (var scenario in new[] { (Item:7440,InDungeon:false),(Item:7377,InDungeon:true) })
        {
            string account = "exp-potion-" + scenario.Item;
            ushort id;
            ulong experience = 0;
            int clearPermits = 0;
            string missingState;
            using (var peer = Open(fixture, account))
            {
                peer.Create(scenario.InDungeon ? "ExpInside" : "ExpBefore");
                using (var owner = new AccountOwners().Acquire(account, () => { }))
                {
                    var repository = new CharacterRepository(fixture.Database);
                    var role = repository.LoadCharacter(owner,0);
                    var after = role.Inventory with
                    {
                        AccountWallet = role.Inventory.AccountWallet.SetContractExpiry(84,DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3600),
                        Items = role.Inventory.Items.Add(new(0,new(InventoryListType.Main,65),
                            InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(scenario.Item),1))).Add(
                            new(0,new(InventoryListType.Equipment,10),new(missingItem,ItemKind.Avatar,0)) { Avatar = new() })
                    };
                    repository.CommitInventory(owner,role,new(role.Inventory,after,"experience_fixture"));
                    missingState = fixture.Snapshot(missingQuery,role.Id,missingItem);
                }
                peer.Select(premiumContracts:true); id = peer.Id;
                if (!scenario.InDungeon) UsePotion();
                EntryTown(peer);
                byte[] map = Enter(peer);
                var targets = new (byte X,byte Y)[] { (1,1),(2,1),(2,0),(3,0),(4,0) };
                var kills = SelfTestCatalogs.KillExperience(resources.Archive);
                var dungeon = SelfTestCatalogs.Dungeons(resources.Archive).Get(3);
                var dgnWeight = DungeonKillExperienceCatalog.GetDungeonWeight(dungeon);
                var named = DungeonKillExperienceCatalog.GetNamedMonsters(dungeon);
                for (int room = 0; room < 6; room++)
                {
                    Load(peer);
                    if (scenario.InDungeon && room == 0) UsePotion();
                    for (int row = 0; row < map[18]; row++)
                    {
                        int at = 19 + row * 22;
                        int monster = checked((int)U32(map,at+8));
                        if (map[at+13] == 9 || monster == 107000906) continue;
                        byte level = checked((byte)(long)fixture.Query("SELECT level FROM characters WHERE id=$p0",id)[0][0]!);
                        byte monsterLevel = map[at+12];
                        uint basis = Game.Dungeons.DungeonKillExperiencePolicy.Calculate(kills.GetExperience(monsterLevel),dgnWeight,
                            kills.GetDifficultyRate(0),kills.GetMonsterRate(map[at+13],named.Contains(monster)),level,monsterLevel);
                        uint growth = basis * 20 / 100;
                        ushort key = U16(map,at+6);
                        var report = Death(key,id,monsterLevel);
                        peer.Send(39,report);
                        var packets = Drain(peer);
                        int permit = Array.FindIndex(packets,packet => packet.Kind == 0 && packet.Opcode == 31);
                        if (permit >= 0)
                        {
                            clearPermits++;
                            int boss = Array.FindIndex(packets,packet => packet.Kind == 0 && packet.Opcode == 115);
                            int experienceNotice = Array.FindIndex(packets,packet => packet.Kind == 0 && packet.Opcode == 37);
                            That(boss >= 0 && boss < permit && permit < experienceNotice,
                                "带秘药的剧情终点先发115/31再发37，避免经验明细挡住客户端结算入口");
                        }
                        var notice = packets.Single(packet => packet.Kind == 0 && packet.Opcode == 37).Body;
                        experience += basis + growth + basis;
                        That(U64(notice,1) == experience && U32(notice,34) == growth && U32(notice,87) == basis,
                            "正式击杀累计XP和契约/秘药分项同时入账，无重复乘算");
                        if (room == 0 && row == 0)
                        {
                            peer.Send(39,report);
                            var repeated = Drain(peer);
                            That(!repeated.Any(packet => packet.Kind == 0 && packet.Opcode == 37) && Experience(fixture,id) == experience,
                                "重复死亡不重复经验、契约或秘药奖励");
                        }
                    }
                    if (room < targets.Length)
                    {
                        byte[] move = new byte[100];move[0]=targets[room].X;move[1]=targets[room].Y;
                        peer.Send(45,move);peer.Expect(1,45,[1]);map=peer.Expect(0,29).Body;
                    }
                }
                That(clearPermits == 1,"客户端已收到唯一结算许可后才发送46，不能由测试强行绕过入口");
                var clear = SelfTestCatalogs.ClearExperience(resources.Archive).Calculate(dungeon,0,99);
                uint contract = clear.BaseExperience * 20 / 100, potion = clear.BaseExperience + clear.RankExperience;
                uint avatar = clear.BaseExperience * 2 / 100;
                byte[] result = DungeonSettlementProtocolTests.Report(id,99);
                peer.Send(46,result);
                var settled = Drain(peer);
                var clearNotice = settled.Single(packet => packet.Kind == 0 && packet.Opcode == 37).Body;
                experience += clear.TotalExperience + contract + potion + avatar;
                That(U64(clearNotice,1) == experience && U32(clearNotice,34) == contract && U32(clearNotice,87) == potion &&
                    settled.Any(packet => packet.Kind == 0 && packet.Opcode == 35) &&
                    fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",id) is [[1L]],
                    "持秘药完整六房可正常结算，含评分基数与契约分开记账，不再被遗漏分项拒绝");
                peer.Send(46,result);Drain(peer);
                That(Experience(fixture,id) == experience,"相同结算恢复不重复发经验");
                var cards = settled.Single(packet => packet.Kind == 0 && packet.Opcode == 35).Body;
                That(U32(cards,12) == avatar && U32(cards,21) == potion && U32(cards,49) == contract &&
                    fixture.Query("SELECT claimed FROM character_dungeon_card_rewards WHERE character_id=$p0",id) is [[0L]],
                    "剧情结算35含实际秘药和契约分项，牌面已冻结但未提前领取");
                peer.Send(69); peer.Expect(1,69,[1]);
                peer.Send(70); That(peer.Expect(1,70).Body.Length == 17,"持秘药剧情结算开放完整八席翻牌资格");
                peer.Send(71,[0,0]);
                var claimed = Drain(peer);
                That(claimed.Any(packet => packet.Kind == 1 && packet.Opcode == 71 && packet.Body.Length == 33 &&
                    packet.Body[0] == 1 && packet.Body[1] == 0 && packet.Body[2] == 255) &&
                    fixture.Query("SELECT claimed FROM character_dungeon_card_rewards WHERE character_id=$p0",id) is [[1L]],
                    "两种用药时机均能经真实69/70/71领取免费牌，不能仅以发出35认定翻牌完成");
                That(fixture.Snapshot(missingQuery,id,missingItem) == missingState,
                    "缺失穿戴模板仍可完整击杀、结算与领取卡牌，原道具UID、核心和槽位保留");
                string afterClaim = fixture.Snapshot();
                peer.Send(71,[0,0]); Drain(peer);
                That(fixture.Snapshot() == afterClaim && Experience(fixture,id) == experience,
                    "重复翻牌不修改库存、领取回执或经验");

                void UsePotion()
                {
                    peer.Send(44,Build(writer => { writer.Write((ushort)65);writer.Write((byte)0);writer.Write(0u);writer.Write(scenario.Item);writer.Write(0); }));
                    var use = Drain(peer);
                    That(use.Any(packet => packet.Kind == 1 && packet.Opcode == 44 && packet.Body[0] == 1) &&
                        fixture.Query("SELECT permille FROM character_exp_bonus WHERE character_id=$p0",id) is [[1000L]] &&
                        fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE character_id=$p0 AND list_type=0 AND slot_index=65",id) is [[0L]],
                        "图外7440或图内7377真实44只消费一件秘药，并提交双倍经验资格");
                }
            }
            saved.Add((account,id,experience,missingState));
        }
        await fixture.StopAsync(); await fixture.StartAsync("restart");
        foreach (var savedRole in saved)
        {
            using var peer = Open(fixture,savedRole.Account);
            peer.Select(premiumContracts:true);
            That(Experience(fixture,savedRole.Id) == savedRole.Experience &&
                fixture.Query("SELECT permille FROM character_exp_bonus WHERE character_id=$p0",savedRole.Id) is [[1000L]] &&
                fixture.Query("SELECT claimed FROM character_dungeon_card_rewards WHERE character_id=$p0",savedRole.Id) is [[1L]] &&
                fixture.Snapshot(missingQuery,savedRole.Id,missingItem) == savedRole.MissingState,
                "重建服务后保持经验、药剂期限、已领取免费牌与缺失模板的原道具，不补发历史分项或卡牌");
        }

        static WirePacket[] Drain(TcpPeer peer)
        {
            peer.SendBarrier();
            var packets = new List<WirePacket>();
            for (int i = 0; i < 128; i++)
            {
                var packet = peer.Read();
                if (packet is { Kind:1,Opcode:1347 }) { Bytes(packet.Body,[1],"奖励后屏障");return [.. packets]; }
                packets.Add(packet);
            }
            throw new InvalidDataException("经验奖励通知未在有界屏障前完成");
        }
    }
}
