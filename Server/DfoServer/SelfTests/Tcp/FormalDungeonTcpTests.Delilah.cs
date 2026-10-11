using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    static void PrepareDelilahServices(CharacterRepository repository, AccountOwners.Lease owner, Character character, TcpTestResources resources)
    {
        var items = resources.ItemResources.Items;
        var low = InventoryRewardGrantService.Create(items.Get(10400));
        var high = InventoryRewardGrantService.Create(items.Get(10402)) with { Durability = 0 };
        var worn = character.Inventory.Find(new(InventoryListType.Equipment,14))!;
        var after = character.Inventory with
        {
            Gold = 1000,
            Items = character.Inventory.Items.Replace(worn,worn with { Core = high })
                .Add(new(0,new(InventoryListType.Main,4),low with { Durability = 0 }))
                .Add(new(0,new(InventoryListType.Main,9),high))
                .Add(new(0,new(InventoryListType.Main,10),low))
                .Add(new(0,new(InventoryListType.Main,11),low))
                .Add(new(0,new(InventoryListType.Main,66),InventoryRewardGrantService.Create(items.Get(2600468),2)))
        };
        repository.CommitInventory(owner,character,new(character.Inventory,after,"delilah_tcp_fixture"));
    }

    static void RejectDelilahServices(TcpTestFixture fixture, TcpPeer peer, string phase)
    {
        string before = fixture.Snapshot();
        SendDelilahRepair(peer,0,4,false);
        peer.Expect(1,23,[0,19,0]);
        SendDelilahDisjoint(peer,10,-1);
        peer.Expect(1,26,[0,19,0]);
        peer.Barrier();
        That(fixture.Snapshot() == before,phase+" rejects NPC services without durability, gold, source or reward changes");
    }

    static void VerifyDelilahServices(TcpTestFixture fixture, TcpPeer peer, TcpTestResources resources)
    {
        long gold = Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id)[0][0]);
        var initialItems = fixture.Query("SELECT i.item_uid,i.item_core,s.list_type,s.slot_index FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid WHERE s.character_id=$p0 ORDER BY i.item_uid",peer.Id);
        SendDelilahRepair(peer,0,4,false);
        gold -= 19;
        RepairAck(0,4);
        RepairDelta(peer.Expect(0,14).Body,0,4,10400);
        string saved = fixture.Snapshot();
        SendDelilahRepair(peer,0,4,false);
        RepairAck(0,4);
        peer.Barrier();
        That(fixture.Snapshot() == saved,"full durability replay in Settlement is an accepted no-op without another debit or audit");

        SendDelilahRepair(peer,0,-1,false);
        gold -= 160;
        RepairAck(0,-1);
        Gold(peer.Expect(0,14).Body);
        RepairDelta(peer.Expect(0,14).Body,3,14,10402);
        That(peer.Expect(0,2).Body[0] == 1,"Settlement worn repair refreshes only the current Player object");
        SendDelilahRepair(peer,0,9,true);
        gold -= 240;
        RepairAck(0,9);
        RepairDelta(peer.Expect(0,14).Body,0,9,10402);
        That(Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",peer.Id)[0][0]) == gold,
            "Settlement single, all and quick repair debit matching PVF per-item prices");
        foreach (var original in initialItems.Where(row => Convert.ToInt32(row[2]) == 3 && Convert.ToInt32(row[3]) == 14 ||
            Convert.ToInt32(row[2]) == 0 && Convert.ToInt32(row[3]) is 4 or 9))
        {
            var old = JsonSerializer.Deserialize<ItemCore>((string)original[1])!;
            var current = JsonSerializer.Deserialize<ItemCore>((string)fixture.Query("SELECT item_core FROM character_inventory_items WHERE item_uid=$p0",original[0])[0][0])!;
            That(current == (old with { Durability = resources.ItemResources.Items.Get(old.ItemId).Durability }),
                "Settlement repair preserves the original UID and every non-durability core field");
        }
        Disjoint(10,-1,2);
        Disjoint(11,66,1);
        saved = fixture.Snapshot();
        SendDelilahDisjoint(peer,11,66);
        peer.Expect(1,26,[0,17,0]);
        peer.Barrier();
        That(fixture.Snapshot() == saved,"Settlement empty-source replay does not spend another tool or duplicate disjoint rewards");

        void RepairAck(byte list,short slot) => peer.Expect(1,23,Build(w =>
        {
            w.Write((byte)1);w.Write(checked((int)gold));w.Write(list);w.Write(slot);w.Write((ushort)0);
        }));
        void Gold(byte[] delta)
        {
            int at = DelilahDeltaEntry(delta,0);
            That(delta[0] == 0 && U32(delta,at+2) == 0 && U32(delta,at+6) == gold,"repair ACK precedes authoritative absolute gold delta");
        }
        void RepairDelta(byte[] delta,byte list,short slot,int item)
        {
            int at = DelilahDeltaEntry(delta,slot);
            That(delta[0] == list && U32(delta,at+2) == item && U16(delta,at+11) == 33,"repair publishes exact committed durability");
            if (list == 0) Gold(delta);
        }
        void Disjoint(short slot,short tool,int toolCount)
        {
            var balances = fixture.Query("SELECT currency_kind,balance FROM account_currency_balances WHERE account=$p0",fixture.Options.DevelopmentAccount)
                .ToDictionary(row => Convert.ToInt32(row[0]),row => Convert.ToInt64(row[1]));
            SendDelilahDisjoint(peer,slot,tool);
            byte[] ack = peer.Expect(1,26).Body;
            That(ack.Length == 27 && ack[0] == 1 && U16(ack,1) == slot && ack[3] == 0 && U16(ack,4) == ushort.MaxValue && ack[6] == 2,
                "Settlement disjoint ACK retains source, NPC owner sentinel and complete result rows");
            byte[] delta = peer.Expect(0,14).Body;
            That(U32(delta,DelilahDeltaEntry(delta,slot)+2) == uint.MaxValue,"source removal is published after disjoint ACK");
            for (int index = 0; index < ack[6];index++)
            {
                int at = 7+index*10;
                int itemId = checked((int)U32(ack,at+2));
                int count = checked((int)U32(ack,at+6));
                var kind = DfoServer.Game.Currency.CurrencyWallet.RewardKind(itemId)!.Value;
                long expected = balances.GetValueOrDefault((int)kind)+count;
                int target = DelilahDeltaEntry(delta,checked((short)U16(ack,at)));
                That(count > 0 && U32(delta,target+2) == itemId && U32(delta,target+6) == expected &&
                    fixture.Query("SELECT balance FROM account_currency_balances WHERE account=$p0 AND currency_kind=$p1",fixture.Options.DevelopmentAccount,(int)kind) is var rows &&
                    Convert.ToInt64(rows[0][0]) == expected,"disjoint ACK uses actual increments and delta/database share committed wallet totals");
            }
            if (tool >= 0)
            {
                int at = DelilahDeltaEntry(delta,tool);
                That(U32(delta,at+2) == 2600468 && U32(delta,at+6) == toolCount,"Settlement portable tool is debited exactly once in the same result");
            }
        }
    }

    static void SendDelilahRepair(TcpPeer peer,byte list,short slot,bool quick) => peer.Send(23,Build(w =>
    {
        w.Write(list);w.Write(slot);w.Write((short)-1);w.Write((ushort)0);w.Write(quick ? (byte)1 : (byte)0);
    }));
    static void SendDelilahDisjoint(TcpPeer peer,short slot,short tool) => peer.Send(26,Build(w =>
    {
        w.Write(slot);w.Write((byte)0);w.Write(tool);w.Write(0);
    }));
    static int DelilahDeltaEntry(byte[] delta,short slot)
    {
        That(delta.Length == 3+123*U16(delta,1),"Delilah delta retains the complete inventory entry contract");
        return Enumerable.Range(0,U16(delta,1)).Select(index => 3+index*123).Single(at => U16(delta,at) == slot);
    }
}
