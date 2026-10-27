using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class UpgradeChronicleTcpTests
{
    static void RunEquipmentGrowth(TcpTestFixture fixture, CharacterRepository repository, TcpTestResources resources)
    {
        foreach (bool automatic in new[] { true, false })
        {
            string account = automatic ? "dragon-auto" : "dragon-scroll";
            Character actor;
            var slot = new InventorySlot(InventoryListType.Equipment,12);
            using (var owner = new AccountOwners().Acquire(account, () => { }))
            {
                actor = repository.Create(owner,CharacterCreation.Prepare(automatic ? "DragonAuto" : "DragonScroll",
                    resources.Characters.Get(0),resources.Towns.Room,resources.Towns.InitialReturn,resources.Learning.Get));
                var core = InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(automatic ? 108030498 : 108030500))
                    with { Extension90 = automatic ? (ushort)1792 : (ushort)4828, Extension92 = 7, Attr = 9, EnchantCardId = 100000201 };
                actor = repository.CommitInventory(owner,actor,new(actor.Inventory,actor.Inventory with
                    { Items = actor.Inventory.Items.Where(item => item.Position != slot).Append(new InventoryItem(0,slot,core)).ToImmutableArray() },"dragon_tcp_setup"));
            }
            var original = actor.Inventory.Find(slot)!;
            using var peer = Open(fixture,account);
            peer.Select();
            FormalDungeonTcpTests.EntryTown(peer);
            peer.Send(31,[31,0,73,12]);
            That(Drain().Any(packet => packet is { Kind:1,Opcode:31,Body:[1,..] }),"真实龙跃副本接受剧情任务3145");
            peer.Send(15,new byte[4]);
            var selected = Drain();
            That(selected.Any(packet => packet is { Kind:1,Opcode:15,Body:[1] }) &&
                selected.Any(packet => packet is { Kind:0,Opcode:27 }),"真实选图完成后请求剧情副本，不伪造实例");
            peer.Send(16,Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000"));
            peer.Expect(0,28);
            var map = peer.Expect(0,29).Body;
            FormalDungeonTcpTests.Load(peer);
            int target = Enumerable.Range(0,map[18]).Select(index => 19 + index * 22)
                .First(at => map[at+13] <= 3 && U32(map,at+8) != 107000906 && map[at+21] == 0);
            That(Math.Abs(actor.Level - map[target+12]) <= 10,"真实怪物符合龙跃成长等级差");
            var report = FormalDungeonTcpTests.Death(U16(map,target+6),peer.Id,map[target+12]);
            peer.Send(39,report);
            var packets = Drain();
            int experience = Array.FindIndex(packets,packet => packet is { Kind:0,Opcode:37 });
            int growth = Array.FindIndex(packets,packet => packet.Kind == 0 && packet.Opcode == (automatic ? 481 : 480));
            That(experience >= 0 && growth > experience,"真实死亡提交经验后发布装备成长");
            if (automatic)
            {
                var body = packets[growth].Body;
                That(body.Length == 12 && body.AsSpan(0,5).SequenceEqual(new byte[] {1,1,3,12,0}) &&
                    U32(body,5) == 108030499 && U16(body,9) == 0 && body[11] == 7 &&
                    packets.Skip(growth+1).Any(packet => packet is { Kind:0,Opcode:2,Body:[0,..] }) &&
                    packets.Skip(growth+1).Any(packet => packet is { Kind:0,Opcode:2,Body:[1,..] }),
                    "自动晋阶481携带新模板并刷新Actor与Player");
            }
            else Bytes(packets[growth].Body,[3,12,0,2,0,222,18,7],"第三阶480封顶后等待卷轴");
            using var reader = new AccountOwners().Acquire(account, () => { });
            var committed = repository.LoadCharacter(reader,actor.Slot).Inventory.Find(slot)!;
            That(committed.Uid == original.Uid && committed.Core == original.Core with
                { ItemId = automatic ? 108030499 : 108030500, Extension90 = automatic ? (ushort)0 : (ushort)4830 },
                "自然击杀成长持久化完整打造与UID");
            peer.Send(39,report);
            That(!Drain().Any(packet => packet.Kind == 0 && packet.Opcode is 37 or 480 or 481) &&
                repository.LoadCharacter(reader,actor.Slot).Inventory.Find(slot) == committed,"重复死亡不再成长或重复发布");

            WirePacket[] Drain()
            {
                peer.SendBarrier();
                var packets = new List<WirePacket>();
                for (int at = 0; at < 128; at++)
                {
                    var packet = peer.Read();
                    if (packet is { Kind:1,Opcode:1347 }) { Bytes(packet.Body,[1],"装备成长阶段屏障");return [..packets]; }
                    packets.Add(packet);
                }
                throw new InvalidDataException("装备成长通知超出阶段包数上限");
            }
        }
    }
}
