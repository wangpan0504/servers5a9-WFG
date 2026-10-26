using DfoServer.Game.Dungeons;
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
        var party = origin.PartyDungeon;
        if (party is null && (!ReferenceEquals(origin, destination) || records.Any(row => row.Appearance is null && row.Kind != 0))) return false;
        if (party is not null && !ReferenceEquals(party, destination.PartyDungeon)) return false;
        var capture = records.Any(row => row.Appearance is not null) ? origin.CaptureNpcFavorDungeon() : null;
        if (records.Any(row => row.Appearance is { } appearance && (capture is null ||
            row.Position is { } position && position != capture.Snapshot.CurrentMap.Position || !favorNpcs!.Validate(capture, appearance))))
            return false;
        var partyGate = party is null ? null : origin.CurrentParty?.Invoke()?.Commands;
        if (party is not null && partyGate is null) return false;
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
                    if (records.Any(row => row.Appearance is not null))
                        log.Write("npc_favor_dungeon", new { Source = source.Context.Id, Target = target.Context.Id,
                            Instance = initial.Snapshot.Identity.InstanceId, Map = initial.Snapshot.CurrentMap.Id, Sent = sent,
                            Npc = origin.ReadNpcFavorAppearance(capture!)?.NpcId });
                    return sent;
                }
                return party is null ? Publish() : party.Execute(_ => Publish());
            }
            finally { source.Context.CommandGate.Release(); }
        }
        finally { partyGate?.Release(); }

        bool Current() => IsRegistered(source.Entry) && IsRegistered(targetEntry) && ReferenceEquals(source.Entry.Current, source) &&
            ReferenceEquals(targetEntry.Current, target) && ReferenceEquals(origin.PartyDungeon, party) &&
            ReferenceEquals(destination.PartyDungeon, party);
    }
}
