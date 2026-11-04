using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record AntonDungeonObjectScope(Guid Battle, Guid Run, long Generation, long RoomEpoch,
    ushort HostCharacter, long HostEpoch);
internal sealed record AntonDungeonObjectDestructionFact(long EventId, AntonDungeonObjectScope Scope,
    DungeonPoint Room, int SourceOrdinal, int SelectionOrdinal, DungeonRandomizedObject Resource,
    ushort ObjectKind, ushort ObjectKey);
internal sealed record AntonNativeObjectSpawn(uint Template, ushort ObjectKind, ushort ObjectKey,
    byte PositionMode, int? X, int? Y, int? Z);
internal sealed record AntonNativeObjectSpawnBatch(ushort RoomX, ushort RoomY, ImmutableArray<AntonNativeObjectSpawn> Rows)
{
    public ushort SourceKind { get; init; }
    public ushort SourceKey { get; init; }
    public ushort ParentKind { get; init; }
    public ushort ParentKey { get; init; }
    public uint Mode { get; init; }
    public uint Tick { get; init; }
}
internal sealed record AntonNativeObjectDestruction(ushort ObjectKind, ushort ObjectKey, DungeonPoint Room);
internal sealed record AntonObjectReport(bool Accepted, bool EnergyChanged, int BatteryCount);

// 只存当前共享Run已冻结的资源许可及其真实Native身份；调用方在共享副本锁下复验当前host/owner。
// 销毁事实和Last执行事实分开：本账本不把离场清理自动转换为符号或资产变化。
internal sealed class AntonDungeonObjectLedger(DungeonRandomizedObjectSelection selection, TimeProvider? timeProvider = null)
{
    sealed record Permit(int SourceOrdinal, int SelectionOrdinal, bool Regenerate, DungeonRandomizedObject Resource);
    sealed record Binding(Permit Permit, ushort Kind, ushort Key, AntonDungeonObjectDestructionFact? Destruction = null,
        long DestroyedAt = 0, bool LastConsumed = false);
    readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    internal static readonly TimeSpan PairWindow = TimeSpan.FromSeconds(30);
    readonly ImmutableArray<Permit> all = selection.Groups.SelectMany(group => group.Objects.Select((row, index) =>
        new Permit(group.SourceOrdinal, index, group.Regenerate, row))).ToImmutableArray();
    readonly HashSet<(int Source, int Selected)> consumed = [];
    readonly Dictionary<(ushort Kind, ushort Key), Binding> bindings = [];
    // Native23不携带生成代，故共享Run内不得重新授权已出现的实例身份；不能将它误配给回访的新对象。
    // 只记录已批准的对象，以上限限制异常长Run的保留量，不缓存任意自报身份。
    internal const int MaximumIdentities = ushort.MaxValue + 1;
    readonly HashSet<(ushort Kind, ushort Key)> identities = [];
    readonly Dictionary<(ushort Kind, ushort Key), long> earlyDestructions = [];
    readonly Queue<long> lastRequests = [];
    AntonDungeonObjectScope? activeScope;
    DungeonPoint activeRoom;
    ImmutableArray<Permit> permits = [];
    // 资源的Cave_02是218共享Run本地计数，只随已配对的小电池Last推进，不是Battle符号。
    internal int BatteryLastCount { get; private set; }
    internal int PendingLastCount => lastRequests.Count;
    internal int PendingDestructionCount => earlyDestructions.Count;

    public ImmutableArray<DungeonRandomizedObject> OpenRoom(AntonDungeonObjectScope scope, DungeonPoint room)
    {
        if (scope.Battle == Guid.Empty || scope.Run == Guid.Empty || scope.Generation < 0 || scope.RoomEpoch < 0)
            throw new ArgumentException("对象许可必须绑定有效战役、共享Run及房间代", nameof(scope));
        if (activeScope == scope && activeRoom == room)
            return permits.Where(row => !AlreadyDestroyed(row)).Select(row => row.Resource).ToImmutableArray();
        activeScope = scope; activeRoom = room; bindings.Clear();
        earlyDestructions.Clear(); lastRequests.Clear();
        permits = all.Where(row => row.Resource.Map == room &&
            !consumed.Contains((row.SourceOrdinal, row.SelectionOrdinal))).ToImmutableArray();
        return permits.Select(row => row.Resource).ToImmutableArray();
    }

    public bool Register(AntonDungeonObjectScope scope, DungeonPoint room, uint template,
        ushort kind, ushort key, int x, int y, int z)
    {
        if (scope != activeScope || room != activeRoom || z != 0) return false;
        if (bindings.TryGetValue((kind, key), out var prior))
            return prior.Destruction is null && Matches(prior.Permit);
        if (identities.Contains((kind, key)) || identities.Count >= MaximumIdentities) return false;
        var matches = permits.Where(row => Matches(row) && !AlreadyBound(row)).ToArray();
        // 相同模板和坐标的多份许可不能靠自报实例键消除资源歧义。
        if (matches.Length != 1) return false;
        bindings.Add((kind, key), new(matches[0], kind, key));
        identities.Add((kind, key));
        Prune();
        if (earlyDestructions.Remove((kind, key), out long observed) &&
            TryDestroy(scope, room, kind, key, out _))
            bindings[(kind, key)] = bindings[(kind, key)] with { DestroyedAt = observed };
        return true;
        bool Matches(Permit permit) => permit.Resource.SpawnMode == 1 &&
            permit.Resource.ObjectId == template && permit.Resource.PositionX == x && permit.Resource.PositionY == y;
    }

