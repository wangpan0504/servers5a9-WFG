namespace DfoServer.Network.Protocol;

// 证明来自实际取锁而非调用者身份；只在本次Dispatch期间有效，不保存长期会话锁状态。
// 共享门禁按队伍、决斗房间、命令自定义的顺序解析；取得命令门禁后复核身份，换了就整轮重来。
internal sealed class DispatchGateScope : IDisposable
{
    readonly GameConnectionContext context;
    readonly SemaphoreSlim? shared;
    bool active = true;

    DispatchGateScope(GameConnectionContext context, SemaphoreSlim? shared)
    { this.context = context; this.shared = shared; }

    public bool Holds(GameConnectionContext caller, SemaphoreSlim gate) => active &&
        ReferenceEquals(context, caller) && ReferenceEquals(context.DispatchGates, this) &&
        (ReferenceEquals(gate, context.CommandGate) || ReferenceEquals(gate, shared));

    public static Task<DispatchGateScope> EnterAsync(GameConnectionContext context, Packet request, GameCommandGate? commandGate = null) =>
        EnterAsync(context, () => Resolve(context, request, commandGate));

    // 服务端自发路径（如阶段超时退场）没有请求包与命令门禁，只按当前队伍、决斗房间解析共享门禁。
    public static Task<DispatchGateScope> EnterAsync(GameConnectionContext context, CancellationToken stop) =>
        EnterAsync(context, () => context.Session.CurrentParty?.Invoke()?.Commands ?? context.Session.CurrentPvpArena?.Room.Commands, stop);

    static async Task<DispatchGateScope> EnterAsync(GameConnectionContext context, Func<SemaphoreSlim?> resolve, CancellationToken stop = default)
    {
        if (stop == default) stop = context.Stop;
        while (true)
        {
            var shared = resolve();
            if (shared is not null) await shared.WaitAsync(stop);
            bool commandHeld = false;
            try
            {
                await context.CommandGate.WaitAsync(stop);
                commandHeld = true;
                context.Owner.Execute(() => true);
                if (ReferenceEquals(shared, resolve()))
                    return new(context, shared);
            }
            catch
            {
                if (commandHeld) context.CommandGate.Release();
                shared?.Release();
                throw;
            }
            context.CommandGate.Release();
            shared?.Release();
        }
    }

    // 每轮重新解析：队伍、决斗房间与命令自定义门禁都可能在此期间切换，必须按当前身份复核。
    static SemaphoreSlim? Resolve(GameConnectionContext context, Packet request, GameCommandGate? commandGate) =>
        context.Session.CurrentParty?.Invoke()?.Commands ?? context.Session.CurrentPvpArena?.Room.Commands ??
        commandGate?.Invoke(context, request);

    public bool HasShared => shared is not null;

    public void Dispose()
    {
        if (!active) return;
        active = false;
        if (ReferenceEquals(context.DispatchGates, this)) context.DispatchGates = null;
        context.CommandGate.Release();
        shared?.Release();
    }
}
