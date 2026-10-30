using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class CharacterRenameCardTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var card = resources.ItemResources.Items.ActionConsumable(2660698);
        That(card is { Label: "[character name change]", Parameters.Length: 0 } && card.AllowsPlace("[seria room]"),
            "正式PVF更名卡按动作及城镇地点注册，不把兑换价当额外使用费");
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "character-rename-card"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        ushort characterId;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var dummy = repository.Create(owner, new("RenameHole", resources.Towns.Room, resources.Towns.InitialReturn));
            var candidate = CharacterCreation.Prepare("RenameTcp", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var role = repository.Create(owner, candidate with { Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                .Add(new(0, new(InventoryListType.Main, 65), new(2660698, ItemKind.Consumable, 2))) } });
            characterId = role.Id;
            repository.Delete(owner, repository.LoadDeletionTarget(owner, dummy.Slot));
        }
        using (var owner = new AccountOwners().Acquire("20001", () => { }))
            repository.Create(owner, new("NameTaken", resources.Towns.Room, resources.Towns.InitialReturn));
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            CheckRoster(peer.Roster, "RenameTcp", false);
            peer.Select();
            That(peer.Id == characterId, "名册index0映射到持久槽1");
            peer.Send(509, [66, 0]); peer.Expect(1, 509, [0, 19, 0]); peer.Barrier();
            peer.Send(509, [65, 0]); peer.Expect(1, 509, [1]); peer.Expect(1, 7);
            CheckRoster(peer.Expect(0, 2).Body, "RenameTcp", true); RosterBarrier(peer);
            That(Remaining() == 1 && Pending() == 1, "成功509与小退后资格及源数量一致");
            peer.Send(509, [65, 0]); peer.Expect(1, 509, [0, 19, 0]); RosterBarrier(peer);
            peer.Send(4, [0, 0]); peer.Expect(1, 4, [0, 96, 0]); RosterBarrier(peer);
            Rename(peer, 1, "RenameTcp", "WrongSlot", [0, 0, 0]);
            Rename(peer, 0, "OtherOld", "WrongOld", [0, 0, 0]);
            Rename(peer, 0, "RenameTcp", "Bad!", [0, 159, 0]);
            Rename(peer, 0, "RenameTcp", "NameTaken", [0, 24, 0]);
            That(Remaining() == 1 && Pending() == 1, "重复、错行、旧名不符、非法名与全局重名均不扣卡或丢资格");
        }
        await fixture.StopAsync(); await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            CheckRoster(peer.Roster, "RenameTcp", true);
            peer.Send(135, CharacterRenameCardTests.Body(0, "RenameTcp", "RenamedTcp"));
            peer.Expect(1, 135, [1]); CheckRoster(peer.Expect(0, 2).Body, "RenamedTcp", false); RosterBarrier(peer);
            That(Remaining() == 1 && Pending() == 0 && fixture.Query("SELECT id,slot,name FROM characters WHERE id=$p0", characterId)
                is [[long id, 1L, "RenamedTcp"]] && id == characterId, "重启后改名保留ID与持久槽，清一次资格不再扣卡");
            Rename(peer, 0, "RenamedTcp", "Again", [0, 0, 0]);
            peer.Select(); peer.Barrier();
            That(peer.Id == characterId, "改名后可正常按刷新名册选中原角色");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "改名及重启后数据库与外键完整");

        long Pending() => (long)fixture.Query("SELECT count(*) FROM character_rename_pending WHERE character_id=$p0", characterId)[0][0];
        long Remaining() => (long)fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=2660698", characterId)[0][0];
    }

    static void Rename(TcpPeer peer, byte index, string oldName, string newName, byte[] expected)
    {
        peer.Send(135, CharacterRenameCardTests.Body(index, oldName, newName));
        peer.Expect(1, 135, expected); RosterBarrier(peer);
    }

    static void RosterBarrier(TcpPeer peer)
    {
        peer.Send(692, Name("RnBarrier"));
        peer.Expect(1, 692, [1]);
    }

    static void CheckRoster(byte[] body, string name, bool pending)
    {
        int width = (int)U32(body, 17);
        int flags = 21 + width;
        That(body[0] == 2 && body[1] == 1 && U16(body, 15) == 1 && DecodeName(body, 21, width) == name &&
            body[flags] == (pending ? 1 : 0) && body[flags + 1] == 0, "原生名册名称后首标志反映持久资格，第二标志不变");
    }
}
