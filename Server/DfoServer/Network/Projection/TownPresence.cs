using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.GameWorld.Channels;
using DfoServer.Game.Town;
using DfoServer.Game.Settings;

namespace DfoServer.Network.Projection;

internal sealed class TownPresence(TownCatalog towns, ChannelIdentity channel = default, TimeProvider? clock = null,
    GameDebugSettings? debugSettings = null)
{
    sealed record Scene(ChannelIdentity Channel, byte Town, byte Area, string Map, string? RoomAccount);
    sealed class Member(AccountOwners.Lease owner, GameConnectionSender sender, Scene scene, Character character, long serial, string adventureName)
    {
        public AccountOwners.Lease Owner { get; } = owner;
        public GameConnectionSender Sender { get; } = sender;
        public Scene Scene { get; } = scene;
        public Character Character { get; set; } = character;
        public string AdventureName { get; set; } = adventureName;
        public long Serial { get; } = serial;
        public ChannelIdentity Channel => Scene.Channel;
    }

    readonly object gate = new();
    readonly Dictionary<GameConnectionSender, Member> members = [];
    readonly Dictionary<ushort, Member> characters = [];
    readonly Dictionary<Scene, HashSet<Member>> scenes = [];
    long serial;
    Scene SceneOf(AccountOwners.Lease owner, Character character, ChannelIdentity identity)
    {
        // 与聊天共用 TownCatalog.Scene 的场景定位（PVF 地图阶段 + 房间判定），避免两处各算一份；
        // 频道与房间账号在这里补齐。
        var town = towns.Scene(character.Position, character.CompletedQuests.ToHashSet());
        return new(identity, town.Town, town.Area, town.Map, town.IsRoom ? owner.Account : null);
    }

    bool Visible(Member source, Member target, Func<Character, Character, bool>? recipient = null)
    {
        lock (gate)
            return members.TryGetValue(source.Sender, out var current) && ReferenceEquals(current, source) &&
                members.TryGetValue(target.Sender, out var currentTarget) && ReferenceEquals(currentTarget, target) &&
                source.Scene == target.Scene && source.Owner.IsCurrent && target.Owner.IsCurrent &&
                recipient?.Invoke(source.Character, target.Character) != false;
    }
    readonly record struct Bodies(ReadOnlyMemory<byte> Actor, ReadOnlyMemory<byte> State, ReadOnlyMemory<byte> Position,
        ReadOnlyMemory<byte> Area);
    Bodies Capture(Member source)
    {
        var c = source.Character;
        return new(TownPackets.Actor(c, source.Channel, (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds(), source.AdventureName,
            premiumPcRoom: debugSettings?.PremiumPcRoom == true), TownPackets.ActorState(c.Id),
            TownPackets.Position(c, false), TownPackets.Position(c, true));
    }
    RemoteTownActor Projection(Member source, Member target, Bodies bodies) =>
        new(source.Character.Id, source.Serial, bodies.Actor, bodies.State, bodies.Position, () => Visible(source, target))
        { Area = bodies.Area };

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

    public void Join(AccountOwners.Lease owner, GameConnectionSender sender, Character character, ChannelIdentity? entry = null,
        string adventureName = "") => owner.Execute(() =>
    {
        lock (gate)
        {
            RemoveCore(sender);
            // 同一角色被新generation选中时，只清理旧连接成员，不允许其稍后删掉新成员。
            if (characters.TryGetValue(character.Id, out var old))
                RemoveCore(old.Sender);
            var joined = new Member(owner, sender, SceneOf(owner, character, entry ?? channel), character, ++serial, adventureName);
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
            if (!members.TryGetValue(sender, out var source) || !ReferenceEquals(source.Owner, owner) || source.Character.Id != character.Id || source.Scene != SceneOf(owner, character, source.Channel))
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
    public void Refresh(AccountOwners.Lease owner, GameConnectionSender sender, Character character, string? adventureName = null,
        bool broadcast = true) => owner.Execute(() =>
    {
        lock (gate)
        {
            if (!members.TryGetValue(sender, out var source) || !ReferenceEquals(source.Owner, owner) || source.Character.Id != character.Id || source.Scene != SceneOf(owner, character, source.Channel))
                throw new InvalidOperationException("外观刷新缺少当前场景成员");
            source.Character = character;
            if (adventureName is not null) source.AdventureName = adventureName;
            if (!broadcast) return true;
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

    // 原生宠物通知直接更新既有实体；复用同一场景及发送队列，不为此重发Actor或创建另一套在线目录。
    public void Publish(AccountOwners.Lease owner, GameConnectionSender sender, ushort opcode, byte[] body, string action,
        Func<Character, Character, bool>? recipient = null) => owner.Execute(() =>
    {
        lock (gate)
        {
            if (!members.TryGetValue(sender, out var source) || !ReferenceEquals(source.Owner, owner)) return false;
            foreach (var target in Peers(source))
                if (recipient?.Invoke(source.Character, target.Character) != false)
                    target.Sender.Post(0, opcode, body, action, () => Visible(source, target, recipient));
            return true;
        }
    });

    // 只读取既有成员快照，不取目标账号锁；房间成员不能作为别人传送的落点。
    public TeleportTarget? TeleportTarget(ChannelIdentity channel, string? name = null, int characterId = -1)
    {
        lock (gate)
        {
            var member = characterId > 0 && characterId <= ushort.MaxValue ? characters.GetValueOrDefault((ushort)characterId) :
                members.Values.FirstOrDefault(value => string.Equals(value.Character.Name, name, StringComparison.OrdinalIgnoreCase));
            return member is not null && member.Owner.IsCurrent && member.Channel == channel && member.Scene.RoomAccount is null
                ? new(member.Character.Id, member.Character.Name, member.Character.Position, member.Channel, member.Serial,
                    member.Character.Level, member.Character.Job, member.Character.GrowType) : null;
        }
    }
    public bool CurrentTeleportTarget(TeleportTarget target) => TeleportTarget(target.Channel, characterId: target.CharacterId) == target;
}
