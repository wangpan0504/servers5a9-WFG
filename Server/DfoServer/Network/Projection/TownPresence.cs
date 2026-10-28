using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.GameWorld.Channels;
using DfoServer.Game.Town;
using DfoServer.Game.Settings;
using DfoServer.Game.ExpertJob;

namespace DfoServer.Network.Projection;

internal sealed class TownPresence(TownCatalog towns, ChannelIdentity channel = default, TimeProvider? clock = null,
    GameDebugSettings? debugSettings = null, ExpertJobStoreBoard? expertStores = null, MarriageAppearance? marriage = null)
{
    sealed record Scene(ChannelIdentity Channel, byte Town, byte Area, string Map, string? RoomAccount);
    sealed class Member(AccountOwners.Lease owner, GameConnectionSender sender, Scene scene, Character character, long serial, string adventureName)
    {
        public AccountOwners.Lease Owner { get; } = owner;
        public GameConnectionSender Sender { get; } = sender;
        public Scene Scene { get; set; } = scene;
        public Character Character { get; set; } = character;
        public ushort ScaledSpeed { get; set; } = 100;
        public string AdventureName { get; set; } = adventureName;
        public long Serial { get; } = serial;
        public ChannelIdentity Channel => Scene.Channel;
    }

    readonly object gate = new();
    readonly Dictionary<GameConnectionSender, Member> members = [];
    readonly Dictionary<ushort, Member> characters = [];
    readonly Dictionary<Scene, HashSet<Member>> scenes = [];
    long serial;
    public bool CanInteract(AccountOwners.Lease first, ushort firstId, AccountOwners.Lease second, ushort secondId)
    {
        lock (gate)
            return characters.TryGetValue(firstId, out var a) && characters.TryGetValue(secondId, out var b) &&
                ReferenceEquals(a.Owner, first) && ReferenceEquals(b.Owner, second) && first.IsCurrent && second.IsCurrent &&
                a.Scene == b.Scene && a.Scene.RoomAccount is null;
    }

    Scene SceneOf(AccountOwners.Lease owner, Character character, ChannelIdentity identity)
    {
        // 与聊天共用 TownCatalog.Scene 的场景定位（PVF 地图阶段 + 房间判定），避免两处各算一份；
        // 频道与房间账号在这里补齐。
        var town = towns.Scene(character.Position, character.CompletedQuests.ToHashSet());
        return new(identity, town.Town, town.Area, town.Map, town.OwnerKey(owner.Account,character.Id,
            character.Marriage is { State: 4 } marriage && marriage.IsMember(character.Id) ? marriage.Id : null,
            character.WeddingHall?.CeremonyId));
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
            TownPackets.Position(c, false, source.ScaledSpeed), TownPackets.Position(c, true));
    }
    RemoteTownActor Projection(Member source, Member target, Bodies bodies) =>
        new(source.Character.Id, source.Serial, bodies.Actor, bodies.State, bodies.Position, () => Visible(source, target))
        { Area = bodies.Area, EchonSets = DfoServer.Network.Builders.Inventory.EchonEquipmentPackets.Snapshot(source.Character) };

    void PublishMarriage(Member source, Member target, bool clear = false)
    {
        if (marriage is null || !clear && source.Character.Marriage?.Active != true) return;
        target.Sender.Post(0, 1016, marriage.Body(source.Character), "town_peer_marriage", () => Visible(source, target));
    }

    // 只枚举当前场景，固定接收者快照后入队。
    Member[] Peers(Member source) => scenes[source.Scene].Where(member => !ReferenceEquals(member, source)).ToArray();

    void RemoveCore(GameConnectionSender sender, byte[]? departureEffect = null, byte[]? prepareEffect = null, byte[]? destinationArea = null)
    {
        if (!members.Remove(sender, out var departed))
            return;
        characters.Remove(departed.Character.Id);
        expertStores?.TryClose(departed.Character.Id, out _);
        var scene = scenes[departed.Scene];
        scene.Remove(departed);
        if (scene.Count == 0)
            scenes.Remove(departed.Scene);
        sender.InvalidateScene();
        // 取消回调在异步执行时也按精确成员身份清理。
        foreach (var target in scene.ToArray())
            target.Sender.Leave(departed.Character.Id, departed.Serial, departureEffect,
                departureEffect is null ? null : () => departed.Owner.IsCurrent, prepareEffect, destinationArea);
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
                PublishMarriage(peer, joined);
                PublishMarriage(joined, peer);
                if (expertStores?.TryGet(peer.Character.Id, out var store) == true)
                    sender.Post(0, (ushort)NotiPacketType.CREATE_EXPERT_JOB_STORE,
                        ExpertJobPackets.CreateStoreNotice(store), "expert_job_existing_store",
                        () => Visible(peer, joined) && expertStores.TryGet(store.OwnerId, out var live) && live.Instance == store.Instance);
            }

            return true;
        }
    });
    public void Move(AccountOwners.Lease owner, GameConnectionSender sender, Character character, ushort scaledSpeed = 100) => owner.Execute(() =>
    {
        lock (gate)
        {
            if (!members.TryGetValue(sender, out var source) || !ReferenceEquals(source.Owner, owner) || source.Character.Id != character.Id)
                throw new InvalidOperationException("Town move without current membership");
            bool changed = source.Character.Position != character.Position || source.ScaledSpeed != scaledSpeed;
            if (AlignPhase(source, character, "Town move without current membership", scaledSpeed))
                return true;
            source.Character = character;
            // 实时速度只归本次场景成员，不加入角色位置或持久化快照。
            source.ScaledSpeed = scaledSpeed;
            if (!changed) return true;
            var peers = Peers(source);
            if (peers.Length == 0)
                return true;
            var bodies = Capture(source);
            foreach (var target in peers)
                target.Sender.Move(Projection(source, target, bodies));
            return true;
        }
    });
    // 同一场景换装只更新快照。完成任务换成新的 PVF 阶段时先迁场景，再按入场投递。
    public void Refresh(AccountOwners.Lease owner, GameConnectionSender sender, Character character, string? adventureName = null,
        bool broadcast = true, bool marriageOnly = false) => owner.Execute(() =>
    {
        lock (gate)
        {
            if (!members.TryGetValue(sender, out var source) || !ReferenceEquals(source.Owner, owner) || source.Character.Id != character.Id)
                throw new InvalidOperationException("外观刷新缺少当前场景成员");
            if (adventureName is not null) source.AdventureName = adventureName;
            if (AlignPhase(source, character, "外观刷新缺少当前场景成员"))
                return true;
            bool clearMarriage = source.Character.Marriage?.Active == true && character.Marriage?.Active != true;
            source.Character = character;
            if (!broadcast) return true;
            var peers = Peers(source);
            if (peers.Length == 0)
                return true;
            if (marriageOnly)
            {
                foreach (var target in peers) PublishMarriage(source, target, clearMarriage);
                return true;
            }
            var bodies = Capture(source);
            foreach (var target in peers)
            {
                target.Sender.Refresh(Projection(source, target, bodies));
                PublishMarriage(source, target, clearMarriage);
            }
            return true;
        }
    });
    // 完成任务会换掉同一城镇区域的 PVF 阶段。人还在，只是地图路径变了。
    // 旧阶段同屏离场，新阶段入场；城镇或区域变了仍视为没有当前成员。
    bool AlignPhase(Member source, Character character, string mismatch, ushort? scaledSpeed = null)
    {
        var next = SceneOf(source.Owner, character, source.Channel);
        if (source.Scene == next)
            return false;
        if (next.Channel != source.Channel || next.Town != source.Scene.Town || next.Area != source.Scene.Area)
            throw new InvalidOperationException(mismatch);
        if (scaledSpeed is { } speed) source.ScaledSpeed = speed;
        var previous = scenes[source.Scene];
        previous.Remove(source);
        if (previous.Count == 0)
            scenes.Remove(source.Scene);
        else
            foreach (var target in previous.ToArray())
                target.Sender.Leave(source.Character.Id, source.Serial, hideActor: true);
        // 同town/area不能借异区23隐藏实体；阶段隔离只隐藏地图实体，不销毁队伍引用。
        var cleared = source.Sender.ClearScene(SceneActorClear.Hide);
        cleared.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        source.Scene = next;
        source.Character = character;
        if (!scenes.TryGetValue(next, out var scene))
            scenes.Add(next, scene = []);
        scene.Add(source);
        var peers = Peers(source);
        if (peers.Length == 0)
            return true;
        var bodies = Capture(source);
        foreach (var peer in peers)
        {
            source.Sender.Enter(Projection(peer, source, Capture(peer)));
            peer.Sender.Enter(Projection(source, peer, bodies));
            PublishMarriage(peer, source);
            PublishMarriage(source, peer);
            if (expertStores?.TryGet(peer.Character.Id, out var store) == true)
                source.Sender.Post(0, (ushort)NotiPacketType.CREATE_EXPERT_JOB_STORE,
                    ExpertJobPackets.CreateStoreNotice(store), "expert_job_existing_store",
                    () => Visible(peer, source) && expertStores.TryGet(store.OwnerId, out var live) && live.Instance == store.Instance);
        }
        return true;
    }

    // 精确清理仅持成员锁，不获取任何账号锁。
    public void Remove(GameConnectionSender sender, byte[]? departureEffect = null, byte[]? prepareEffect = null, byte[]? destinationArea = null)
    {
        lock (gate)
            RemoveCore(sender, departureEffect, prepareEffect, destinationArea);
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
