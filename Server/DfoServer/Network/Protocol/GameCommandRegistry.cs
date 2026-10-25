using System.Collections.Frozen;
using DfoServer.Game.Session;

namespace DfoServer.Network.Protocol;

internal enum CommandResult
{
    Handled,
    Unhandled,
    CloseConnection
}

internal delegate Task<CommandResult> GameCommandHandler(GameConnectionContext context, Packet request);
internal interface IGameCommandModule
{
    void Register(GameCommandRegistry commands);
}

internal sealed class GameCommandRegistry
{
    sealed record Registration(string Source, GameCommandHandler Handler, UserStateRequirement MinimumState);
    readonly Dictionary<ushort, Registration> registrations = [];
    FrozenDictionary<ushort, Registration>? commands;
    public int Count => commands?.Count ?? registrations.Count;
    public void Register(CmdPacketType opcode, GameCommandHandler handler, string source,
        UserStateRequirement minimumState = UserStateRequirement.Owner)
    {
        if (commands is not null)
            throw new InvalidOperationException("Command registry is already sealed");
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (opcode >= CmdPacketType.END)
            throw new ArgumentOutOfRangeException(nameof(opcode));
        if (!Enum.IsDefined(minimumState)) throw new ArgumentOutOfRangeException(nameof(minimumState));
        ushort value = (ushort)opcode;
        if (registrations.TryGetValue(value, out var previous))
            throw new InvalidOperationException($"Duplicate command {opcode} ({value}): {previous.Source} / {source}");
        registrations.Add(value, new(source, handler, minimumState));
    }

    public void Seal()
    {
        if (commands is not null)
            throw new InvalidOperationException("Command registry is already sealed");
        commands = registrations.ToFrozenDictionary();
        registrations.Clear();
    }

    public async Task<CommandResult> DispatchAsync(GameConnectionContext context, Packet request)
    {
        if (commands is null)
            throw new InvalidOperationException("Command registry is not sealed");
        // 保留旧owner拒绝的优先级；等待门禁期间可能接管，取得后仍须复验。
        context.Owner.Execute(() => true);
        CommandResult result = CommandResult.Unhandled;
        while (true)
        {
            var partyGate = context.Session.CurrentParty?.Invoke()?.Commands;
            if (partyGate is not null) await partyGate.WaitAsync(context.Stop);
            try
            {
                await context.CommandGate.WaitAsync(context.Stop);
                try
                {
                    context.Owner.Execute(() => true);
                    if (!ReferenceEquals(partyGate, context.Session.CurrentParty?.Invoke()?.Commands)) continue;
                    // 队伍门禁协调所有成员命令；本人应答也只能入队，不能等慢连接拖住整队。
                    using var publication = partyGate is null ? null : context.Sender.QueuePublication(() =>
                    {
                        long epoch = context.Session.SelectionEpoch;
                        return () => context.Owner.IsCurrent && context.Session.SelectionEpoch == epoch && !context.Stop.IsCancellationRequested;
                    });
                    if (commands.TryGetValue(request.Opcode, out var command) && context.Session.CheckState(command.MinimumState))
                    {
                        if (context.Creatures is { } creatures) await creatures.BeforeCommandAsync(request);
                        result = await command.Handler(context, request);
                    }
                    // 异步处理结束后仍须属于当前账号；旧连接不能继续下一条业务。
                    context.Owner.Execute(() => true);
                    break;
                }
                finally { context.CommandGate.Release(); }
            }
            finally { partyGate?.Release(); }
        }
        if (result == CommandResult.CloseConnection) await context.Sender.DrainAsync();
        return result;
    }
}
