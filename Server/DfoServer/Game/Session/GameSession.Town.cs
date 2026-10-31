using DfoServer.Game.Town;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    private (NpcTownVisit Visit, long Epoch)? npcTownVisit;

    // 已进入提交的任务依赖原角色快照恢复；未提交手册与已提交的券回执不排斥城镇操作，按普通操作自然失效。
    bool HasPendingTownQuestCommit => formalDungeon is { PendingHopeReward: not null, HopeRewardConfirmed: false } || pendingTrainingCopy is not null || pendingPvpSkills is not null || pendingCommunication is not null || pendingQuestCompletion is not null || pendingQuestLifecycle is not null ||
        pendingQuestQuickClear is { CommitStarted: true } or { Committed: null, Plan.IsPreparedSkip: false } || HasPendingQuestScene;

    public bool Move(PositionRequest request) => owner.Execute(() =>
    {
        if (pendingRental is not null || HasPendingTownQuestCommit || Phase != SessionPhase.Town || Selected is null)
            return false;
        var target = TownTravel.Walk(Selected, request, towns, completed);
        if (target is null)
            return false;
        if (target != Selected.Position)
        {
            var saved = SaveTownPosition(Selected, target, Selected.Return);
            if (saved is null) return false;
            Selected = saved;
        }
        return true;
    });

    public bool Travel(AreaRequest request) => Travel(request, out _);

    public bool Travel(AreaRequest request, out TownTravelReceipt? receipt)
    {
        receipt = owner.Execute(() => CommitTravel(Phase == SessionPhase.Town && Selected is not null
            ? TownTravel.Plan(Selected, request, towns, completed, ChannelEntry,
                npcTownVisit is { } visit && visit.Epoch == SelectionEpoch ? visit.Visit : null)
            : TownTravelPlan.Reject("not-ready")));
        return receipt is not null;
    }

    public bool LeaveRoom() => LeaveRoom(out _);

    public bool LeaveRoom(out TownTravelReceipt? receipt)
    {
        receipt = owner.Execute(() => CommitTravel(Phase == SessionPhase.Town && Selected is not null
            ? TownTravel.LeaveRoom(Selected, towns, completed, ChannelEntry)
            : TownTravelPlan.Reject("not-in-ready-room")));
        return receipt is not null;
    }

    TownTravelReceipt? CommitTravel(TownTravelPlan plan)
    {
        TravelReason = plan.Reason;
        if (pendingRental is not null || HasPendingTownQuestCommit || !plan.Accepted)
            return null;
        // 等待区只允许当前真实魔界大战队伍进入；拒绝必须发生在保存位置和发布切图之前。
        if (ChannelEntry?.Type == 54 && Selected?.Position is { Town: 67, Area: 1 } &&
            plan.Position is { Town: 67, Area: 2 } &&
            (CurrentParty?.Invoke() is not { Settings.Kind: 5 } party ||
             !party.Members.Any(member => ReferenceEquals(member.Actor.Session, this) &&
                 member.Actor.Epoch == SelectionEpoch && member.IsCurrent())))
        {
            TravelReason = "evildom-war-party-required";
            return null;
        }
        var saved = SaveTownPosition(Selected!, plan.Position!, plan.Return);
        if (saved is null) { TravelReason = "guild-membership-required"; return null; }
        Selected = saved;
        // 保存成功后才替换来源；失败重试保留原票据，不提前消耗也不制造成功入场。
        npcTownVisit = plan.NpcVisit is { } visit ? (visit, SelectionEpoch) : null;
        InvalidateImageCommunication();
        InvalidateExpertJobStore();
        return new(Selected, owner.Generation, SelectionEpoch);
    }

    Game.Characters.Character? SaveTownPosition(Game.Characters.Character character,
        GameWorld.Towns.Location position, GameWorld.Towns.Location? returnPoint) =>
        towns.IsGuildHall(position, completed)
            ? repository is Game.Guild.IGuildHallStore hall ? hall.SaveGuildHallPosition(owner, character, position, returnPoint) : null
            : repository.Save(owner, character, position, returnPoint);

    public bool CanPublishTravel(TownTravelReceipt receipt) => owner.Execute(() =>
    {
        if (!CheckState(UserState.Town) || receipt.Generation != owner.Generation || !MatchesSelection(receipt.SelectionEpoch, receipt.Character.Id))
            return false;
        // 同次选角内不能静默丢掉已提交切图并继续收请求；让连接关闭后重读持久位置。
        if (!ReferenceEquals(Selected, receipt.Character))
            throw new InvalidOperationException("Town travel snapshot changed before publication");
        return true;
    });
}
