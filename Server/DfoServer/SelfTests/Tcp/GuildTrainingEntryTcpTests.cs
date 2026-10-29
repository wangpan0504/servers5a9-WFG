using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.PartyDungeonTcpTests;

namespace DfoServer.Testing;

internal static class GuildTrainingEntryTcpTests
{
    internal static InventorySnapshot Coins(InventorySnapshot inventory, TcpTestResources resources) => inventory with
    {
        Items = inventory.Items.Add(new(0, new(InventoryListType.Main,
            DfoServer.Game.Inventory.Services.ItemSlotBoundService.Range(resources.ItemResources.Items.Get(490011648).Kind, inventory).Start),
            new(490011648, resources.ItemResources.Items.Get(490011648).Kind, 20)))
    };

    internal static void SelectTraining(TcpTestFixture fixture, ushort id)
    {
        fixture.Sql("""
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix,public_funds)
            VALUES($p0,'训练入场' || $p0,'training-entry-' || $p0,$p0,1600000000,1000000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES($p0,$p0,1,1600000000);
            INSERT INTO guild_npc_progress(guild_id,npc_id,level,contract_until) VALUES($p0,400001064,2,$p1);
            INSERT INTO character_guild_npc_benefits(character_id,guild_id,joined_at,effect,benefit_id,remaining,deadline,selected)
            VALUES($p0,$p0,1600000000,'guild trainer attack raid buff',5001,0,0,1);
            """, id, fixture.UtcNow.ToUnixTimeSeconds() + 86400);
    }

    internal static void CheckCoins(TcpTestFixture fixture, ushort id, int expected) =>
        That(fixture.Query("""
            SELECT sum(json_extract(item_core,'$.Value')) FROM character_inventory_items
            WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=490011648
            """, id) is [[long actual]] && actual == expected, "专用入场训练币只按本次提交扣一次");

    internal static async Task VerifySoloAsync(ServerOptions source, string root, TcpTestResources resources, bool imperial)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, imperial ? "imperial-training" : "tag-training"), resources: resources);
        ushort primary;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var repository = new CharacterRepository(fixture.Database);
            Character Create(string name, bool training)
            {
                var plan = CharacterCreation.Prepare(name, resources.Characters.Get(0), resources.Towns.Room,
                    resources.Towns.InitialReturn, resources.Learning.Get);
                return repository.Create(owner, plan with { Level = 95, Inventory = training ? Coins(plan.Inventory, resources) with { Gold = 1_000_000 } : plan.Inventory,
                    InitialLearning = SkillPointLedger.Synchronize(0, 0, 95,
                        plan.InitialLearning with { BonusSp = 0, BonusTp = 0 }, resources.Learning) });
            }
            primary = Create("TrainFirst", true).Id; Create("TrainSecond", false);
        }
        SelectTraining(fixture, primary);
        await fixture.StartAsync();
        byte channel = imperial ? (byte)48 : (byte)46;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var peer = Open(fixture, gamePort: ChannelTcpTests.Endpoints(fixture)[channel], channelNumber: channel);
            peer.Select(tagEvildom: imperial ? [1403, 1400, 1632, 1393, 1394] : [1677, 1678, 1393, 1394]);
            peer.Send(1426); Drain(peer);
            if (imperial)
            {
                peer.Send(1461, [1]); peer.Expect(0, 14); peer.Expect(0, 1403); peer.Expect(0, 1400); peer.Expect(0, 1632);
                peer.Expect(0, 1393); peer.Expect(0, 1394); peer.Expect(1, 1461, [1]); peer.Barrier();
            }
            uint dungeon = imperial ? 320u : 9608u;
            peer.Send(15, Build(w => { w.Write(dungeon); w.Write((ushort)0); }));
            if (attempt == 0)
            {
                peer.Expect(1, 15, [1]); peer.Expect(0, 3); peer.Expect(0, 27); peer.Barrier();
                string before = EntryState(fixture);
                string table = imperial ? "account_imperial_arena_runs" : "account_tag_evildom_runs";
                fixture.Sql("CREATE TRIGGER reject_training_entry BEFORE INSERT ON " + table +
                    " BEGIN SELECT RAISE(ABORT,'training entry rollback'); END");
                SendSelection(); peer.Closed();
                That(EntryState(fixture) == before && fixture.Query("SELECT count(*) FROM " + table) is [[0L]],
                    "专用准入末段SQL失败，训练费、版本、尝试号及资格全部回滚");
                CheckCoins(fixture, primary, 20);
                fixture.Sql("DROP TRIGGER reject_training_entry");
                continue;
            }
            SendSelection();
            void SendSelection() =>
                peer.Send(16, Build(w => { w.Write(dungeon); w.Write((byte)0); w.Write((ushort)0); w.Write((byte)0); w.Write((byte)0);
                    w.Write(ushort.MaxValue); w.Write(0u); w.Write((byte)0); w.Write(0u); w.Write((byte)0); w.Write(uint.MaxValue); }));
            var entered = Drain(peer);
            That(entered.Any(p => p is { Kind: 0, Opcode: 28 }) && entered.Any(p => p is { Kind: 0, Opcode: 29 }) &&
                entered.Any(p => p is { Kind: 0, Opcode: 14 }), "已选择训练的专用副本创建真实地图并发送扣币差量，连接保持可用");
            CheckCoins(fixture, primary, 19);
            peer.Send(37, new byte[8]); var loaded = Drain(peer);
            That(loaded.Any(p => p is { Kind: 0, Opcode: 1774 } && p.Body.Length == 16 && p.Body[0] == 3) &&
                loaded.Any(p => p is { Kind: 0, Opcode: 487 }), "专用副本加载保留训练状态与属性投影");
            peer.Send(37, new byte[8]); Drain(peer); CheckCoins(fixture, primary, 19);
            peer.Send(42); Drain(peer); CheckCoins(fixture, primary, 19);
        }
    }

    static string EntryState(TcpTestFixture fixture) => string.Concat(new[]
    {
        "SELECT id,revision,gold FROM characters ORDER BY id",
        "SELECT * FROM character_inventory_items ORDER BY item_uid",
        "SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index",
        "SELECT * FROM character_dungeon_state ORDER BY character_id",
        "SELECT * FROM character_fatigue ORDER BY character_id"
    }.Select(sql => fixture.Snapshot(sql)));
}
