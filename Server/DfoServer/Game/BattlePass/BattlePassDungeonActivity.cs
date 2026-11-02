using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

// 普通、帝国逐轮、祭坛、单人漩涡引导死亡及原疲劳消费分别取原事实；其它专用死亡仍未接入。
internal sealed class BattlePassDungeonActivity
{
    readonly DungeonEntryPlan? entry;
    readonly PreparedDungeonDeath? death;
    readonly AntonRaidBattle.FiendwarGuideDeath? guideDeath;
    readonly Character? altarCharacter;
    readonly QuestCatalog? altarQuests;
    public DungeonRunIdentity Identity { get; }
    public string SourceKind { get; }
    public string SourceId { get; }
    public int Amount { get; }
    public bool IsBoss { get; }
    public TimeProvider Clock { get; }
    public DateTimeOffset PlannedAt { get; }

    private BattlePassDungeonActivity(DungeonRunIdentity identity, string kind, string id, int amount,
        bool boss, TimeProvider clock, DungeonEntryPlan? entry = null, PreparedDungeonDeath? death = null,
        Character? altarCharacter = null, QuestCatalog? altarQuests = null,
        AntonRaidBattle.FiendwarGuideDeath? guideDeath = null)
    {
        Identity=identity;SourceKind=kind;SourceId=id;Amount=amount;IsBoss=boss;Clock=clock;
        PlannedAt=guideDeath?.CapturedAt ?? clock.GetUtcNow();this.entry=entry;this.death=death;
        this.altarCharacter=altarCharacter;this.altarQuests=altarQuests;
        this.guideDeath=guideDeath;
    }

    public static BattlePassDungeonActivity? PrepareDeath(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared,
        TimeProvider clock, AntonRaidBattle? battle = null, RaidActor? actor = null)
    {
        var guideDeath = actor is null ? null : battle?.CaptureFiendwarGuideDeath(actor,run,prepared);
        bool guide = EligibleGuideDeath(run,prepared,guideDeath);
        if(!guide && !EligibleDeath(run,prepared))return null;
        string kind=guide ? "fiendwar_guide_monster_death" :
            prepared.ImperialArenaRound is null ? "normal_formal_monster_death" : "imperial_arena_monster_death";
        return new(prepared.Death.Identity,kind,
            SourceKey(kind,FormattableString.Invariant($"{prepared.Death.Identity.CharacterId}:{prepared.Death.EventId:N}")),
            1,prepared.Monster.IsBoss,clock,run.Entry,prepared,
            guideDeath:guide ? guideDeath : null);
    }

    static bool EligibleGuideDeath(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared,
        AntonRaidBattle.FiendwarGuideDeath? guideDeath)
    {
        var e=run.Entry;var m=prepared.Monster;var id=prepared.Death.Identity;
        return guideDeath is not null && run.IsPendingDeath(prepared) &&
            id==run.Snapshot.Identity && id.CharacterId==e.CharacterId && id.InstanceId!=Guid.Empty &&
            prepared.Death.EventId!=Guid.Empty && !e.IsPractice && !e.IsTrainingRoom && !e.IsTutorial &&
            !e.IsClassExperience && !e.IsQuestReplay && !e.IsAltar && !e.IsTagSystem &&
            m.MonsterId>0 && m.CanReportDeath && !m.IsFriendly && !m.IsSpecialChild && m.AiCharacter is null &&
            guideDeath.Matches(run,prepared);
    }

    public static BattlePassDungeonActivity? PrepareAltarQuestTarget(SinglePlayerDungeonRun run,
        PreparedDungeonDeath prepared, Character expected, QuestCatalog quests, TimeProvider clock)
    {
        if(!EligibleAltarQuestTarget(run,prepared,expected,quests))return null;
        const string kind="altar_quest_target_monster_death";
        return new(prepared.Death.Identity,kind,
            SourceKey(kind,FormattableString.Invariant($"{prepared.Death.Identity.CharacterId}:{prepared.Death.EventId:N}")),
            1,prepared.Monster.IsBoss,clock,run.Entry,prepared,expected,quests);
    }

