using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
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
        DungeonSettlementTests.RunExperienceBonusStorage(SelfTestCatalogs.Growth(resources.Archive));
        var rules = ExperienceBonusCatalog.Load(resources.Archive);
        That(rules.BlackDiamondPercent == 5 && rules.InternetCafePercent == 20,
            "实际S5A9特权资源黑钻5%、网吧20%，编号与比例来自本版资源");
        await using var fixture = new TcpTestFixture(options, Path.Combine(root,"experience-bonuses"), resources: resources);
        await fixture.StartAsync();
        ushort id;
        ulong experience = 0;
        using (var peer = Open(fixture))
        {
            peer.Create("ExpBonuses");
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var repository = new CharacterRepository(fixture.Database);
                var role = repository.LoadCharacter(owner,0);
                var after = role.Inventory with
                {
                    AccountWallet = role.Inventory.AccountWallet.SetContractExpiry(84,DateTimeOffset.UtcNow.ToUnixTimeSeconds()+3600),
                    Items = role.Inventory.Items.Add(new(0,new(InventoryListType.Main,65),
                        InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(7440),1)))
                };
                repository.CommitInventory(owner,role,new(role.Inventory,after,"experience_fixture"));
            }
            peer.Select(premiumContracts:true); id = peer.Id;
            peer.Send(44,Build(writer => { writer.Write((ushort)65);writer.Write((byte)0);writer.Write(0u);writer.Write(7440);writer.Write(0); }));
            var use = Drain(peer);
            That(use.Any(packet => packet.Kind == 1 && packet.Opcode == 44 && packet.Body[0] == 1) &&
                fixture.Query("SELECT permille FROM character_exp_bonus WHERE character_id=$p0",id) is [[1000L]],
                "真实44消费经验药，倍率与期限提交到角色持久状态");
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
            var clear = SelfTestCatalogs.ClearExperience(resources.Archive).Calculate(dungeon,0,99);
            uint contract = clear.BaseExperience * 20 / 100, potion = clear.BaseExperience + clear.RankExperience;
            byte[] result = DungeonSettlementProtocolTests.Report(id,99);
            peer.Send(46,result);
            var settled = Drain(peer);
            var clearNotice = settled.Single(packet => packet.Kind == 0 && packet.Opcode == 37).Body;
            experience += clear.TotalExperience + contract + potion;
            That(U64(clearNotice,1) == experience && U32(clearNotice,34) == contract && U32(clearNotice,87) == potion &&
                settled.Any(packet => packet.Kind == 0 && packet.Opcode == 35) &&
                fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",id) is [[1L]],
                "持秘药完整六房可正常结算，含评分基数与契约分开记账，不再被遗漏分项拒绝");
            peer.Send(46,result);Drain(peer);
            That(Experience(fixture,id) == experience,"相同结算恢复不重复发经验");
        }
        await fixture.StopAsync(); await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            peer.Select(premiumContracts:true);
            That(Experience(fixture,id) == experience &&
                fixture.Query("SELECT permille FROM character_exp_bonus WHERE character_id=$p0",id) is [[1000L]],
                "重建服务后保持经验及药剂期限，不补发历史分项");
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
