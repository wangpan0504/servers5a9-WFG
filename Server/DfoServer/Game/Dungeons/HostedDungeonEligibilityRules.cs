namespace DfoServer.Game.Dungeons;

internal static class HostedDungeonEligibilityRules
{
    public const string ReasonMissingCombatProfile = "missing-combat-profile";
    public const string ReasonNotOrdinary = "not-ordinary";
    public const string ReasonPartyDungeon = "party-dungeon";
    public const string ReasonScriptedReturn = "scripted-return";
    public const string ReasonNavigationReplay = "navigation-replay";
    public const string ReasonHell = "hell";
    public const string ReasonRaid = "raid";
    public const string ReasonSpecialNormal = "special-normal";
    public const string ReasonTagSystem = "tag-system";
    public const string ReasonAltar = "altar";
    public const string ReasonHope = "hope";
    public const string ReasonLostLand = "lost-land";
    public const string ReasonDimension = "dimension";
    public const string ReasonAncient = "ancient";
    public const string ReasonMarriage = "marriage";
    public const string ReasonGrief = "grief";
    public const string ReasonBossRush = "boss-rush";
    public const string ReasonTournament = "tournament";
    public const string ReasonTraining = "training";
    public const string ReasonQuestReplay = "quest-replay";
    public const string ReasonPartyRoute = "party-route";
    public const string ReasonEvildomWar = "evildom-war";
    public const string ReasonStaleReply = "stale-reply";
    public const string ReasonOverBudget = "over-budget";
    public const string ReasonTimeout = "timeout";
    public const string ReasonCancelled = "cancelled";
    public const string ReasonEof = "eof";
    public const string ReasonInvalidReply = "invalid-reply";
    public const string ReasonUnsupportedProfile = "unsupported-combat-profile";
    public const string ReasonCanFightWithoutProfile = "can-fight-without-profile";
    public const string ReasonDisabled = "disabled";

    public static HostedDungeonEligibilityResult Refuse(string reason, Guid probeId) =>
        new(false, reason, probeId);

    public static string? RejectReason(DungeonEntryPlan entry, bool partyDungeon, bool scriptedReturn, bool navigationReplay)
    {
        if (partyDungeon) return ReasonPartyDungeon;
        if (scriptedReturn) return ReasonScriptedReturn;
        if (navigationReplay) return ReasonNavigationReplay;
        if (entry.HellParty is not null || entry.IsHellLegend || entry.IsInfernoParty) return ReasonHell;
        if (entry.RaidEntry is not null) return ReasonRaid;
        if (entry.IsOrdinaryAnton || entry.IsOrdinaryLuke || entry.IsOrdinaryPrey) return ReasonSpecialNormal;
        if (entry.IsTagEvildom || entry.IsImperialArena) return ReasonTagSystem;
        if (entry.IsAltar || entry.IsDespair) return ReasonAltar;
        if (entry.Hope is not null) return ReasonHope;
        if (entry.LostLand is not null) return ReasonLostLand;
        if (entry.Dimension is not null) return ReasonDimension;
        if (entry.IsAncientInfinite) return ReasonAncient;
        if (entry.Marriage is not null) return ReasonMarriage;
        if (entry.GriefLayer is not null) return ReasonGrief;
        if (entry.IsBossRush) return ReasonBossRush;
        if (entry.IsTournament) return ReasonTournament;
        if (entry.IsPractice || entry.IsTrainingRoom || entry.IsClassExperience || entry.IsTutorial) return ReasonTraining;
        if (entry.IsQuestReplay) return ReasonQuestReplay;
        if (entry.PartyRoute is not null) return ReasonPartyRoute;
        if (entry.IsEvildomWarHidden) return ReasonEvildomWar;
        if (!entry.IsOrdinary) return ReasonNotOrdinary;
        return null;
    }

    // 生成等级用 CandidateLevel；有显式 BattleLevel 时不得改填 CandidateLevel。
    public static HostedDungeonEligibilityWireSpawn[] Spawns(IEnumerable<DungeonPlannedMonster> monsters) =>
        monsters.Select(monster => new HostedDungeonEligibilityWireSpawn(
            monster.ObjectKey, monster.MonsterId, monster.CandidateLevel, monster.BattleLevel)).ToArray();

    public static HostedDungeonEligibilityWireRequest Request(HostedDungeonEligibilityCapture capture) =>
        new(capture.ProbeId, capture.Generation, capture.SelectionEpoch, capture.CharacterRevision, capture.CharacterId,
            new HostedDungeonEligibilityWireIdentity(capture.Identity.CharacterId, capture.Identity.QuestId,
                capture.Identity.TaskAttempt, capture.Identity.InstanceId, capture.Identity.Epoch),
            capture.DungeonId, capture.QuestId, capture.MapPosition.X, capture.MapPosition.Y, capture.MapId, capture.Spawns);

    public static HostedDungeonEligibilityResult FromReply(HostedDungeonEligibilityWireReply? reply, Guid expectedProbeId)
    {
        if (reply is null) return Refuse(ReasonInvalidReply, expectedProbeId);
        if (reply.ProbeId != expectedProbeId) return Refuse(ReasonStaleReply, expectedProbeId);
        if (reply.CanFight) return Refuse(ReasonCanFightWithoutProfile, expectedProbeId) with { ProfileSchema = reply.ProfileSchema };
        if (!string.IsNullOrEmpty(reply.ProfileSchema))
            return Refuse(ReasonUnsupportedProfile, expectedProbeId) with { ProfileSchema = reply.ProfileSchema };
        string reason = string.IsNullOrWhiteSpace(reply.Reason) ? ReasonMissingCombatProfile : reply.Reason;
        return new(false, reason, expectedProbeId, reply.ProfileSchema);
    }
}
