using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

// 原权威死亡的完整深渊房间或超星空终末事实；普通清房和客户端退出请求都不生成来源。
internal sealed class BattlePassHellCosmoCompletion
{
    readonly DungeonEntryPlan entry;
    readonly PreparedDungeonDeath prepared;
    public string SourceKind { get; }
    public string SourceId { get; }
    public TimeProvider Clock { get; }
    public DateTimeOffset PlannedAt { get; }
    public bool IsCosmo { get; }

    BattlePassHellCosmoCompletion(SinglePlayerDungeonRun run, PreparedDungeonDeath death, bool cosmo, TimeProvider clock)
    {
        entry = run.Entry; prepared = death; IsCosmo = cosmo; Clock = clock; PlannedAt = clock.GetUtcNow();
        SourceKind = cosmo ? "cosmo_completion" : "normal_hell_completion";
        SourceId = Identity(SourceKind, death.Death.Identity, entry.DungeonId);
    }

    public static BattlePassHellCosmoCompletion? Prepare(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared, TimeProvider clock)
    {
        if (!Eligible(run, prepared)) return null;
        return new(run, prepared, run.Entry.IsCosmo, clock);
    }

    static bool Eligible(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared)
    {
        var e = run.Entry; var id = prepared.Death.Identity; var result = prepared.Result;
        if (!run.IsPendingDeath(prepared) || id != run.Snapshot.Identity || id.CharacterId != e.CharacterId ||
            id.InstanceId == Guid.Empty || id.TaskAttempt <= 0 || id.Epoch <= 0 || prepared.Death.EventId == Guid.Empty ||
            !e.IsOrdinary || e.IsPractice || e.IsTrainingRoom || e.IsClassExperience || e.IsTutorial || e.IsQuestReplay ||
            e.RaidEntry is not null || e.IsAltar || e.IsTagSystem || prepared.Monster is not { IsBlocking: true, CanReportDeath: true, IsFriendly: false, IsSpecialChild: false } ||
            result.Outcome != DungeonRunOutcome.Accepted) return false;
        if (e.IsCosmo)
            return e.HellParty is null && result.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement &&
                result.ClearFact is { PassiveSource: null, ClearMap: { } map } clear &&
                clear.Identity == result.Snapshot.Identity && clear.DungeonId == e.DungeonId &&
                map == result.Snapshot.CurrentMap && e.IsTerminalMap(map) &&
                clear.ConfirmedDeathEvents.Contains(prepared.Death.EventId);
        return IsHellRoomComplete(e, run.CurrentMonsterPlan, result.Snapshot);
    }

    internal static bool IsHellRoomComplete(DungeonEntryPlan entry, DungeonMonsterPlan monsters, DungeonRunSnapshot snapshot) =>
        !entry.IsCosmo && entry.HellParty is { } hell && snapshot.Phase == SinglePlayerDungeonPhase.Dungeon &&
        snapshot.CurrentMap.Id == hell.MapId && snapshot.CurrentMap.Position == hell.Room &&
        monsters.Room.Map == snapshot.CurrentMap && monsters.HellWaves.Count > 0 &&
        monsters.Monsters.Any(monster => monster.IsBlocking) &&
        monsters.Monsters.Where(monster => monster.IsBlocking).All(monster => !snapshot.LivingMonsters.Contains(monster.ObjectKey));

    public bool Matches(SinglePlayerDungeonRun run, PreparedDungeonDeath death) =>
        ReferenceEquals(entry, run.Entry) && ReferenceEquals(prepared, death) && Eligible(run, death);

    public bool MatchesMission(byte category, BattlePassMission mission) => MatchesDefinition(IsCosmo, category, mission);

    internal static bool MatchesDefinition(bool cosmo, byte category, BattlePassMission mission) => mission.RawParameter == -1 &&
        (cosmo ? category == 1 && mission is { Id: 29, RawTypeCode: 24, RequiredCount: 3, Experience: 1000 } ||
                 category == 2 && mission is { Id: 53, RawTypeCode: 24, RequiredCount: 80, Experience: 1600 }
               : category == 0 && mission is { Id: 23, RawTypeCode: 23, RequiredCount: 1, Experience: 100 } ||
                 category == 1 && mission is { Id: 40, RawTypeCode: 23, RequiredCount: 3, Experience: 1000 });

    internal static string Identity(string kind, DungeonRunIdentity identity, uint dungeon)
    {
        if (kind is not ("normal_hell_completion" or "cosmo_completion") || identity.CharacterId == 0 ||
            identity.InstanceId == Guid.Empty || identity.TaskAttempt <= 0 || identity.Epoch <= 0 || dungeon == 0)
            throw new InvalidDataException("深渊整次完成必须具有原实例和角色身份");
        // 同实例只完成一次；后续死亡或退出epoch不生成第二次完成。
        string text = FormattableString.Invariant($"{kind}:{identity.CharacterId}:{identity.InstanceId:N}:{identity.TaskAttempt}:{dungeon}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16)).ToLowerInvariant();
    }
}
