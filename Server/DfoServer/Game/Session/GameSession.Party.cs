using DfoServer.Game.Parties;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    SingleMemberParty? singleParty;
    long singlePartyVersion;
    public SingleMemberParty? ReadSoloParty() => owner.Execute(() => singleParty);

    bool CanChangeSoloParty() => Ready && Phase == SessionPhase.Town && Selected is not null && pendingRental is null &&
        !HasPendingTownQuestCommit && !HasActiveDungeonContinuation && !HasPendingQuestReplay && !HasPendingFormalCard;

    public SoloPartyReceipt? SetSoloParty(SoloPartyRequest request, bool blocked = false) => owner.Execute(() =>
    {
        if (blocked || !CanChangeSoloParty() || request.Mode > 1 || request.Mode == 1 && singleParty is null ||
            request.Settings.MemberCode is < 1 or > 8 || request.Settings.TitlePreset == 0 && string.IsNullOrWhiteSpace(request.Settings.Title)) return null;
        bool created = singleParty is null;
        if (singleParty?.Settings != request.Settings)
        {
            singleParty = new(Selected!.Id, request.Settings);
            singlePartyVersion++;
        }
        return new SoloPartyReceipt(singleParty!, owner.Generation, SelectionEpoch, created, Version: singlePartyVersion);
    });

    public SoloPartyReceipt? LeaveSoloParty(bool blocked = false) => owner.Execute(() =>
    {
        if (blocked || !CanChangeSoloParty() || singleParty is not { } party) return null;
        singleParty = null;
        singlePartyVersion++;
        return new SoloPartyReceipt(party, owner.Generation, SelectionEpoch, false, Removed: true, Version: singlePartyVersion);
    });

    public bool CanPublishSoloParty(SoloPartyReceipt receipt) => owner.Execute(() => Ready && Phase == SessionPhase.Town &&
        receipt.Generation == owner.Generation && receipt.Version == singlePartyVersion && MatchesSelection(receipt.SelectionEpoch, receipt.Party.LeaderId) &&
        (receipt.Removed ? singleParty is null : ReferenceEquals(singleParty, receipt.Party)));
}
