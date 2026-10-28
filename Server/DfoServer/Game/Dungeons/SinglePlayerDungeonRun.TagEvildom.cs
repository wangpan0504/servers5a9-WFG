using DfoServer.Game.TagEvildom;

namespace DfoServer.Game.Dungeons;

internal sealed partial class SinglePlayerDungeonRun
{
    TagEvildomRunState? tagEvildom;
    internal TagEvildomAdmission? TagEvildomAdmission { get; private set; }
    internal TagEvildomLifeSnapshot? TagEvildomLife => tagEvildom?.Snapshot;
    internal DfoServer.Game.ImperialArena.ImperialArenaAdmission? ImperialArenaAdmission { get; private set; }

    internal bool InstallImperialArena(DfoServer.Game.ImperialArena.ImperialArenaAdmission admission, int switchCooldownMilliseconds)
    {
        admission.Validate();
        if (tagEvildom is not null || Phase != SinglePlayerDungeonPhase.Loading || allRooms.Any(room => room.Loaded) ||
            !entry.IsImperialArena || entry.DungeonId != admission.DungeonId || entry.CharacterId != admission.Primary.Id ||
            entry.QuestId != 0 || entry.PartyRoute is not null || !entry.DisableMonsterAndClearExperience ||
            !entry.DisableFatigueConsumption || !entry.DisableGiveupPenalty) return false;
        tagEvildom = new(identity.InstanceId, new(admission.Primary, admission.Standby, admission.Configuration.Revision),
            imperialArena: true, switchCooldownMilliseconds: switchCooldownMilliseconds);
        ImperialArenaAdmission = admission;
        return true;
    }

    // 仅供未来真实准入提交安装；同账号与编队revision须由该owner事务复核，本方法不是授权能力。
    // 不创建Run、不绕过DungeonEntryPlan的TAG入口guard。
    internal bool InstallTagEvildom(TagEvildomParticipants participants, TagEvildomAdmission? admission = null)
    {
        if (tagEvildom is not null || Phase != SinglePlayerDungeonPhase.Loading || allRooms.Any(room => room.Loaded) ||
            entry.DungeonId is not (9608 or 400000095) || entry.CharacterId != participants.Primary.Id ||
            entry.CharacterRevision != participants.Primary.Revision || entry.QuestId != 0 ||
            entry.PartyRoute is not null || entry.IsTutorial || entry.IsPractice || entry.IsTrainingRoom || entry.IsClassExperience || entry.IsTournament ||
            !entry.DisableMonsterAndClearExperience || !entry.DisableFatigueConsumption || !entry.DisableGiveupPenalty)
            return false;
        if (admission is not null)
        {
            admission.Validate();
            if (admission.DungeonId != entry.DungeonId || admission.Primary.Id != participants.Primary.Id ||
                admission.Standby.Id != participants.Standby.Id || admission.Configuration.Revision != participants.ConfigurationRevision)
                return false;
        }
        tagEvildom = new(identity.InstanceId, participants);
        TagEvildomAdmission = admission;
        return true;
    }

    internal TagEvildomLifeResult? SwitchTagEvildom(DungeonRunIdentity expected, byte ordinal, long nowMilliseconds = 0) =>
        CanChangeTagLife(expected) ? tagEvildom!.Switch(ordinal, nowMilliseconds) : null;
    internal TagEvildomLifeResult? ReportTagEvildomDeath(DungeonRunIdentity expected, byte ordinal, long now) =>
        CanChangeTagLife(expected) ? tagEvildom!.ReportDeath(ordinal, now) : null;
    internal TagEvildomRevivalTimeout? CurrentTagEvildomRevival() =>
        CanChangeTagLife(identity) ? tagEvildom!.NextRevival() : null;
    internal TagEvildomLifeResult? ReviveTagEvildom(TagEvildomRevivalTimeout expected, long now) =>
        CanChangeTagLife(identity) ? tagEvildom!.Revive(expected, now) : null;
    internal void CancelTagEvildom() => tagEvildom?.Cancel();

    bool CanChangeTagLife(DungeonRunIdentity expected) => tagEvildom is not null && Matches(expected) &&
        Phase == SinglePlayerDungeonPhase.Dungeon && !HasPendingDeath && !HasPendingSettlement;
}
