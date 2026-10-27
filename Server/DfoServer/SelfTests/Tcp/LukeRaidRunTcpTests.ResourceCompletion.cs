using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class LukeRaidRunTcpTests
{
    internal static void ResourceCompletion(TcpTestFixture fixture,TcpPeer[] peers,RaidScript raw,byte[] roster,RaidDungeonCatalog catalog)
    {
        var members=peers.Take(4).ToArray();var others=peers.Skip(4).ToArray();
        var captain=members.Single(peer=>peer.Id==Leader(roster));
        var instance=(string)fixture.Query("SELECT instance_id FROM character_raid_runs WHERE character_id=$p0 AND state=0",captain.Id)[0][0]!;
        int hp=raw.Phases[0].Dungeons.Single(row=>row.Id==5054).HpRaw;
        for(int run=0;run<hp;run++)
        {
            var entered=Enter(members,captain,5054);Load(members);
            Clear(fixture,members,others,captain,entered[0],catalog,0);
            foreach(var peer in members.Where(peer=>peer!=captain))Return(members,peer,true);
            Return(members,captain,true);
            foreach(var peer in others)Drain(peer,true);
        }
        That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE instance_id=$p0 AND phase0_receipt IS NOT NULL AND state=0",instance) is [[8L]],
            "资源最少0使全团八人获得真实P1资格，且P1不占周clear");
        foreach(var peer in peers)
        {
            That(Amount(peer.Id,10308804)==0&&Amount(peer.Id,10308807)==0,"领取前原箱与盒内材料均未提前发放");
            peer.Send(660,[0,1]);
            var claimed=Drain(peer,true);
            That(claimed.Count(packet=>packet is {Kind:1,Opcode:660,Body:[1]})==1&&!claimed.Any(packet=>packet.Opcode==600),
                "Runtime资源策略实际660领取原箱，不填造600展示");
            That(Amount(peer.Id,10308804)==1&&Amount(peer.Id,10308807)==0,"实际SQL仅发显式一个10308804原箱，不直接拆25材料");
            string saved=fixture.Snapshot();
            peer.Send(660,[0,1]);
            var replay=Drain(peer,true);
            That(replay.Count(packet=>packet is {Kind:1,Opcode:660,Body:[1]})==1&&fixture.Snapshot()==saved,
                "原箱领取重放使用原回执，不重复箱子或资格");
            foreach(var other in peers.Where(other=>other!=peer))Drain(other,true);
        }
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE instance_id=$p0 AND phase=0 AND raid_type=1",instance) is [[8L]],
            "资源单kind布局每人仅一条持久回执，未额外创造其它奖励种类");
        That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE instance_id=$p0 AND phase1_receipt IS NOT NULL",instance) is [[0L]],
            "进入P2不提前签发要求最少2次通关的下一阶段资格");
        foreach(int dungeon in new[]{5311,5313})
        {
            var entered=Enter(members,captain,dungeon);Load(members);
            Clear(fixture,members,others,captain,entered[0],catalog,1);
            That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE instance_id=$p0 AND phase1_receipt IS NOT NULL",instance)
                is [[long receipts]] && receipts==(dungeon==5311?0:8), "P2仅1次清图不签资格，显式第二目标终结才密封八人事实");
            foreach(var peer in members.Where(peer=>peer!=captain))Return(members,peer,true);
            Return(members,captain,true);
            foreach(var peer in others)Drain(peer,true);
        }
        foreach(var peer in members)
        {
            peer.Send(660,[0,2]);var claimed=Drain(peer,true);
            That(claimed.Count(packet=>packet is {Kind:1,Opcode:660,Body:[1]})==1&&!claimed.Any(packet=>packet.Opcode==600),
                "达到资源最少2次的P2成员真实领取原箱");
            That(Amount(peer.Id,10304817)==1&&Amount(peer.Id,10308807)==0,"P2仅发10304817原箱，不自动拆100材料");
            string saved=fixture.Snapshot();peer.Send(660,[0,2]);var replay=Drain(peer,true);
            That(replay.Count(packet=>packet is {Kind:1,Opcode:660,Body:[1]})==1&&fixture.Snapshot()==saved,"P2原箱回执重放不重复发放");
            foreach(var other in peers.Where(other=>other!=peer))Drain(other,true);
        }
        foreach(var peer in others)
        {
            string saved=fixture.Snapshot();
            peer.Send(660,[0,1]);var oldReceipt=Drain(peer,true);
            That(oldReceipt.Count(packet=>packet is {Kind:1,Opcode:660,Body:[1]})==1&&fixture.Snapshot()==saved,
                "P2不合资格成员仍可幂等重放旧P1卡，不把原回执误判成新发奖");
            peer.Send(660,[0,2]);var denied=Drain(peer,true);
            That(denied.Count(packet=>packet is {Kind:1,Opcode:660,Body:[0,0x12,0]})==1 && fixture.Snapshot()==saved && Amount(peer.Id,10304817)==0,
                "P2未通关另一队不得靠P1已领奖或全团终结领取原箱");
        }
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE instance_id=$p0 AND phase=1 AND raid_type=1",instance) is [[4L]],
            "资源最少2按真实成员Run记录执行，P2仅四条领取回执");
        long Amount(ushort character,int item) => (long)fixture.Query("SELECT COALESCE(SUM(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=$p1",character,item)[0][0]!;
    }
}
