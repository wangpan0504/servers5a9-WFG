using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    RaidDungeonAdmission? raidDungeonAdmission;

    internal bool IsPreySoloRaid => raidDungeonAdmission is { Kind: RaidKind.Prey, PreyGuide: false, PreyPartyId: null, PreySoloCharacter: { } solo } &&
        Selected?.Id == solo && CurrentParty?.Invoke() is null;

    internal bool MatchesPreySoloSelection(Game.Party.PartySnapshot scene) => owner.Execute(() =>
        IsPreySoloRaid && raidDungeonAdmission!.Current && scene.Id == ushort.MaxValue && scene.Members.Length == 1 &&
        scene.PreyBattleId == raidDungeonAdmission.InstanceId && scene.Members[0].Actor.Session == this &&
        scene.Members[0].IsCurrent() && CurrentDungeonSelection?.Raid?.Admission == raidDungeonAdmission);

    internal (Guid Instance, byte Area)? CapturePreySoloSceneIdentity() => owner.Execute<(Guid, byte)?>(() =>
    {
        if (!Ready || Phase is not (SessionPhase.Town or SessionPhase.MapSelect or SessionPhase.Loading) ||
            CurrentParty?.Invoke() is not null || Selected?.Position is not { Town: 59, Area: 4 or 5 } position ||
            raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid ||
            !HasPreyTeamTownAccess || raid.Battle.View.State != AntonRaidBattleState.Fighting) return null;
        if (Phase == SessionPhase.Loading)
        {
            // Loading只描述尚未37的原单人Run，不在此签发新授权或重建场景。
            if (!IsPreySoloRaid || formalDungeon is not { } dungeon || PartyDungeon is not { } instance ||
                instance.Members is not [var member] || !ReferenceEquals(member.Actor.Session, this) ||
                instance.Id != dungeon.Run.Snapshot.Identity.InstanceId || ReplayAntonLoading(checked((int)dungeon.Entry.DungeonId)) is null) return null;
        }
        else if (formalDungeon is not null || PartyDungeon is not null || Phase == SessionPhase.Town && !CanChangeTownParty) return null;
        return (raid.Battle.Instance, position.Area);
    });

    // 只能由已提交开战的领域协调者调用；授权仍随原owner/选角epoch与原实例一起失效。
    public bool AuthorizeRaidDungeon(RaidDungeonAdmission admission) => owner.Execute(() =>
    {
        if (admission.Kind is not (RaidKind.Anton or RaidKind.Prey or RaidKind.Fiendwar) || !ReferenceEquals(admission.Actor.Session, this) || !ReferenceEquals(admission.Actor.Owner, owner) ||
            !admission.Current || ChannelEntry?.Identity != admission.Actor.Channel || RaidTypes.FromChannelType(ChannelEntry.Type) != (byte)admission.Kind ||
            !Ready || !(Phase == SessionPhase.Town || admission.Kind == RaidKind.Luke && Phase == SessionPhase.MapSelect) || formalDungeon is not null ||
            Selected?.Position is not { } position || !IsRaidDungeonTownArea(admission, position)) return false;
        raidDungeonAdmission = admission;
        return true;
    });

    internal bool CanBeginRaidDungeonSelection(RaidDungeonAdmission admission) => owner.Execute(() =>
        admission.Current && admission.Battle is not null && ReferenceEquals(admission.Actor.Session, this) &&
        ReferenceEquals(CurrentRaidBattle(admission.Actor), admission.Battle) && StartedRaid?.InstanceId == admission.InstanceId &&
        ReferenceEquals(admission.Actor.Owner, owner) && ChannelEntry?.Identity == admission.Actor.Channel &&
        RaidTypes.FromChannelType(ChannelEntry.Type) == (byte)admission.Kind && Ready && Phase is SessionPhase.Town or SessionPhase.MapSelect &&
        formalDungeon is null && pendingRental is null && !HasPendingTownQuestCommit &&
        Selected?.Position is { } position && IsRaidDungeonTownArea(admission, position) && RaidSelectionReturn(admission) is not null);

    GameWorld.Towns.Location? RaidSelectionReturn(RaidDungeonAdmission admission) =>
        admission.Kind == RaidKind.Prey && !admission.PreyGuide && Selected is { } character
            ? towns.Entrance(character.Position, new(59, 3, 0, 0), completed) ?? towns.Spawn(new(59, 3, 0, 0), completed)
            : Selected?.Position;

    static bool IsRaidDungeonTownArea(RaidDungeonAdmission admission, GameWorld.Towns.Location position) =>
        admission.Catalog.Script.WaitingRoom is { } room && position.Town == room.Town &&
        (admission.Kind == RaidKind.Fiendwar ? admission.Battle?.FiendwarPosition(admission.Actor.Character) == position.Area :
            admission.Kind == RaidKind.Prey && !admission.PreyGuide ? position.Area is 4 or 5 : position.Area == room.Area);

    internal DungeonSelectionReceipt InstallRaidDungeonMenu(RaidDungeonAdmission admission) => InstallRaidSelection(admission, null);

    internal DungeonSelectionReceipt InstallRaidDungeonSelection(RaidDungeonSelection selection) => InstallRaidSelection(selection.Admission, selection);

    DungeonSelectionReceipt InstallRaidSelection(RaidDungeonAdmission admission, RaidDungeonSelection? selection) => owner.Execute(() =>
    {
        if (!CanBeginRaidDungeonSelection(admission))
            throw new InvalidOperationException("团本选图安装前身份已变化");
        InvalidateFiendwarKeyPreparation(preserveScene: true);
        raidDungeonAdmission = admission;
        CurrentDungeonSelection = new(++dungeonSelectionId, SelectionEpoch, Selected!.Id, Selected.Position, RaidSelectionReturn(admission)!)
            { Raid = selection };
        SetState(SessionPhase.MapSelect);
        return new DungeonSelectionReceipt(Selected, owner.Generation, SelectionEpoch, dungeonSelectionId);
    });

    internal bool HasLukeRaidSelection => CurrentDungeonSelection?.Raid?.Admission.Kind == RaidKind.Luke;
    internal bool HasFormalRaidSelection => CurrentDungeonSelection?.Raid is not null;
    internal FormalDungeonReceipt? ReplayAntonLoading(int dungeon) => owner.Execute(() =>
        Ready && Phase == SessionPhase.Loading && formalDungeon is { } run &&
        run.Entry.DungeonId == dungeon && run.Run.LastLoadingFact is null &&
        run.Entry.StartMap == run.Run.Snapshot.CurrentMap &&
        run.Entry.RaidEntry is { Kind: RaidKind.Anton or RaidKind.Prey or RaidKind.Fiendwar } identity &&
        StartedRaid?.InstanceId == identity.InstanceId && IsCurrentRaidDungeon(run.Entry) &&
        raidDungeonAdmission!.Allows(dungeon) ? FormalReceipt() : null);

    internal void CancelRaidDungeonSelection(PartyDungeonNotice[] selections)
    {
        // 仅清本次选择令牌；事务成功安装的Run和后来建立的选择均不回滚。
        foreach (var notice in selections)
            notice.Recipient.Actor.Owner.Execute(() =>
            {
                var session = notice.Recipient.Actor.Session;
                if (notice.Selection is not { } receipt || session.formalDungeon is not null ||
                    session.CurrentDungeonSelection?.Id != receipt.SelectionId || session.SelectionEpoch != receipt.SelectionEpoch) return false;
                session.CurrentDungeonSelection = session.CurrentDungeonSelection with { Raid = null };
                session.raidDungeonAdmission = null;
                return true;
            });
    }
    internal void ClearLukeRaidSelection(bool preserveScene = false) => owner.Execute(() =>
    {
        if (CurrentDungeonSelection?.Raid is null || formalDungeon is not null) return false;
        // 入图拒绝或重选只撤销目标，客户端仍在scene2；132才负责关闭场景并发布回城。
        if (preserveScene && Phase == SessionPhase.MapSelect)
        {
            CurrentDungeonSelection = CurrentDungeonSelection with { Raid = null };
            return true;
        }
        CurrentDungeonSelection = null;
        raidDungeonAdmission = null;
        if (Phase == SessionPhase.MapSelect) SetState(SessionPhase.Town);
        return true;
    });

    bool HasRaidDungeonAdmission => raidDungeonAdmission is { } admission && admission.Current &&
        ReferenceEquals(admission.Actor.Session, this);

    bool IsCurrentRaidDungeon(DungeonEntryPlan entry) => entry.RaidEntry is not { } raid ||
        raidDungeonAdmission is { } admission && admission.InstanceId == raid.InstanceId && admission.Phase == raid.Phase &&
        admission.Squad == raid.Squad && admission.PreyPartyId == raid.PreyPartyId &&
        admission.PreySoloCharacter == raid.PreySoloCharacter && admission.Kind == raid.Kind &&
        (raid.Kind != RaidKind.Prey || admission.PreyGuide == raid.PreyGuide) && admission.Current;

    bool CanInstallRaidDungeon(DungeonEntryPlan entry) => IsCurrentRaidDungeon(entry) &&
        (entry.RaidEntry is null || raidDungeonAdmission!.Allows(checked((int)entry.DungeonId)));

    DungeonSelectionReceipt? BeginRaidDungeonSelection(bool partyAdmission)
    {
        if (!HasRaidDungeonAdmission || Selected is not { } character ||
            !IsRaidDungeonTownArea(raidDungeonAdmission!, character.Position) ||
            !partyAdmission && HasMultiplePartyMembers?.Invoke() == true) return null;
        if (CurrentDungeonSelection is null)
            CurrentDungeonSelection = new(++dungeonSelectionId, SelectionEpoch, character.Id, character.Position, character.Position);
        ValidateDungeonSelection();
        SetState(SessionPhase.MapSelect);
        return new(character, owner.Generation, SelectionEpoch, dungeonSelectionId);
    }

    PreparedFormalDungeon? PrepareRaidDungeon(uint dungeonId, uint questId, byte difficulty, DungeonRoomCatalog rooms,
        Func<int, int>? draw, Character? requiredCharacter, bool incompatibleMode, PartyDungeonPreparation? shared, uint? firstKey, Guid? partyInstance,
        bool partyAdmission)
    {
        var captured = owner.Execute(() => (Selected: Ready && Phase == SessionPhase.MapSelect && CanEnterFormalDungeon(null, partyAdmission) &&
            (requiredCharacter is null || ReferenceEquals(requiredCharacter, Selected)) ? Selected : null,
            SelectionEpoch, CurrentDungeonSelection, Admission: raidDungeonAdmission));
        var admission = captured.Admission;
        string prefix = admission?.Kind switch { RaidKind.Luke => "luke", RaidKind.Anton => "anton", RaidKind.Prey => "prey", _ => "raid" };
        if (captured.Selected is not { } character || captured.CurrentDungeonSelection is null || admission is null ||
            incompatibleMode || questId != 0 || dungeonId > int.MaxValue || !admission.Allows((int)dungeonId))
            return RejectFormal(prefix + "-entry-authorization-changed");
        var definition = admission.Catalog.Find((int)dungeonId);
        if (definition is null) return RejectFormal(prefix + "-entry-resource-unregistered");
        var selected = captured.CurrentDungeonSelection.Raid;
        bool controlled = admission.Kind == RaidKind.Luke || admission.Battle is not null;
        if (controlled && (selected is null || !ReferenceEquals(selected.Admission, admission) || selected.Dungeon != dungeonId ||
            admission.Kind == RaidKind.Luke && (selected.Route is { } route
                ? admission.Battle?.IsLukeRouteCurrent(route) != true || route.Dungeon != selected.Dungeon || route.MazeOrdinal != selected.MazeOrdinal
                : admission.Battle?.UsesLukeRoute(admission.Phase) == true || admission.LukePolicy?.Maze(admission.Phase, selected.Dungeon) != selected.MazeOrdinal)))
            return RejectFormal(prefix + "-entry-selection-changed");
        var identity = new RaidDungeonIdentity(admission.InstanceId, admission.Phase, admission.Squad, admission.Kind)
            { PreyGuide = admission.PreyGuide, PreyPartyId = admission.PreyPartyId, PreySoloCharacter = admission.PreySoloCharacter };
        var sharedEntry = shared?.Session.PreparedParty(shared).Dungeon.Entry;
        if (sharedEntry is not null && sharedEntry.RaidEntry != identity)
            return RejectFormal(prefix + "-entry-squad-mismatch");
        if (!DungeonEntryPlan.TryCreate(definition, character, dungeonId, 0, difficulty, out var entry, out string reason,
            draw: draw, resolveMaze: maze => admission.Catalog.ResolveMaze(definition, maze, draw), partyRoute: sharedEntry, raid: controlled ? identity : null, raidMaze: selected?.MazeOrdinal))
            return RejectFormal(prefix + "-entry-plan:" + reason);
        var energy = admission.Kind == RaidKind.Luke && admission.LukePolicy?.UseEnergyObjects == true && admission.Phase == 1
            ? admission.LukeObjects?.Energy((int)dungeonId, entry!.Maze) : null;
        if (admission.Kind == RaidKind.Luke && admission.LukePolicy?.UseEnergyObjects == true && dungeonId is 5063 or 5066 && energy is null)
            return RejectFormal("luke-energy-object-resource-unavailable");
        entry = entry! with
        {
            RaidEntry = identity,
            FiendwarScale = admission.FiendwarScale,
            LukeEnergyObject = energy,
            CoinLimit = controlled && admission.Kind == RaidKind.Luke ? 0 : definition.CoinLimit,
            DisableMonsterAndClearExperience = controlled || entry!.DisableMonsterAndClearExperience,
            DisableClearReward = controlled || entry.DisableClearReward,
            DisableFatigueConsumption = controlled || entry.DisableFatigueConsumption,
            DisableGiveupPenalty = controlled || entry.DisableGiveupPenalty
        };
        // UNIMPLEMENTED：团本单图逐怪地面掉落缺少匹配资源及原生合同，资格、来源表、分配与重放尚未闭合；
        // 保留空掉落依赖，不以可解析的普通怪表授予团本资格或套用普通图奖池。需匹配版本210自然战斗的
        // 39/38地面、43/44、14与库存对照后，复用DungeonResourceDropPolicy.PrepareEntry。
        var prepared = PrepareFormalDungeonResources(character, captured.SelectionEpoch, entry, rooms, null, null, draw, null,
            shared: shared, partyFirstKey: firstKey, partyInstance: partyInstance);
        if (prepared is null) return null;
        return owner.Execute(() => ReferenceEquals(raidDungeonAdmission, admission) && admission.Allows((int)dungeonId) &&
            ReferenceEquals(Selected, character) && ReferenceEquals(CurrentDungeonSelection, captured.CurrentDungeonSelection) &&
            SelectionEpoch == captured.SelectionEpoch ? prepared : RejectFormal(prefix + "-entry-authorization-changed"));
    }
}