    public bool TryDestroy(AntonDungeonObjectScope scope, DungeonPoint room, ushort kind, ushort key,
        out AntonDungeonObjectDestructionFact? fact)
    {
        fact = null;
        if (scope != activeScope || room != activeRoom || !bindings.TryGetValue((kind, key), out var binding) ||
            binding.Destruction is not null) return false;
        fact = new(EventId(binding.Permit.SourceOrdinal, binding.Permit.SelectionOrdinal), scope, room,
            binding.Permit.SourceOrdinal, binding.Permit.SelectionOrdinal,
            binding.Permit.Resource, kind, key);
        bindings[(kind, key)] = binding with { Destruction = fact, DestroyedAt = clock.GetTimestamp() };
        if (!binding.Permit.Regenerate) consumed.Add((binding.Permit.SourceOrdinal, binding.Permit.SelectionOrdinal));
        return true;
    }

    internal static long EventId(int source, int selected) => ((long)source + 1) << 32 | (uint)(selected + 1);

    // UDP23可先于21。未绑定的候选最多占本格许可数，随后21仍须独立匹配服务端许可。
    internal bool ObserveDestruction(AntonDungeonObjectScope scope, DungeonPoint room, ushort kind, ushort key)
    {
        if (scope != activeScope || room != activeRoom) return false;
        Prune();
        if (bindings.ContainsKey((kind, key))) return TryDestroy(scope, room, kind, key, out _);
        if (identities.Contains((kind, key)) || earlyDestructions.ContainsKey((kind, key)) ||
            earlyDestructions.Count >= permits.Length) return false;
        earlyDestructions.Add((kind, key), clock.GetTimestamp());
        return true;
    }

    internal bool ReportBatteryLast(AntonDungeonObjectScope scope, DungeonPoint room)
    {
        if (scope != activeScope || room != activeRoom) return false;
        Prune();
        int remaining = permits.Count(Battery) - bindings.Values.Count(binding => Battery(binding.Permit) && binding.LastConsumed);
        if (lastRequests.Count >= remaining) return false;
        lastRequests.Enqueue(clock.GetTimestamp());
        return true;
    }

    // 双事实齐备后才提交；业务拒绝不消费，已经提交的稳定事件重放由业务以成功确认以清理本地等待。
    internal int ApplyBatteryLast(AntonDungeonObjectScope scope, DungeonPoint room,
        Func<AntonDungeonObjectDestructionFact, bool> commit)
    {
        if (scope != activeScope || room != activeRoom) return 0;
        Prune();
        int applied = 0;
        foreach (var binding in bindings.Values.Where(binding => Battery(binding.Permit) &&
            binding.Destruction is not null && !binding.LastConsumed &&
            clock.GetElapsedTime(binding.DestroyedAt) <= PairWindow).ToArray())
        {
            if (lastRequests.Count == 0) break;
            if (!commit(binding.Destruction!)) continue;
            lastRequests.Dequeue();
            bindings[(binding.Kind, binding.Key)] = binding with { LastConsumed = true };
            BatteryLastCount++;
            applied++;
        }
        return applied;
    }

    static bool Battery(Permit permit) => !permit.Regenerate && permit.Resource is { ObjectId: 18865, SpawnMode: 1 };
    void Prune()
    {
        while (lastRequests.TryPeek(out long timestamp) && clock.GetElapsedTime(timestamp) > PairWindow) lastRequests.Dequeue();
        foreach (var key in earlyDestructions.Where(row => clock.GetElapsedTime(row.Value) > PairWindow).Select(row => row.Key).ToArray())
            earlyDestructions.Remove(key);
    }

    // 28初始投影与29用同一选择；资源声明不再生的消耗从后续投影中移除，空组仍保留。
    public ImmutableArray<ImmutableArray<DungeonPoint>> MinimapGroups => selection.Groups
        .Where(group => group.MinimapIcon.HasValue).Select(group => group.Objects
            .Where((_, index) => !Unavailable(group.SourceOrdinal, index))
            .Select(row => row.Map).Distinct().ToImmutableArray()).ToImmutableArray();

    public DungeonRandomizedObjectSelection Projection => new(selection.Groups.Select(group => group with
    {
        Objects = group.Objects.Where((_, index) => !Unavailable(group.SourceOrdinal, index)).ToImmutableArray()
    }).ToImmutableArray());

    bool Unavailable(int source, int selected) => consumed.Contains((source, selected)) ||
        bindings.Values.Any(binding => binding.Permit.SourceOrdinal == source &&
            binding.Permit.SelectionOrdinal == selected && binding.Destruction is not null);

    bool AlreadyBound(Permit permit) => bindings.Values.Any(binding => binding.Permit == permit);
    bool AlreadyDestroyed(Permit permit) => bindings.Values.Any(binding => binding.Permit == permit && binding.Destruction is not null);
}