    public static BattlePassDungeonActivity? PrepareAltarNonTarget(SinglePlayerDungeonRun run,
        PreparedDungeonDeath prepared, Character expected, QuestCatalog quests, TimeProvider clock)
    {
        if(!EligibleAltarNonTarget(run,prepared,expected,quests))return null;
        const string kind="altar_non_target_monster_death";
        var id=prepared.Death.Identity;var map=run.Snapshot.CurrentMap;
        return new(id,kind,SourceKey(kind,FormattableString.Invariant(
            $"{id.CharacterId}:{id.QuestId}:{id.TaskAttempt}:{id.InstanceId:N}:{id.Epoch}:{map.Id}:{map.Position.X}:{map.Position.Y}:{prepared.Death.MonsterKey}:{prepared.Death.CombatSequence}:{prepared.Death.EventId:N}")),
            1,prepared.Monster.IsBoss,clock,run.Entry,prepared,expected,quests);
    }

    static bool EligibleAltarNonTarget(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared,
        Character expected, QuestCatalog quests)
    {
        var e=run.Entry;var m=prepared.Monster;var id=prepared.Death.Identity;
        return e.IsAltar && run.IsPendingDeath(prepared) && id==run.Snapshot.Identity &&
            id.CharacterId==e.CharacterId && id.CharacterId==expected.Id && id.InstanceId!=Guid.Empty &&
            prepared.Death.EventId!=Guid.Empty && !e.IsPractice && !e.IsTrainingRoom && !e.IsTutorial &&
            !e.IsClassExperience && !e.IsQuestReplay && e.RaidEntry is null && !e.IsTagSystem &&
            m.MonsterId>0 && m.CanReportDeath && !m.IsFriendly && !m.IsSpecialChild && m.AiCharacter is null &&
            run.CurrentAltar is { Started:true } altar && altar.WasPublished(prepared.Death.MonsterKey) &&
            QuestHuntProgress.MonsterDeath(expected.ActiveMainQuests,e,quests,m).SequenceEqual(expected.ActiveMainQuests);
    }

