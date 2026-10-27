namespace DfoServer.Network.Protocol;

// 证明来自实际取锁而非调用者身份；只在本次Dispatch期间有效，不保存长期会话锁状态。
internal sealed class DispatchGateScope : IDisposable
{
    readonly GameConnectionContext context;
    readonly SemaphoreSlim? party;
    bool active = true;

    DispatchGateScope(GameConnectionContext context, SemaphoreSlim? party)
    { this.context = context; this.party = party; }

    public bool Holds(GameConnectionContext caller, SemaphoreSlim gate) => active &&
        ReferenceEquals(context, caller) && ReferenceEquals(context.DispatchGates, this) &&
        (ReferenceEquals(gate, context.CommandGate) || ReferenceEquals(gate, party));

    public static async Task<DispatchGateScope> EnterAsync(GameConnectionContext context, CancellationToken? stop = null)
    {
        while (true)
        {
            var party = context.Session.CurrentParty?.Invoke()?.Commands;
            if (party is not null) await party.WaitAsync(stop ?? context.Stop);
            bool commandHeld = false;
            try
            {
                await context.CommandGate.WaitAsync(stop ?? context.Stop);
                commandHeld = true;
                context.Owner.Execute(() => true);
                if (ReferenceEquals(party, context.Session.CurrentParty?.Invoke()?.Commands))
                    return new(context, party);
            }
            catch
            {
                if (commandHeld) context.CommandGate.Release();
                party?.Release();
                throw;
            }
            context.CommandGate.Release();
            party?.Release();
        }
    }

    public bool HasParty => party is not null;

    public void Dispose()
    {
        if (!active) return;
        active = false;
        if (ReferenceEquals(context.DispatchGates, this)) context.DispatchGates = null;
        context.CommandGate.Release();
        party?.Release();
    }
}
