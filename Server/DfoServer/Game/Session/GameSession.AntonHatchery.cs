using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 四角原生筋对象生成的warp目标，不能借Dungeon阶段的16任意跨图。
    internal static bool IsAntonHatcheryEntrance(int map, uint target) => (map, target) is
        (19103, 221) or (19119, 222) or (19116, 223) or (19100, 224);

    bool CanTransferAntonHatchery(FormalDungeonReceipt source, uint target) =>
        Ready && Phase == SessionPhase.Dungeon && CanMoveWithParty() && pendingRental is null &&
        !HasPendingTownQuestCommit && formalDungeon is { CharacterDead: false, Entry.DungeonId: 218, Entry.RaidEntry: { Kind: RaidKind.Anton, Phase: 1 } identity } dungeon &&
        dungeon.Run.Snapshot.Identity == source.Snapshot.Identity && dungeon.Run.Snapshot.CurrentMap == source.Snapshot.CurrentMap &&
        IsAntonHatcheryEntrance(source.Snapshot.CurrentMap.Id, target) &&
        raidDungeonAdmission is { Kind: RaidKind.Anton, Phase: 1 } admission && admission.Current && admission.Allows(checked((int)target)) &&
        admission.InstanceId == identity.InstanceId && admission.Squad == identity.Squad &&
        ReferenceEquals(CurrentRaidBattle(admission.Actor), admission.Battle) &&
        !dungeon.Ground.HasPendingOperation && !HasPendingFormalCard && pendingQuestCompletion is null &&
        !dungeon.HasPendingFatigue && !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement;

    internal PartyDungeonPreparation? PrepareAntonHatchery(uint target, DungeonRoomCatalog rooms,
        Guid instance, uint firstKey, PartyDungeonPreparation? shared)
    {
        LastFormalDungeonRejection = null;
        var capture = owner.Execute(() => (Source: formalDungeon is null ? null : FormalReceipt(),
            Character: Selected, Epoch: SelectionEpoch, Admission: raidDungeonAdmission));
        if (capture.Source is not { } source || capture.Character is not { } character ||
            !owner.Execute(() => CanTransferAntonHatchery(source, target)) || capture.Admission is not { } admission ||
            admission.Catalog.Find(checked((int)target)) is not { } definition) return Reject("anton-hatchery-source-or-target-changed");
        var mazes = definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0).ToArray();
        if (mazes.Length != 1) return Reject("anton-hatchery-maze-unproven");
        if (!DungeonEntryPlan.TryCreate(definition, character, target, 0, 2,
            out var entry, out var reason, resolveMaze: maze => admission.Catalog.ResolveMaze(definition, maze, null),
            partyRoute: shared?.Session.PreparedParty(shared).Dungeon.Entry,
            raid: source.Entry.RaidEntry, raidMaze: mazes[0].SourceOrdinal)) return Reject("anton-hatchery-plan:" + reason);
        entry = entry! with { RaidEntry = source.Entry.RaidEntry, DisableMonsterAndClearExperience = true,
            DisableClearReward = true, DisableFatigueConsumption = true, DisableGiveupPenalty = true };
        var prepared = PrepareFormalDungeonResources(character, capture.Epoch, entry, rooms, null, null, null, null,
            shared: shared, partyFirstKey: firstKey, partyInstance: instance);
        return owner.Execute(() =>
        {
            if (prepared is null || !ReferenceEquals(Selected, character) || SelectionEpoch != capture.Epoch ||
                !ReferenceEquals(raidDungeonAdmission, admission) || !CanTransferAntonHatchery(source, target))
                return Reject(LastFormalDungeonRejection ?? "anton-hatchery-prepared-source-changed");
            preparedPartyDungeon = prepared with { AntonHatcherySource = source };
            return partyPreparation = new(this, character);
        });
        PartyDungeonPreparation? Reject(string reason) { LastFormalDungeonRejection = reason; return null; }
    }

    bool CanReserveAntonHatchery(PreparedFormalDungeon plan) => plan.AntonHatcherySource is { } source &&
        CanTransferAntonHatchery(source, plan.Dungeon.Entry.DungeonId);
}
