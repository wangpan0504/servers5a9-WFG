using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Projection;

internal sealed class TownPresence(TownCatalog towns)
{
    sealed record Scene(byte Town, byte Area, string Map, string? RoomAccount);
    sealed class Member(AccountOwners.Lease owner, GameConnectionSender sender, Scene scene, Character character, long serial)
    {
        public AccountOwners.Lease Owner { get; } = owner;
        public GameConnectionSender Sender { get; } = sender;
        public Scene Scene { get; } = scene;
        public Character Character { get; set; } = character;
        public long Serial { get; } = serial;
    }

    readonly object gate = new();
    readonly Dictionary<GameConnectionSender, Member> members = [];
    readonly Dictionary<ushort, Member> characters = [];
    readonly Dictionary<Scene, HashSet<Member>> scenes = [];
    long serial;
    Scene SceneOf(AccountOwners.Lease owner, Character character)
    {
        var map = towns.Areas[(character.Position.Town, character.Position.Area)].Map(character.CompletedQuests.ToHashSet());
        return new(character.Position.Town, character.Position.Area, map.Path, map.IsRoom ? owner.Account : null);
    }

    bool Visible(Member source, Member target)
    {
        lock (gate)
            return members.TryGetValue(source.Sender, out var current) && ReferenceEquals(current, source) &&
                members.TryGetValue(target.Sender, out var recipient) && ReferenceEquals(recipient, target) &&
                source.Scene == target.Scene && source.Owner.IsCurrent && target.Owner.IsCurrent;
    }
    readonly record struct Bodies(ReadOnlyMemory<byte> Actor, ReadOnlyMemory<byte> State, ReadOnlyMemory<byte> Position);
    static Bodies Capture(Member source)
    {
        var c = source.Character;
        return new(TownPackets.Actor(c), TownPackets.ActorState(c.Id), TownPackets.Position(c.Id, c.Position, false));
    }
    RemoteTownActor Projection(Member source, Member target, Bodies bodies) =>
        new(source.Character.Id, source.Serial, bodies.Actor, bodies.State, bodies.Position, () => Visible(source, target));

    // 只枚举当前场景，固定接收者快照后入队。
    Member[] Peers(Member source) => scenes[source.Scene].Where(member => !ReferenceEquals(member, source)).ToArray();

    void RemoveCore(GameConnectionSender sender)
    {
        if (!members.Remove(sender, out var departed))
            return;
        characters.Remove(departed.Character.Id);
        var scene = scenes[departed.Scene];
        scene.Remove(departed);
        if (scene.Count == 0)
            scenes.Remove(departed.Scene);
        sender.InvalidateScene();
        // 取消回调在异步执行时也按精确成员身份清理。
        foreach (var target in scene.ToArray())
            target.Sender.Leave(departed.Character.Id, departed.Serial);
    }

    public void Join(AccountOwners.Lease owner, GameConnectionSender sender, Character character) => owner.Execute(() =>
    {
        lock (gate)
        {
            RemoveCore(sender);
            // 同一角色被新generation选中时，只清理旧连接成员，不允许其稍后删掉新成员。
            if (characters.TryGetValue(character.Id, out var old))
                RemoveCore(old.Sender);
            var joined = new Member(owner, sender, SceneOf(owner, character), character, ++serial);
            members.Add(sender, joined);
            characters.Add(character.Id, joined);
            if (!scenes.TryGetValue(joined.Scene, out var scene))
                scenes.Add(joined.Scene, scene = []);
            scene.Add(joined);
            var peers = Peers(joined);
            if (peers.Length == 0)
                return true;
            var bodies = Capture(joined);
            foreach (var peer in peers)
            {
                sender.Enter(Projection(peer, joined, Capture(peer)));
                peer.Sender.Enter(Projection(joined, peer, bodies));
            }

            return true;
        }
    });
    public void Move(AccountOwners.Lease owner, GameConnectionSender sender, Character character) => owner.Execute(() =>
    {
        lock (gate)
        {
            if (!members.TryGetValue(sender, out var source) || !ReferenceEquals(source.Owner, owner) || source.Character.Id != character.Id || source.Scene != SceneOf(owner, character))
                throw new InvalidOperationException("Town move without current membership");
            source.Character = character;
            var peers = Peers(source);
            if (peers.Length == 0)
                return true;
            var bodies = Capture(source);
            foreach (var target in peers)
                target.Sender.Move(Projection(source, target, bodies));
            return true;
        }
    });
    // 当前场景换装更新快照并投递Actor，不通过离场/重入改变成员身份。
    public void Refresh(AccountOwners.Lease owner, GameConnectionSender sender, Character character) => owner.Execute(() =>
    {
        lock (gate)
        {
            if (!members.TryGetValue(sender, out var source) || !ReferenceEquals(source.Owner, owner) || source.Character.Id != character.Id || source.Scene != SceneOf(owner, character))
                throw new InvalidOperationException("外观刷新缺少当前场景成员");
            source.Character = character;
            var peers = Peers(source);
            if (peers.Length == 0)
                return true;
            var bodies = Capture(source);
            foreach (var target in peers)
                target.Sender.Refresh(Projection(source, target, bodies));
            return true;
        }
    });
    // 精确清理仅持成员锁，不获取任何账号锁。
    public void Remove(GameConnectionSender sender)
    {
        lock (gate)
            RemoveCore(sender);
    }
    public int Count { get { lock (gate) return members.Count; } }
}
