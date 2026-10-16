using DfoServer.Game.Characters;

namespace DfoServer.Game.Session;

internal sealed record ClassChangeResult(ushort Error, string Reason, Character? Character, long Generation, long Epoch,
    SessionPhase Phase, ClassChangePlan? Plan = null);

internal sealed partial class GameSession
{
    public ClassChangeResult ChangeClass(ClassChangeService service, ClassChangeRequest request, DateTimeOffset now)
    {
        bool Allowed() => Phase == SessionPhase.Town && CanPlanAdventureRepresentative();
        var capture = owner.Execute(() => (Allowed: Allowed(), Selected, SelectionEpoch, Phase, owner.Generation));
        ClassChangeResult Rejected(ushort error, string reason) => new(error, reason, capture.Selected,
            capture.Generation, capture.SelectionEpoch, capture.Phase);
        if (!capture.Allowed || capture.Selected is not { } before) return Rejected(30, "not-ready-town");
        ClassChangePreparation prepared;
        try { prepared = service.Prepare(before, request, now); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
        { return Rejected(23, "unsupported-class-resource: " + error.Message[..Math.Min(error.Message.Length, 160)]); }
        if (prepared.Plan is not { } plan) return Rejected(prepared.Error, prepared.Reason);
        return owner.Execute(() =>
        {
            if (!Allowed() || SelectionEpoch != capture.SelectionEpoch || !ReferenceEquals(Selected, before))
                return Rejected(30, "stale-class-change");
            var committed = repository.CommitClassChange(owner, plan);
            Selected = committed;
            return new ClassChangeResult(0, "committed", committed, owner.Generation, SelectionEpoch, Phase, plan);
        });
    }

    public bool CanPublishClassChange(ClassChangeResult result) => owner.Execute(() =>
        result.Generation == owner.Generation && result.Epoch == SelectionEpoch && result.Phase == Phase &&
        ReferenceEquals(result.Character, Selected));
}
