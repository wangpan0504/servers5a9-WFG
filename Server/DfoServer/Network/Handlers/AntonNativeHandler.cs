using DfoServer.Game.Dungeons;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class AntonNativeHandler : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.RegisterPrivate(AntonNativeRequests.Opcode, Report, nameof(AntonNativeHandler),
            Game.Session.UserStateRequirement.DungeonContext,
            sharedGate: static (context, _) => context.Session.CurrentParty?.Invoke()?.Commands);

    async Task<CommandResult> Report(GameConnectionContext context, Packet packet)
    {
        AntonNativeObservation report;
        try { report = AntonNativeRequests.Parse(packet.Body); }
        catch (InvalidDataException) { return CommandResult.Handled; }
        var room = context.AntonNativeRoom;
        if (room is null || report.Serial != room.Serial || report.Sequence <= room.LastSequence ||
            report.Dungeon != room.Receipt.Entry.DungeonId || report.Map != room.Receipt.Snapshot.CurrentMap.Id ||
            report.Seed != room.Receipt.MapSeed ||
            report.Position != (uint)(room.Receipt.Snapshot.CurrentMap.Position.X | room.Receipt.Snapshot.CurrentMap.Position.Y << 8))
            return CommandResult.Handled;
        AntonNativeObjectSpawnBatch[] spawns = [];
        AntonNativeObjectDestruction[] destructions = [];
        var position = room.Receipt.Snapshot.CurrentMap.Position;
        if (report.NativeOpcode == 21)
        {
            if (!NativePassiveObjectSpawns.TryReadPlain(report.Plain, position, out var batch)) return CommandResult.Handled;
            spawns = [new(batch!.RoomX, batch.RoomY, [.. batch.Rows.Select(row => new AntonNativeObjectSpawn(
                row.Template, row.ObjectKind, row.ObjectKey, row.PositionMode, row.X, row.Y, row.Z))])
            { SourceKind = batch.SourceKind, SourceKey = batch.SourceKey, ParentKind = batch.ParentKind,
                ParentKey = batch.ParentKey, Mode = batch.Mode, Tick = batch.Tick }];
        }
        else
        {
            if (!NativeObjectDestructions.TryReadPlain(report.Plain, position, out var death)) return CommandResult.Handled;
            destructions = [new(death!.ObjectKind, death.ObjectKey, death.Room)];
        }
        var instance = context.Session.PartyDungeon;
        var party = context.Session.CurrentParty?.Invoke();
        if (instance is null || party is null || context.DispatchGates?.Holds(context, party.Commands) != true)
            return CommandResult.Handled;
        using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, instance.Members);
        if (!ReferenceEquals(context.AntonNativeRoom, room) || !ReferenceEquals(context.Session.PartyDungeon, instance) ||
            !ReferenceEquals(context.Session.CurrentParty?.Invoke(), party) ||
            context.Session.CurrentAntonNativeScene(room.Receipt) is not { } scene)
            return CommandResult.Handled;
        room.LastSequence = report.Sequence;
        var result = instance.ObserveNativeObjects(context.Session, scene, spawns, destructions);
        // 发布可取得其他分队owner，必须先退出本次peer、队伍和本人命令门禁。
        if (result.EnergyChanged) context.AfterDispatch = () =>
        {
            context.Session.PublishRaidCombat();
            return Task.CompletedTask;
        };
        return CommandResult.Handled;
    }
}
