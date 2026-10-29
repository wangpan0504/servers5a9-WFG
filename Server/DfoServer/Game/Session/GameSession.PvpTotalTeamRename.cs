using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Pvp;
using DfoServer.Game.Social;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public PvpTotalTeamRenameResult RenamePvpTotalTeam(PvpTotalTeamService teams, string name, ushort slot, bool blocked = false)
    {
        if (!PvpTotalTeam.ValidName(name)) return new(PvpTotalTeamRenameFailure.InvalidName);
        var admission = blocked ? null : CapturePvpMatchingAdmission();
        if (admission is null) return new(PvpTotalTeamRenameFailure.Unavailable);
        var team = admission.Actor.Execute(() => teams.Read(owner));
        if (team is null) return new(PvpTotalTeamRenameFailure.MissingTeam);
        var plan = teams.PrepareRename(admission.Actor.Character, team, name, slot);
        if (plan is null) return new(PvpTotalTeamRenameFailure.InvalidCard);
        return admission.Actor.Execute(() =>
        {
            if (!CanRemainPvpMatching || pvpMatching is not null || !ReferenceEquals(Selected, plan.Expected))
                return new PvpTotalTeamRenameResult(PvpTotalTeamRenameFailure.Unavailable);
            PvpTotalTeamRenameCommit? committed;
            try { committed = teams.CommitRename(owner, plan); }
            catch (InventoryQualificationExpiredException) { return new(PvpTotalTeamRenameFailure.Expired); }
            if (committed is null) return new(PvpTotalTeamRenameFailure.NameTaken);
            Selected = committed.Character;
            var actor = new SocialActor(owner, this, Selected, SelectionEpoch);
            var inventory = plan.Inventory is { } changed ? new InventoryResult(true, 0, changed)
            {
                Receipt = new(Selected, owner.Generation, SelectionEpoch)
                { Phase = Phase, PreviousQuests = plan.Expected.ActiveMainQuests,
                    PreviousSkillSwitchInventory = plan.Expected.SkillSwitchInventory }
            } : null;
            return new(PvpTotalTeamRenameFailure.None, committed.Team, actor, inventory);
        });
    }

    internal bool CanPublishPvpTotalTeamRename(SocialActor actor) => CanRemainPvpMatching && pvpMatching is null &&
        ReferenceEquals(actor.Owner, owner) && ReferenceEquals(actor.Session, this) && actor.IsCurrent() &&
        ReferenceEquals(Selected, actor.Character);
}
