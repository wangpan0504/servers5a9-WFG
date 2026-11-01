using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Titles;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FiendwarRaidTcpTests
{
    static void VerifyNameHistory(TcpTestFixture fixture, TcpTestResources resources, CharacterRepository repository,
        TcpPeer requester, byte server)
    {
        const string account = "fiendname";
        using (var owner = new AccountOwners().Acquire(account, () => { }))
        {
            var role = repository.Create(owner, CharacterCreation.Prepare("FiendOld", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
            role = repository.Rename(owner, role, "FiendNew");
            var core = new ItemCore(26596, ItemKind.Equipment, 987654321)
            {
                Attr = 7, Durability = 30, SealFlag = 1, EnchantCardId = 3185, EnchantUpgrade = 2,
                AmplifyType = 3, AmplifyValue = 120, Extension97 = 0x12340056, Marker16 = 123
            };
            repository.CommitInventory(owner, role, new(role.Inventory,
                role.Inventory with { Items = role.Inventory.Items.Add(new(0, TitleBookService.Slot(4, 3), core)) }, "fixture_raid_title"));
        }
        using var target = Open(fixture, account);
        target.Select(); target.Barrier();
        requester.Send(8, [(byte)target.Id, (byte)(target.Id >> 8), 3]);
        requester.ExpectOtherPlayerActor(target.Id, 10);
        var publicInfo = requester.Expect(0, 2).Body;
        That(publicInfo[0] == 3 && U16(publicInfo, 14) == target.Id, "既有跨频道公开资料来自目标角色");
        requester.Barrier();
        VerifyCrossPvp(fixture, requester, target, server);
        string before = fixture.Snapshot("SELECT * FROM character_name_history WHERE character_id=$p0", target.Id);
        string titlesBefore = fixture.Snapshot("SELECT * FROM character_title_book_slots WHERE character_id=$p0", target.Id);
        for (int repeat = 0; repeat < 2; repeat++)
        {
            requester.Send(821, Request(server, target.Id, 0, 10));
            var seededActor = requester.ExpectOtherPlayerActor(target.Id, 10).Body;
            var infoFrames = AntonTcpProbe.Barrier(requester, "fiendwar cross-channel character information");
            var info = infoFrames.Single(row => row is { Kind: 0, Opcode: 757 }).Body;
            Bytes(info[..5], [10, (byte)target.Id, (byte)(target.Id >> 8), server, 10],
                "757目标头及第一段server/channel保持原生顺序");
            That(U16(info, 121) == target.Id && info.Length == seededActor.Length + publicInfo.Length - 3,
                "先建立目标缓存，再发完整双段757，身份属于目标角色");
            Bytes(info.AsSpan(3, seededActor.Length - 3), seededActor.AsSpan(3), "757身份正文与同次实际缓存安装相同");
            Bytes(info.AsSpan(seededActor.Length), publicInfo.AsSpan(3),
                "757公开属性与原生普通查询逐字节相同，保留装备/技能/公会尾部");
            That(!infoFrames.Any(row => row is { Kind: 1, Opcode: 821 } or { Kind: 0, Opcode: 2 }),
                "757不造ACK或多发本人/普通公开属性，后续仍能正常查询");
            requester.Send(821, Request(server, target.Id, 1, 10));
            var frames = AntonTcpProbe.Barrier(requester, "fiendwar cross-channel former name");
            var body = frames.Single(row => row is { Kind: 0, Opcode: 761 }).Body;
            int width = checked((int)U32(body, 5));
            That(body[0] == 10 && body[1] == server && U16(body, 2) == target.Id && body[4] == 1 &&
                body.Length == 14 + width && DecodeName(body, 9, width) == "FiendOld" && body[9 + width] == server &&
                U32(body, 10 + width) > 0 && !frames.Any(row => row is { Kind: 1, Opcode: 821 } or { Kind: 0, Opcode: 736 }),
                "漩涡频道实际查询普通10频道在线角色，返回原生761及真实历史，不造ACK或错用736");
            requester.Send(821, Request(server, target.Id, 2, 10));
            var titleFrames = AntonTcpProbe.Barrier(requester, "fiendwar cross-channel title book");
            var titleBody = titleFrames.Single(row => row is { Kind: 0, Opcode: 762 }).Body;
            Bytes(titleBody[..31],
                [10, server, (byte)target.Id, (byte)(target.Id >> 8), 1, 5, 0, 0, 0, 0, 0,
                    1, 0, 0, 0, 0, 2, 0, 0, 0, 0, 3, 0, 0, 0, 0, 4, 1, 0, 0, 0],
                "762返回目标定位和五类实际数量，末类非空不提前终止");
            Bytes(titleBody[31..],
                [3, 0, 0xE4, 0x67, 0, 0, 0xB1, 0x68, 0xDE, 0x3A, 7, 30, 0, 1, 0x71, 0x0C, 0, 0,
                    2, 3, 120, 0, 4, 0, 0, 0, 0x56, 0, 0x34, 0x12, 123, 0, 0, 0],
                "跨频道称号保留目标实例的附魔、增幅、RAW扩展及独立尾参数");
            That(!titleFrames.Any(row => row is { Kind: 1, Opcode: 821 } or { Kind: 0, Opcode: 358 }),
                "跨频道称号不造ACK或误发同频道358");
        }
        requester.Send(821, Request(server, requester.Id, 1, 39));
        Bytes(AntonTcpProbe.Barrier(requester, "fiendwar empty former name").Single(row => row is { Kind: 0, Opcode: 761 }).Body,
            [39, server, (byte)requester.Id, (byte)(requester.Id >> 8), 0], "没有历史的角色发送完整零行表");
        requester.Send(821, Request(server, requester.Id, 2, 39));
        Bytes(AntonTcpProbe.Barrier(requester, "fiendwar empty title book").Single(row => row is { Kind: 0, Opcode: 762 }).Body,
            [39, server, (byte)requester.Id, (byte)(requester.Id >> 8), 1, 5, 0, 0, 0, 0, 0,
                1, 0, 0, 0, 0, 2, 0, 0, 0, 0, 3, 0, 0, 0, 0, 4, 0, 0, 0, 0],
            "空称号簿发送全部五类零行表以清理他人窗口旧数据");
        foreach (var body in new[]
        {
            Request(unchecked((byte)(server + 1)), target.Id, 1, 10), Request(server, target.Id, 1, 39),
            Request(server, ushort.MaxValue, 1, 10), Request(server, target.Id, 4, 10),
            Request(server, target.Id, 1, 10).Concat(new byte[] { 0 }).ToArray(),
            Request(unchecked((byte)(server + 1)), target.Id, 2, 10), Request(server, target.Id, 2, 39),
            Request(server, ushort.MaxValue, 2, 10), Request(server, target.Id, 3, 10),
            Request(server, target.Id, 2, 10).Concat(new byte[] { 0 }).ToArray(),
            Request(unchecked((byte)(server + 1)), target.Id, 0, 10), Request(server, target.Id, 0, 39),
            Request(server, ushort.MaxValue, 0, 10), Request(server, target.Id, 0, 10).Concat(new byte[] { 0 }).ToArray()
        })
        {
            requester.Send(821, body);
            That(!AntonTcpProbe.Barrier(requester, "fiendwar invalid cross-channel target").Any(row =>
                row is { Kind: 0, Opcode: 757 or 761 or 762 or 358 or 2 } or { Kind: 1, Opcode: 821 }),
                "错服、错频道、离线目标、未知模式或多余尾字节不返回虚假查询或ACK");
        }
        That(fixture.Snapshot("SELECT * FROM character_name_history WHERE character_id=$p0", target.Id) == before,
            "重复和拒绝的跨频道查询不修改改名账本");
        That(fixture.Snapshot("SELECT * FROM character_title_book_slots WHERE character_id=$p0", target.Id) == titlesBefore,
            "重复和拒绝的跨频道查询不移动或复制目标称号");

        static byte[] Request(byte server, ushort character, byte mode, byte channel) =>
            [server, (byte)character, (byte)(character >> 8), mode, channel];
    }
}
