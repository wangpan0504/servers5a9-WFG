using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

internal enum FiendwarKeyLeaseKind : byte { Offer = 1, Candidates, Grant, Ready, Cancel, Refill }
internal readonly record struct FiendwarKeyLeaseScene(Guid Run, uint Dungeon, uint Map, uint Seed, uint Position, uint Serial, long Epoch);
internal sealed record FiendwarKeyLeaseMessage(FiendwarKeyLeaseKind Kind, byte Status, Guid Handshake,
    FiendwarKeyLeaseScene Scene, ulong Lease, ImmutableArray<ushort> Keys)
{
    internal const int BatchCapacity = 64;
    internal const int LowWaterTarget = 16;
    internal const int TimeoutMilliseconds = 10000;
}

// 归原正式Run共享；不保存角色快照或数据库。原owner组及队伍命令门禁串行调用。
internal sealed class FiendwarObjectKeyLeases(Guid run)
{
    sealed class Member(ulong id)
    {
        internal ulong Id { get; } = id;
        internal HashSet<ushort> Granted { get; } = [];
        internal HashSet<ushort> Usable { get; } = [];
        internal HashSet<ushort> Pending { get; } = [];
        internal Guid Batch;
        internal FiendwarKeyLeaseScene Scene;
        internal long Deadline;
        internal ImmutableArray<ushort> LastCandidates = [];
        internal ImmutableArray<ushort> LastGrant = [];
        internal bool Ready;
        internal Guid LastRefillRequest;
    }

    readonly Dictionary<ushort, Member> members = [];
    readonly object gate = new();
    readonly HashSet<ushort> reserved = [];
    readonly HashSet<ushort> consumed = [];
    ulong nextLease;
    internal Guid Run { get; } = run;
    internal bool RecoveryRequired { get; private set; }
    internal Guid RecoveryHandshake { get; private set; }
    internal FiendwarKeyLeaseScene RecoveryScene { get; private set; }
    internal bool OwnsLease(ushort character, ulong lease)
    {
        lock (gate) return members.TryGetValue(character, out var member) && member.Id == lease;
    }
    internal bool OwnsKey(ushort character, ulong lease, ushort key)
    {
        lock (gate) return members.TryGetValue(character, out var member) && member.Id == lease && member.Granted.Contains(key);
    }
    internal bool MatchesBatch(ushort character, FiendwarKeyLeaseMessage message)
    {
        lock (gate) return members.TryGetValue(character, out var member) && member.Id == message.Lease &&
            member.Batch == message.Handshake && member.Scene == message.Scene;
    }
    internal FiendwarKeyLeaseMessage? RecoveryMessage(ushort character)
    {
        lock (gate)
            return RecoveryRequired && members.TryGetValue(character, out var member) && member.Batch != Guid.Empty
                ? Message(member, FiendwarKeyLeaseKind.Cancel, [], 2) : null;
    }
    internal void RequireRecovery(Guid handshake, FiendwarKeyLeaseScene scene)
    {
        lock (gate)
        {
            if (scene.Run != Run || handshake == Guid.Empty) throw new InvalidOperationException("漩涡恢复不属于原Run");
            if (RecoveryRequired) return;
            RecoveryRequired = true;
            RecoveryHandshake = handshake;
            RecoveryScene = scene;
        }
    }
    internal bool Contains(ushort key)
    {
        lock (gate) return reserved.Contains(key);
    }
    internal bool IsMember(ushort character)
    {
        lock (gate) return members.ContainsKey(character);
    }
    internal bool IsReady(ushort character)
    {
        lock (gate) return members.TryGetValue(character, out var member) && member.Ready;
    }
    internal int Available(ushort character)
    {
        lock (gate) return members.TryGetValue(character, out var member) ? member.Usable.Count(key => !consumed.Contains(key)) : 0;
    }

    internal FiendwarKeyLeaseMessage Offer(ushort character, FiendwarKeyLeaseScene scene, long now, bool renew = false)
    {
        lock (gate)
        {
            if (character == 0 || scene.Run != Run || Run == Guid.Empty || scene.Dungeon == 0 || scene.Map == 0 || scene.Epoch < 0)
                throw new InvalidOperationException("漩涡对象键租约不属于原Run");
            if (!members.TryGetValue(character, out var member))
            {
                if (members.Count >= 4) throw new InvalidOperationException("漩涡遭遇租约成员超过原分队容量");
                members.Add(character, member = new(++nextLease));
            }
            if (renew || member.Batch == Guid.Empty || member.Scene != scene || now >= member.Deadline)
            {
                member.Batch = Guid.NewGuid();
                member.Scene = scene;
                member.Deadline = checked(now + FiendwarKeyLeaseMessage.TimeoutMilliseconds);
                member.LastCandidates = [];
                member.LastGrant = [];
                member.Pending.Clear();
            }
            return Message(member, FiendwarKeyLeaseKind.Offer, [], FiendwarKeyLeaseMessage.LowWaterTarget);
        }
    }

