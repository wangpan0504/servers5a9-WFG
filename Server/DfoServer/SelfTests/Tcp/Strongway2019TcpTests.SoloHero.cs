using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.PartyDungeonTcpTests;

namespace DfoServer.Testing;

internal static partial class Strongway2019TcpTests
{
    static Character PrepareSoloHero(TcpTestFixture fixture, TcpTestResources resources, CharacterAntiEvil provider)
    {
        // 与匹配TCP共用已核实装备路径和真实强化，不向Session注入抗魔或队伍。
        var index = PvfScript.List(resources.Archive.ReadText("equipment/equipment.lst"));
        int Exact(string path) => index.Single(row => PvfArchive.NormalizePath("equipment/" + row.Value) == path).Key;
        int weapon = Exact("equipment/character/swordman/weapon/ssword/101000783.equ");
        int[] ids = [weapon, .. EquipmentIds.Skip(1),
            Exact("equipment/character/common/magicstone/100353080.equ"),
            Exact("equipment/character/common/earring/100390020.equ")];
        using var owner = new AccountOwners().Acquire("swtcphero", () => { });
        var candidate = CharacterCreation.Prepare("SWSoloHero", resources.Characters.Get(0), new(65, 1, 450, 234),
            resources.Towns.InitialReturn, resources.Learning.Get);
        var equipment = ids.Select(id =>
        {
            var template = resources.ItemResources.Items.Get(id);
            return new InventoryItem(0, new(InventoryListType.Equipment, template.EquipmentSlot),
                new(id, template.Kind, 0) { Durability = template.Durability, Attr = id == weapon ? (byte)10 : (byte)0 });
        }).ToImmutableArray();
        var hero = new CharacterRepository(fixture.Database).Create(owner, candidate with
        {
            Level = 95, AcceptedStoryLevel = 95, EquipmentSlotFlags = InventoryMoveService.ExpansionSlotFlags,
            Inventory = candidate.Inventory with { Items = equipment },
            InitialLearning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning)
        });
        That(hero.Inventory.Items.Length == 12 && provider.Compute(hero) >= 5400 &&
            hero.Inventory.Items.All(item => InventoryMoveService.Allows(hero, item.Position, item.Core,
                resources.ItemResources.Items, fixture.UtcNow.ToUnixTimeSeconds(), resources.ItemResources.Contracts)),
            "英雄单人TCP角色用十二件真实合法穿戴达到5400，不把简单4127资格当作英雄资格");
        return hero;
    }

    static void VerifySoloHero(TcpTestFixture fixture, TcpTestResources resources,
        (byte Number, int Port) entry, Character hero)
    {
        using var peer = Open(fixture, "swtcphero", gamePort: entry.Port, channelNumber: entry.Number);
        peer.Select(hero.Slot, prepared: true, resumeTown: true, room: false);
        peer.Barrier();
        BeginSelection(peer);
        Reject(5363, "抗魔达标但未组队的英雄仍拒绝");
        peer.Send(132);
        That(Drain(peer).Any(packet => packet is { Kind: 1, Opcode: 132, Body: [1] }),
            "单人英雄回归从选图真实返回城镇再建队直入");

        byte[] partyRequest = Convert.FromHexString("000304000000000500000000000007");
        byte[] created = SetParty();
        ushort partyId = U16(created, 4);
        byte[] direct = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(direct, 5363);
        peer.Send(15, direct);
        var selected = Drain(peer).ToArray();
        int ack = Array.FindIndex(selected, packet => packet is { Kind: 1, Opcode: 15, Body: [1] });
        int state = Array.FindIndex(selected, packet => packet is { Kind: 0, Opcode: 3, Body.Length: 4 } &&
            packet.Body[0] == 1 && U16(packet.Body, 1) == peer.Id && packet.Body[3] == 1);
        int scene = Array.FindIndex(selected, packet => packet is { Kind: 0, Opcode: 27, Body.Length: 42 });
        That(ack >= 0 && state > ack && scene > state &&
            selected.Count(packet => packet is { Kind: 1, Opcode: 15, Body: [1] }) == 1 &&
            selected.Count(packet => packet is { Kind: 0, Opcode: 27 }) == 1,
            "真实一人Party从Town直入5363必须依次ACK15、state1、唯一42B场景27，旧5359专用条件在此失败");
        peer.Send(15, direct);
        var duplicate = Drain(peer);
        That(duplicate.Count(packet => packet is { Kind: 1, Opcode: 15, Body: [1] }) == 1 &&
            !duplicate.Any(packet => packet is { Kind: 0, Opcode: 27 }),
            "一人英雄已在MapSelect的重复指定15不重开场景");
        Reject(5360, "真实一人Party也不能绕过匹配直接进入5360");
        Reject(5364, "真实一人Party不会开放5364资格图");

        byte[] request = Entry(5363);
        peer.Send(16, request);
        var entered = Drain(peer);
        byte[] info = entered.Single(packet => packet is { Kind: 0, Opcode: 28 }).Body;
        byte[] map = entered.Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
        That(info.Length == 36 && U32(info) == 5363 && info[4] == 2 &&
            !entered.Any(packet => packet is { Kind: 1, Opcode: 16 }) &&
            fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id) is [[1L]],
            "真实一人英雄16发送完整28/29并只预留一次原Run尝试");
        var dungeon = SelfTestCatalogs.Dungeons(resources.Archive).Get(5363);
        var maze = dungeon.Mazes[info[7]];
        var path = Route(maze, new(map[0], map[1]), new(info[8], info[9]));
        That(maze.MapSpecification!.Count == 13 && path.Count == 12,
            "单人英雄复用真实十三房路线，不借用匹配九房地图");
        int rooms = 0;
        bool cleared = false;
        while (true)
        {
            That(++rooms <= 13 && map.Length >= 19 + map[18] * 22, "英雄START_MAP演员完整且路线有界");
            FormalDungeonTcpTests.Load(peer);
            Drain(peer);
            foreach (int at in Enumerable.Range(0, map[18]).Select(i => 19 + i * 22)
                .Where(at => map[at + 13] <= 3 && map[at + 21] == 0).OrderBy(at => map[at + 13] == 3))
            {
                ushort key = U16(map, at + 6);
                peer.Send(39, FormalDungeonTcpTests.Death(key, peer.Id, map[at + 12]));
                var deaths = Drain(peer);
                That(deaths.Count(packet => packet is { Kind: 0, Opcode: 38 } && U16(packet.Body) == key) == 1,
                    "单人英雄真实39只退场当前公布演员，连接继续处理原Run");
                cleared |= deaths.Any(packet => packet is { Kind: 0, Opcode: 1491, Body.Length: 16 });
            }
            if (!path.TryPop(out var target)) break;
            byte[] move = new byte[100]; move[0] = checked((byte)target.X); move[1] = checked((byte)target.Y);
            peer.Send(45, move);
            var moved = Drain(peer);
            That(moved.Count(packet => packet is { Kind: 1, Opcode: 45, Body: [1] }) == 1,
                "单人英雄清房后由真实45推进原十三房路线");
            map = moved.Single(packet => packet is { Kind: 0, Opcode: 29 }).Body;
            That(map[0] == target.X && map[1] == target.Y, "英雄换图绑定本次相邻房请求");
        }
        That(rooms == 13 && cleared && fixture.Query(
            "SELECT dungeon_id,mode FROM character_strongway2019_statistics WHERE character_id=$p0", peer.Id) is [[5363L, 2L]],
            "真实一人英雄跑完整条路线并由原Run终点提交英雄成绩");
        peer.Send(42);
        var returned = Drain(peer);
        That(returned.Any(packet => packet is { Kind: 1, Opcode: 42, Body: [1] }) &&
            fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[65L, 1L]],
            "一人英雄真实42正常返回65/1，连接没有断开");
        // 完全相同设置会被原队伍服务静默去重；真实改标题后才发布可核对身份的名册。
        partyRequest[1] = 4;
        That(U16(SetParty(), 4) == partyId,
            "英雄选图、加载、击杀、过房、通关和返城保留真实一人Party，改名12仍更新原队伍ID");
        peer.Send(13); peer.Expect(1, 13, [1]); Drain(peer);
        peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();

        byte[] SetParty()
        {
            peer.Send(12, partyRequest);
            var body = Drain(peer).Single(packet => packet is { Kind: 0, Opcode: 9 }).Body;
            That(body.Length == 56 && U16(body) == 1 && body[9] == 0 && body[38] == 0 && U16(body, 39) == peer.Id,
                "真实CMD12创建或修改唯一队长的一人Party，不注入Session队伍代理");
            return body;
        }

        void Reject(uint dungeonId, string reason)
        {
            string saved = Saved();
            peer.Send(16, Entry(dungeonId));
            var rejected = Drain(peer);
            That(rejected.Count(packet => packet is { Kind: 1, Opcode: 16, Body.Length: 4 } && packet.Body[0] == 0) == 1 &&
                !rejected.Any(packet => packet.Kind == 0 && packet.Opcode is 28 or 29) && Saved() == saved,
                reason + "，不写资产、attempt或奖励");
        }

        string Saved() => fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", peer.Id) +
            fixture.Snapshot("SELECT * FROM character_dungeon_state WHERE character_id=$p0", peer.Id) +
            fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id) +
            fixture.Snapshot("SELECT * FROM account_strongway2019_rewards WHERE character_id=$p0 ORDER BY instance_id", peer.Id) +
            fixture.Snapshot("SELECT * FROM character_strongway2019_statistics WHERE character_id=$p0 ORDER BY instance_id", peer.Id);

        byte[] Entry(uint dungeonId)
        {
            var body = EntryRequest();
            BinaryPrimitives.WriteUInt32LittleEndian(body, dungeonId);
            return body;
        }
    }
}
