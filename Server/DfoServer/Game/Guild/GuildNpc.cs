using DfoServer.GameWorld.Guilds;

namespace DfoServer.Game.Guild;

internal sealed record GuildNpcProgress(uint NpcId, int Level, long UpgradeUntil, long ContractUntil);
internal sealed record GuildNpcState(GuildManagementState Management, GuildNpcProgress[] Npcs)
{
    public GuildNpcCraftProgress[] Crafts { get; init; } = [];
}
internal sealed record GuildNpcCommit(int Error, uint Action, GuildNpcState? State = null, bool Replayed = false)
{
    public bool Success => Error == 0;
}
internal interface IGuildNpcStore
{
    GuildNpcState? ReadGuildNpcs(GuildManagementIdentity actor, GuildNpcCatalog catalog, long now);
    GuildNpcCommit UpgradeGuildNpc(GuildManagementIdentity actor, GuildNpcCatalog catalog, uint npcId,
        uint argument, string requestId, long now);
    GuildNpcCommit ContractGuildNpc(GuildManagementIdentity actor, GuildNpcCatalog catalog, uint npcId,
        uint argument, string requestId, long now);
    GuildNpcState[] CompleteGuildNpcUpgrades(GuildNpcCatalog catalog, GuildParameterCatalog parameters, long now);
    GuildNpcCommit StartGuildNpcCraft(GuildManagementIdentity actor, GuildNpcCatalog catalog, GuildNpcCraftCatalog crafts,
        uint benefitId, string requestId, long now);
    GuildNpcCommit CancelGuildNpcCraft(GuildManagementIdentity actor, GuildNpcCatalog catalog, GuildNpcCraftCatalog crafts,
        uint benefitId, string requestId, long now);
    GuildNpcState[] CompleteGuildNpcCrafts(GuildNpcCatalog catalog, GuildNpcCraftCatalog crafts, long now);
}

internal sealed class GuildNpcService : IDisposable
{
    readonly IGuildNpcStore store;
    readonly GuildNpcCatalog catalog;
    readonly GuildParameterCatalog parameters;
    readonly GuildManagementService management;
    readonly GuildNpcCraftCatalog? crafts;
    readonly Action<Exception>? onError;
    readonly TimeProvider clock;
    readonly ITimer timer;
    readonly object lifecycle = new();
    int disposed;
    int active;
    int ticking;
    public GuildNpcService(IGuildNpcStore store, GuildNpcCatalog catalog, GuildParameterCatalog parameters, GuildManagementService management,
        Action<Exception>? onError = null, TimeProvider? clock = null, GuildNpcCraftCatalog? crafts = null)
    {
        this.store = store; this.catalog = catalog; this.parameters = parameters; this.management = management;
        this.onError = onError; this.clock = clock ?? TimeProvider.System;
        this.crafts = crafts;
        // 只持有一个有界轮询计时器；到期事实保存在数据库，重开服务仍会继续完成。
        timer = this.clock.CreateTimer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }
    public long Now => clock.GetUtcNow().ToUnixTimeSeconds();
    public GuildNpcState? Read(GuildManagementIdentity actor)
    {
        if (!Enter()) return null;
        try { return actor.IsCurrent() ? store.ReadGuildNpcs(actor, catalog, Now) : null; }
        finally { Leave(); }
    }
    public GuildNpcCommit Change(GuildManagementIdentity actor, uint action, uint npcId, uint argument, string requestId)
    {
        if (!Enter()) return new(1, action);
        try
        {
            return action switch
            {
                1 => store.UpgradeGuildNpc(actor, catalog, npcId, argument, requestId, Now),
                2 => store.ContractGuildNpc(actor, catalog, npcId, argument, requestId, Now),
                4 when npcId == 0 && crafts is not null => store.StartGuildNpcCraft(actor, catalog, crafts,
                    argument, requestId, Now),
                5 when npcId == 0 && crafts is not null => store.CancelGuildNpcCraft(actor, catalog, crafts,
                    argument, requestId, Now),
                _ => new(1, action)
            };
        }
        finally { Leave(); }
    }
    public void Publish(GuildNpcState state) => management.Publish(new(0, state.Management.Guild.Id,
        state.Management.Revision, Members: state.Management.Members) { NpcsChanged = true });
    public void Publish(GuildNpcCommit commit)
    {
        if (commit.Success && commit.State is { } state) Publish(state);
    }
    void Tick()
    {
        if (Interlocked.CompareExchange(ref ticking, 1, 0) != 0) return;
        if (!Enter()) { Volatile.Write(ref ticking, 0); return; }
        try
        {
            foreach (var state in store.CompleteGuildNpcUpgrades(catalog, parameters, Now))
                Publish(state);
            if (crafts is not null)
                foreach (var state in store.CompleteGuildNpcCrafts(catalog, crafts, Now)) Publish(state);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
        { onError?.Invoke(error); }
        finally { Leave(); Volatile.Write(ref ticking, 0); }
    }
    bool Enter()
    {
        lock (lifecycle)
        {
            if (disposed != 0) return false;
            active++; return true;
        }
    }
    void Leave()
    {
        lock (lifecycle) { active--; if (active == 0) Monitor.PulseAll(lifecycle); }
    }
    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed != 0) return;
            disposed = 1;
            timer.Dispose();
            // 门禁只登记在途操作，不持有它进入账号owner、SQLite或广播，避免与会话回调形成反向锁序。
            while (active != 0) Monitor.Wait(lifecycle);
        }
    }
}