    public bool MatchesAltarNonTarget(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared,
        Character expected, QuestCatalog quests) => SourceKind=="altar_non_target_monster_death" &&
        altarCharacter is {} original && original.Id==expected.Id &&
        original.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) &&
        ReferenceEquals(altarQuests,quests) && MatchesDeath(run,prepared);

    static bool EligibleAltarQuestTarget(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared,
        Character expected, QuestCatalog quests)
    {
        var e=run.Entry;var m=prepared.Monster;var id=prepared.Death.Identity;
        return e.IsAltar && run.IsPendingDeath(prepared) && id==run.Snapshot.Identity &&
            id.CharacterId==e.CharacterId && id.CharacterId==expected.Id && id.InstanceId!=Guid.Empty &&
            prepared.Death.EventId!=Guid.Empty && !e.IsPractice && !e.IsTrainingRoom && !e.IsTutorial &&
            !e.IsClassExperience && !e.IsQuestReplay && e.RaidEntry is null && !e.IsTagSystem &&
            m.MonsterId>0 && m.CanReportDeath && !m.IsFriendly && !m.IsSpecialChild && m.AiCharacter is null &&
            run.CurrentAltar is { Started:true } altar && altar.WasPublished(prepared.Death.MonsterKey) &&
            !QuestHuntProgress.MonsterDeath(expected.ActiveMainQuests,e,quests,m).SequenceEqual(expected.ActiveMainQuests);
    }

    public bool MatchesAltarQuestTarget(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared,
        Character expected, QuestCatalog quests) => SourceKind=="altar_quest_target_monster_death" &&
        altarCharacter is {} original && original.Id==expected.Id &&
        original.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) &&
        ReferenceEquals(altarQuests,quests) && MatchesDeath(run,prepared);

    static bool EligibleDeath(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared)
    {
        var e=run.Entry;var m=prepared.Monster;var id=prepared.Death.Identity;
        if(!run.IsPendingDeath(prepared) || id!=run.Snapshot.Identity || id.CharacterId!=e.CharacterId ||
            id.InstanceId==Guid.Empty || prepared.Death.EventId==Guid.Empty || e.IsPractice || e.IsTrainingRoom ||
            e.IsTutorial || e.IsClassExperience || e.IsQuestReplay || e.RaidEntry is not null || e.IsAltar ||
            m.MonsterId<=0 || !m.CanReportDeath || m.IsFriendly || m.IsSpecialChild || m.AiCharacter is not null)return false;
        if(!e.IsTagSystem)return e.IsOrdinary && prepared.ImperialArenaRound is null;
        // 只接原帝国当前轮的真实对手，不以TAG标记或副本名扩大其它专用场景。
        if(e.QuestId!=0 || e.RouteQuestId!=0 || run.ImperialArenaAdmission is not {} admission ||
            run.TagEvildomLife is not { Defeated:false,Cancelled:false } || admission.Primary.Id!=id.CharacterId ||
            admission.DungeonId!=e.DungeonId || prepared.ImperialArenaRound is not {} round)return false;
        var opponents=admission.Route.Opponents(admission.Mode);
        return round>0 && round<=opponents.Length && opponents[round-1].MonsterId==m.MonsterId;
    }

    public bool MatchesDeath(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared) =>
        ReferenceEquals(entry,run.Entry) && ReferenceEquals(death,prepared) && run.IsPendingDeath(prepared) &&
        Identity==prepared.Death.Identity && Identity==run.Snapshot.Identity &&
        (SourceKind=="fiendwar_guide_monster_death"
            ? EligibleGuideDeath(run,prepared,guideDeath)
            : SourceKind=="altar_quest_target_monster_death"
            ? altarCharacter is {} character && altarQuests is {} quests && EligibleAltarQuestTarget(run,prepared,character,quests)
            : SourceKind=="altar_non_target_monster_death"
                ? altarCharacter is {} original && altarQuests is {} catalog && EligibleAltarNonTarget(run,prepared,original,catalog)
                : EligibleDeath(run,prepared));

    public static BattlePassDungeonActivity? PrepareFatigue(DungeonRunIdentity identity, string scope, int unit,
        CharacterFatigueState before, CharacterFatigueState after, TimeProvider clock)
    {
        before.Validate();after.Validate();
        int amount=before.Remaining-after.Remaining;
        if(identity.CharacterId==0 || identity.InstanceId==Guid.Empty || identity.TaskAttempt<=0 || identity.Epoch<=0 ||
            scope is not ("entry" or "room" or "hope_area") || unit<0 || amount<=0 ||
            after != before with { Used=(ushort)Math.Min(before.Maximum,before.Used+amount),Remaining=after.Remaining }) return null;
        return new(identity,"dungeon_fatigue_spend",SourceKey("dungeon_fatigue_spend", FormattableString.Invariant($"{identity.InstanceId:N}:{identity.CharacterId}:{scope}:{unit}")),amount,false,clock);
    }

    // 完整业务键先做域隔离摘要，再取16字节满足原来源表32hex合同；不截断原身份维度。
    static string SourceKey(string kind, string identity) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + identity)).AsSpan(0, 16)).ToLowerInvariant();

    public bool MatchesMission(byte category, BattlePassMission mission) => category==0 && mission.RawParameter==-1 &&
        mission.Experience==100 && (SourceKind is "fiendwar_guide_monster_death" or "normal_formal_monster_death" or "imperial_arena_monster_death" or "altar_quest_target_monster_death" or "altar_non_target_monster_death" && (mission is
            { Id:1,RawTypeCode:1,RequiredCount:10 } || IsBoss && mission is { Id:2,RawTypeCode:2,RequiredCount:2 }) ||
            SourceKind=="dungeon_fatigue_spend" && mission is { Id:4,RawTypeCode:3,RequiredCount:10 });
}