    internal FiendwarKeyLeaseMessage? Grant(ushort character, FiendwarKeyLeaseMessage request, long now,
        Func<ushort, bool> free)
    {
        lock (gate)
        {
            if (!Current(character, request, now, out var member) || request.Kind != FiendwarKeyLeaseKind.Candidates ||
                request.Keys.Length is < 1 or > FiendwarKeyLeaseMessage.BatchCapacity ||
                request.Keys.Any(key => key is 0 or ushort.MaxValue) || request.Keys.Distinct().Count() != request.Keys.Length)
                return null;
            if (member.LastCandidates.SequenceEqual(request.Keys))
                return Message(member, FiendwarKeyLeaseKind.Grant, member.LastGrant);
            int needed = Math.Max(0, FiendwarKeyLeaseMessage.LowWaterTarget - Available(character) - member.Pending.Count);
            var accepted = ImmutableArray.CreateBuilder<ushort>();
            foreach (ushort key in request.Keys)
            {
                if (accepted.Count >= needed) break;
                if (reserved.Contains(key) || !free(key)) continue;
                reserved.Add(key);
                member.Granted.Add(key);
                member.Pending.Add(key);
                accepted.Add(key);
            }
            member.LastCandidates = request.Keys;
            member.LastGrant = accepted.ToImmutable();
            return Message(member, FiendwarKeyLeaseKind.Grant, member.LastGrant);
        }
    }

    internal bool Confirm(ushort character, FiendwarKeyLeaseMessage request, long now)
    {
        lock (gate)
        {
            // 候选过期不能新增授键；已完整授出的当前场景ready允许网络迟到，初次入场serial0仍守原截止。
            if (RecoveryRequired || !Current(character, request, now, out var member, ignoreDeadline: request.Scene.Serial != 0) ||
                request.Kind != FiendwarKeyLeaseKind.Ready ||
                request.Lease != member.Id || !request.Keys.IsEmpty) return false;
            if (Available(character) + member.Pending.Count(key => !consumed.Contains(key)) < FiendwarKeyLeaseMessage.LowWaterTarget) return false;
            foreach (ushort key in member.Pending) if (!consumed.Contains(key)) member.Usable.Add(key);
            member.Ready = true;
            member.Pending.Clear();
            return true;
        }
    }

    internal FiendwarKeyLeaseMessage? Refill(ushort character, FiendwarKeyLeaseMessage request, long now)
    {
        lock (gate)
        {
            if (!members.TryGetValue(character, out var member) || member.Id != request.Lease || request.Scene.Run != Run ||
                Available(character) >= FiendwarKeyLeaseMessage.LowWaterTarget || RecoveryRequired) return null;
            if (member.LastRefillRequest == request.Handshake && member.Scene == request.Scene)
                return Offer(character, request.Scene, now);
            member.LastRefillRequest = request.Handshake;
            return Offer(character, request.Scene, now, renew: true);
        }
    }

    internal bool CanUse(ushort character, ulong lease, ushort key)
    {
        lock (gate) return members.TryGetValue(character, out var member) && !RecoveryRequired && member.Ready &&
            member.Id == lease && member.Usable.Contains(key) && !consumed.Contains(key);
    }
    internal void Consume(ushort character, ulong lease, ushort key)
    {
        lock (gate)
        {
            if (!CanUse(character, lease, key) || !consumed.Add(key))
                throw new InvalidOperationException("漩涡对象键租约已采用或不属于报告者");
        }
    }

    // 撤销的是本批新握手；已授键可能存在客户端在途确认，整Run保守占位，不能退给Ground。
    internal bool CancelBatch(ushort character, FiendwarKeyLeaseMessage request)
    {
        lock (gate)
        {
            if (!members.TryGetValue(character, out var member) || member.Id != request.Lease || member.Batch != request.Handshake || member.Scene != request.Scene)
                return false;
            member.Batch = Guid.Empty;
            member.LastCandidates = [];
            member.LastGrant = [];
            member.Pending.Clear();
            return true;
        }
    }

    bool Current(ushort character, FiendwarKeyLeaseMessage request, long now, out Member member, bool ignoreDeadline = false)
    {
        if (members.TryGetValue(character, out var found) && found.Batch != Guid.Empty && found.Batch == request.Handshake &&
            found.Scene == request.Scene && (ignoreDeadline || now < found.Deadline) && request.Status == 0 &&
            request.Lease != 0 && request.Lease == found.Id) { member = found; return true; }
        member = null!;
        return false;
    }
    static FiendwarKeyLeaseMessage Message(Member member, FiendwarKeyLeaseKind kind, ImmutableArray<ushort> keys, byte status = 0) =>
        new(kind, status, member.Batch, member.Scene, member.Id, keys);
}
