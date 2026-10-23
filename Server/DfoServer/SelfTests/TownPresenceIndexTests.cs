using static DfoServer.Testing.TestCheck;
using System.Collections.Concurrent;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;

namespace DfoServer;

internal static class TownPresenceIndexTests
{
    sealed class Peer : IAsyncDisposable
    {
        readonly TownPresence presence;
        readonly CancellationTokenSource stop = new();
        public AccountOwners.Lease Owner { get; }
        public GameConnectionSender Sender { get; }
        public ConcurrentQueue<(ushort Opcode, ReadOnlyMemory<byte> Body)> Sent { get; } = new();

        public Peer(AccountOwners owners, TownPresence presence, ushort id)
        {
            this.presence = presence;
            Owner = owners.Acquire("presence-index-" + id, stop.Cancel);
            Sender = new(Owner, Stream.Null, stop, id, (_, opcode, _, body, _) => Sent.Enqueue((opcode, body)));
        }

        public Task Drain() => Sender.Send(1, 1347, [1], "index-test-barrier").WaitAsync(TimeSpan.FromSeconds(2));
        public async ValueTask DisposeAsync()
        {
            presence.Remove(Sender);
            await Sender.DisposeAsync();
            Owner.Dispose();
            stop.Dispose();
        }
    }

    public static async Task RunAsync(TownCatalog towns)
    {
        int assertions = Count;
        var owners = new AccountOwners();
        var presence = new TownPresence(towns);
        var first = towns.InitialReturn;
        var other = towns.Areas.Values.First(area => !area.Default.IsRoom && (area.Town != first.Town || area.Area != first.Area));
        var second = new Location(other.Town, other.Area, 450, 234);
        var peers = Enumerable.Range(1, 6).Select(id => new Peer(owners, presence, (ushort)id)).ToArray();
        Character Role(int index, Location location) => new((ushort)(index + 1), 0, "Index" + index, 1, location, first);
        async Task Drain()
        {
            foreach (var peer in peers) await peer.Drain();
        }
        void Clear()
        {
            foreach (var peer in peers) peer.Sent.Clear();
        }
        ReadOnlyMemory<byte> Body(int index, ushort opcode) => peers[index].Sent.Single(row => row.Opcode == opcode).Body;
        try
        {
            for (int i = 0; i < peers.Length; i++)
                presence.Join(peers[i].Owner, peers[i].Sender, Role(i, i < 3 ? first : second));
            await Drain();
            That(peers.All(peer => peer.Sent.Count(row => row.Opcode == 2) == 2), "each scene receives only its own two peers");
            Clear();
            var source = Role(0, first with { X = (ushort)(first.X + 1) });
            presence.Move(peers[0].Owner, peers[0].Sender, source);
            await Drain();
            That(Body(1, 22).Equals(Body(2, 22)), "one movement shares the same read-only body between recipients");
            That(Body(1, 22).Span.SequenceEqual(TownPackets.Position(source, false)), "shared movement retains committed coordinates");
            That(peers.Where((_, i) => i == 0 || i >= 3).All(peer => peer.Sent.All(row => row.Opcode == 1347)), "movement does not reach self or another scene");
            Clear();
            presence.Refresh(peers[0].Owner, peers[0].Sender, source);
            await Drain();
            That(Body(1, 2).Equals(Body(2, 2)) && Body(1, 2).Span.SequenceEqual(TownPackets.Actor(source)), "appearance shares one complete actor body");
            That(peers.Skip(3).All(peer => peer.Sent.All(row => row.Opcode == 1347)), "appearance stays within the indexed scene");
            foreach (bool refresh in new[] { false, true })
            {
                bool rejected = false;
                try
                {
                    if (refresh) presence.Refresh(peers[0].Owner, peers[0].Sender, source with { Id = 999 });
                    else presence.Move(peers[0].Owner, peers[0].Sender, source with { Id = 999 });
                }
                catch (InvalidOperationException) { rejected = true; }
                That(rejected, "a current sender cannot change the indexed character identity");
            }
            Clear();
            presence.Remove(peers[0].Sender);
            await peers[0].Sender.ClearScene();
            source = source with { Position = second };
            presence.Join(peers[0].Owner, peers[0].Sender, source);
            await Drain();
            That(peers.Skip(1).Take(2).All(peer => peer.Sent.Count(row => row.Opcode == 6) == 1 && peer.Sent.All(row => row.Opcode != 2)), "scene transfer removes the old indexed membership once");
            That(peers.Skip(3).All(peer => peer.Sent.Where(row => row.Opcode != 1347).Select(row => row.Opcode).SequenceEqual(new ushort[] { 2, 3, 23, 22 })), "new scene receives actor, state, area and position in order");
            That(Body(3, 2).Equals(Body(4, 2)) && Body(4, 2).Equals(Body(5, 2)), "joining actor body is shared across the new scene");
            Clear();
            source = source with { Position = second with { X = 451 } };
            presence.Move(peers[0].Owner, peers[0].Sender, source);
            await Drain();
            That(peers.Skip(1).Take(2).All(peer => peer.Sent.All(row => row.Opcode == 1347)) && peers.Skip(3).All(peer => peer.Sent.Count(row => row.Opcode == 22) == 1), "movement uses only the new scene after transfer");
            foreach (var peer in peers) presence.Remove(peer.Sender);
            await Drain();
            That(presence.Count == 0, "all indexed memberships leave cleanly");
            Clear();
            for (int i = 0; i < peers.Length; i++)
                presence.Join(peers[i].Owner, peers[i].Sender, Role(i, i < 3 ? first : second));
            await Drain();
            That(presence.Count == 6 && peers.All(peer => peer.Sent.Count(row => row.Opcode == 2) == 2), "emptied scenes accept reused actor IDs without stale members");
            foreach (var peer in peers) presence.Remove(peer.Sender);
            await Drain();
            // 成员移除与客户端视图清场是两个步骤，独立频道场景沿用正式切换顺序。
            foreach (var peer in peers) await peer.Sender.ClearScene();
            Clear();
            ChannelIdentity[] channels = [new(2, 11), new(2, 11), new(2, 12), new(2, 12), new(3, 11), new(3, 11)];
            for (int i = 0; i < peers.Length; i++)
                presence.Join(peers[i].Owner, peers[i].Sender, Role(i, first), channels[i]);
            await Drain();
            That(peers.All(peer => peer.Sent.Count(row => row.Opcode == 2) == 1), "同地图按服务器分组和频道隔离入场，只收到同频道角色");
            Clear();
            source = Role(0, first with { X = (ushort)(first.X + 1) });
            presence.Move(peers[0].Owner, peers[0].Sender, source);
            await Drain();
            That(peers[1].Sent.Count(row => row.Opcode == 22) == 1 &&
                peers.Skip(2).All(peer => peer.Sent.All(row => row.Opcode == 1347)), "移动不向其他频道或服务器分组传输");
            Clear();
            presence.Refresh(peers[0].Owner, peers[0].Sender, source);
            await Drain();
            That(peers[1].Sent.Count(row => row.Opcode == 2) == 1 &&
                peers.Skip(2).All(peer => peer.Sent.All(row => row.Opcode == 1347)), "外观刷新不向其他频道或服务器分组传输");
            Clear();
            presence.Remove(peers[0].Sender, [1, 1, 1, 0]);
            await peers[0].Sender.ClearScene();
            presence.Join(peers[0].Owner, peers[0].Sender, source, channels[2]);
            presence.Publish(peers[0].Owner, peers[0].Sender, 379, [2, 1, 1, 0], "teleport_arrival_test");
            await Drain();
            That(peers[1].Sent.Where(row => row.Opcode != 1347).Select(row => row.Opcode).SequenceEqual(new ushort[] { 379, 6 }) &&
                Body(1, 379).Span.SequenceEqual(new byte[] { 1, 1, 1, 0 }),
                "光环离场按原频道的已知membership先发出发特效再发离场，不广播到其他分组或频道");
            That(peers.Skip(2).Take(2).All(peer => peer.Sent.Where(row => row.Opcode != 1347)
                .Select(row => row.Opcode).SequenceEqual(new ushort[] { 2, 3, 23, 22, 379 })) &&
                Body(2, 379).Span.SequenceEqual(new byte[] { 2, 1, 1, 0 }) &&
                peers.Skip(4).All(peer => peer.Sent.All(row => row.Opcode == 1347)), "光环到达特效仅在新频道同场景的角色入场序列之后发送");
            That(Body(2, 2).Equals(Body(3, 2)), "同频道入场共享只读Actor包体");
            Clear();
            source = source with { Position = first with { X = (ushort)(first.X + 2) } };
            presence.Move(peers[0].Owner, peers[0].Sender, source);
            await Drain();
            That(peers.Skip(2).Take(2).All(peer => peer.Sent.Count(row => row.Opcode == 22) == 1) &&
                new[] { peers[1], peers[4], peers[5] }.All(peer => peer.Sent.All(row => row.Opcode == 1347)),
                "换频道后只有新频道继续收到移动，账号及角色身份不变");
        }
        finally
        {
            foreach (var peer in peers) await peer.DisposeAsync();
        }
        Console.WriteLine($"PASS {Count - assertions} 场景索引、跨场景迁移和只读包体共享断言");
    }
}
