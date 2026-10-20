using DfoServer.Game.Characters;

namespace DfoServer.Game.Session;

internal sealed record GrowUpChangeResult(ushort Error, string Reason, Character? Character, long Generation, long Epoch,
    SessionPhase Phase, int Count, GrowUpChangePlan? Plan = null, bool Quoted = false);

internal sealed partial class GameSession
{
    // 785收费询价：首包confirm=0只报价(result133)，客户端确认窗口再发confirm=1。确认必须对应同一角色快照、
    // 选角周期、目标、次数和价格；任何一项变化都不按新价格悄悄扣费。
    readonly record struct GrowUpQuote(ushort CharacterId, long Revision, long Epoch, byte Target, int Count, int Fee);
    GrowUpQuote? growUpQuote;

    public int GrowUpChangeCount() => owner.Execute(() =>
        Selected is { } character ? repository.LoadGrowUpChangeCount(owner, character.Id) : 0);

    public GrowUpChangeResult ChangeGrowUp(GrowUpChangeService service, GrowUpChangeRequest request, DateTimeOffset now)
    {
        bool Allowed() => Phase == SessionPhase.Town && CanPlanAdventureRepresentative();
        var capture = owner.Execute(() => (Allowed: Allowed(), Selected, SelectionEpoch, Phase, owner.Generation));
        GrowUpChangeResult Rejected(ushort error, string reason, int count = 0) => new(error, reason, capture.Selected,
            capture.Generation, capture.SelectionEpoch, capture.Phase, count);
        if (!capture.Allowed || capture.Selected is not { } before) return Rejected(30, "not-ready-town");
        int count = repository.LoadGrowUpChangeCount(owner, before.Id);
        GrowUpChangePreparation prepared;
        try { prepared = service.Prepare(before, count, request.Target, now); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
        { prepared = new(23, "unsupported-class-resource: " + error.Message[..Math.Min(error.Message.Length, 160)]); }
        return owner.Execute(() =>
        {
            var quote = growUpQuote;
            growUpQuote = null;
            if (!Allowed() || SelectionEpoch != capture.SelectionEpoch || !ReferenceEquals(Selected, before))
                return Rejected(30, "stale-growup-change", count);
            if (prepared.Plan is not { } plan) return Rejected(prepared.Error, prepared.Reason, count);
            var offered = new GrowUpQuote(before.Id, before.Revision, SelectionEpoch, request.Target, count, plan.Fee);
            if (!request.Confirmed && plan.Fee > 0)
            {
                growUpQuote = offered;
                return new GrowUpChangeResult(0, "quoted", before, owner.Generation, SelectionEpoch, Phase, count, plan, Quoted: true);
            }
            if (request.Confirmed && quote != offered) return Rejected(23, "unquoted-confirmation", count);
            var committed = repository.CommitGrowUpChange(owner, plan);
            Selected = committed.Character;
            return new GrowUpChangeResult(0, "committed", committed.Character, owner.Generation, SelectionEpoch, Phase,
                committed.Count, plan);
        });
    }

    public bool CanPublishGrowUpChange(GrowUpChangeResult result) => owner.Execute(() =>
        result.Generation == owner.Generation && result.Epoch == SelectionEpoch && result.Phase == Phase &&
        ReferenceEquals(result.Character, Selected));
}
