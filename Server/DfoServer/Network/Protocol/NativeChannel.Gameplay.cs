using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;

namespace DfoServer.Network.Protocol;

internal sealed partial class NativeChannel
{
    async Task<bool> RelayAsync(NativePacket packet, NativeLease source)
    {
        if (!registrations.TryGetValue(packet.Route, out var targetEntry) || Volatile.Read(ref targetEntry.Current) is not { } target ||
            !IsRegistered(source.Entry) || !IsRegistered(targetEntry) || !ReferenceEquals(source.Entry.Current, source)) return false;
        NativeGameplayRecord[] records;
        try { records = NativeGameplayRecords.Read(packet.Body); }
        catch (InvalidDataException) { return false; }
        if (records.Length == 0) return false;
        var origin = source.Context.Session;
        var destination = target.Context.Session;
        var initial = origin.CurrentFormalDungeonScene();
        var peerScene = destination.CurrentFormalDungeonScene();
        if (initial is null || peerScene is null || initial.Snapshot.Identity.InstanceId != peerScene.Snapshot.Identity.InstanceId ||
            initial.Snapshot.CurrentMap != peerScene.Snapshot.CurrentMap || records.Any(row => row.SourceSlot != initial.PartySlot)) return false;
        // 只预解码，不改变转发接受规则；未知/坏Native5仍按既有中继合同处理。
        var observations = initial.Entry.RaidEntry is { Kind: RaidKind.Luke }
            ? records.Select(row => (Row: row, State: NativeObjectStates.TryRead(row,
                initial.Snapshot.CurrentMap.Position, out var state) ? state : null)).Where(x => x.State is not null).ToArray()
            : [];
        var objectSpawns = initial.Entry.RaidEntry is { Kind: RaidKind.Anton }
            ? records.Select(row => NativePassiveObjectSpawns.TryRead(row, initial.Snapshot.CurrentMap.Position, out var batch)
                ? batch : null).Where(batch => batch is not null).Select(batch => new AntonNativeObjectSpawnBatch(
                    batch!.RoomX, batch.RoomY, [.. batch.Rows.Select(row => new AntonNativeObjectSpawn(row.Template,
                        row.ObjectKind, row.ObjectKey, row.PositionMode, row.X, row.Y, row.Z))])
                    { SourceKind = batch.SourceKind, SourceKey = batch.SourceKey, ParentKind = batch.ParentKind,
                        ParentKey = batch.ParentKey, Mode = batch.Mode, Tick = batch.Tick }).ToArray()
            : [];
        var objectDestructions = initial.Entry.RaidEntry is { Kind: RaidKind.Anton }
            ? records.Select(row => NativeObjectDestructions.TryRead(row, initial.Snapshot.CurrentMap.Position, out var destruction)
                ? destruction : null).Where(row => row is not null).Select(row =>
                    new AntonNativeObjectDestruction(row!.ObjectKind, row.ObjectKey, row.Room)).ToArray()
            : [];
        var party = origin.PartyDungeon;
        if (party is null && (!ReferenceEquals(origin, destination) || records.Any(row => row.Appearance is null && row.Kind != 0))) return false;
        if (party is not null && !ReferenceEquals(party, destination.PartyDungeon)) return false;
        var capture = records.Any(row => row.Appearance is not null) ? origin.CaptureNpcFavorDungeon() : null;
        if (records.Any(row => row.Appearance is { } appearance && (capture is null ||
            row.Position is { } position && position != capture.Snapshot.CurrentMap.Position || !favorNpcs!.Validate(capture, appearance))))
            return false;
        var partyGate = party is null ? null : origin.CurrentParty?.Invoke()?.Commands;
        if (party is not null && partyGate is null) return false;
        bool energyChanged = false, published = false;
        if (partyGate is not null) await partyGate.WaitAsync(source.Context.Stop);
        try
        {
            await source.Context.CommandGate.WaitAsync(source.Context.Stop);
            try
            {
                using var peers = party is null ? null : await PartyDungeonProjection.AcquirePeersAsync(source.Context, party.Members);
                bool Publish()
                {
                    if (!Current() || !origin.CanPublishNativeScene(initial) || !destination.CanPublishNativeScene(peerScene)) return false;
                    if (party is null && capture is null && (origin.CaptureNpcFavorDungeon() is not { } cleared ||
                        !origin.HasNpcFavorAppearance(cleared))) return false;
                    using var bytes = new MemoryStream();
                    foreach (var row in records)
                    {
                        if (row.Appearance is not { } appearance) { bytes.Write(row.Wire.Span); continue; }
                        var selected = party?.ReadNpcFavorAppearance() ?? origin.ReadNpcFavorAppearance(capture!) ?? appearance;
                        if (party is null ? !origin.ConfirmNpcFavorAppearance(capture!, selected) :
                            !party.ConfirmNpcFavorAppearance(origin, destination, capture!, selected)) return false;
                        bytes.Write(NativeGameplayRecords.WithAppearance(row, selected));
                    }
                    var body = bytes.ToArray();
                    bool CanSend() => Current() && origin.CanPublishNativeScene(initial) && destination.CanPublishNativeScene(peerScene);
                    bool sent = target.Post(NativeFrame.Build(source.Entry.Token, targetEntry.Token, body), CanSend);
                    if (sent)
                    {
                        if (party is not null && (objectSpawns.Length != 0 || objectDestructions.Length != 0))
                            energyChanged |= party.ObserveNativeObjects(origin, initial, objectSpawns, objectDestructions).EnergyChanged;
                        foreach (var observation in observations)
                        {
                            var result = source.StateObservations.Observe(initial.Snapshot.Identity,
                                initial.Snapshot.CurrentMap, observation.Row, observation.State!);
                            var counts = source.StateObservations.Snapshot();
                            // 仅新增计数的2次幂输出汇总，重传/冲突不制造逐包热点日志。
                            if (result == NativeObservationResult.Added && (counts.Added & (counts.Added - 1)) == 0)
                                log.Write("luke_native_state_observation", new { Queued = true, counts.Added,
                                    counts.Duplicates, counts.Conflicts, counts.Retained });
                        }
                    }
                    if (records.Any(row => row.Appearance is not null))
                        log.Write("npc_favor_dungeon", new { Source = source.Context.Id, Target = target.Context.Id,
                            Instance = initial.Snapshot.Identity.InstanceId, Map = initial.Snapshot.CurrentMap.Id, Sent = sent,
                            Npc = origin.ReadNpcFavorAppearance(capture!)?.NpcId });
                    return sent;
                }
                published = party is null ? Publish() : party.Execute(_ => Publish());
            }
            finally { source.Context.CommandGate.Release(); }
        }
        finally { partyGate?.Release(); }
        // 阶段发布可能取得全团owner；退出共享副本Execute及本次peer/command门禁后再发布。
        if (energyChanged) origin.PublishRaidCombat();
        return published;

        bool Current() => IsRegistered(source.Entry) && IsRegistered(targetEntry) && ReferenceEquals(source.Entry.Current, source) &&
            ReferenceEquals(targetEntry.Current, target) && ReferenceEquals(origin.PartyDungeon, party) &&
            ReferenceEquals(destination.PartyDungeon, party);
    }
}
