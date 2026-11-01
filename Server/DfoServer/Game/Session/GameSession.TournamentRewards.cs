using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    bool TournamentReady() => Ready && Phase == SessionPhase.Settlement &&
        formalDungeon is { Entry.IsTournament: true, Settlement: { Committed: true, Tournament: not null } };

    FormalTournamentCardResult TournamentView(InventoryResult? inventory = null, bool duplicate = false) =>
        new(FormalReceipt(), formalDungeon!.Settlement!.Tournament!, [.. formalDungeon.Settlement.TournamentChoices],
            formalDungeon.Settlement.TournamentClaimed, inventory, formalDungeon.Settlement.TournamentMail, duplicate);

    public FormalTournamentCardResult? OpenTournamentCards() => owner.Execute(() =>
    {
        if (!TournamentReady()) return null;
        formalDungeon!.Settlement!.CardsShown = true;
        return TournamentView();
    });

    public FormalTournamentCardResult? TournamentRewardState() => owner.Execute(() => TournamentReady() ? TournamentView() : null);

    public bool CanPublishTournamentCards(FormalTournamentCardResult result) => owner.Execute(() =>
        TournamentReady() && result.Receipt.Generation == owner.Generation && result.Receipt.SelectionEpoch == SelectionEpoch &&
        result.Receipt.Snapshot.Identity == formalDungeon!.Run.Snapshot.Identity && ReferenceEquals(Selected, result.Receipt.Character) &&
        formalDungeon.Settlement!.TournamentChoices.SequenceEqual(result.Choices) &&
        formalDungeon.Settlement.TournamentClaimed == result.Complete);

    public FormalTournamentCardResult? SelectTournamentCard(byte side, byte choice, FormalDungeonReceipt? expectedRun = null)
    {
        FormalTournamentCardResult? view = null;
        FormalDungeon? dungeon = null;
        Character? expected = null;
        TournamentCardClaim? pending = null;
        DungeonCardRewardPolicy? policy = null;
        owner.Execute(() =>
        {
            if (!TournamentReady() || formalDungeon!.Settlement is not { CardsShown: true } state || side >= 2 ||
                choice >= state.Tournament!.Row(side).Length ||
                expectedRun is not null && (expectedRun.Generation != owner.Generation || expectedRun.SelectionEpoch != SelectionEpoch ||
                    expectedRun.Snapshot.Identity != formalDungeon.Run.Snapshot.Identity) ||
                state.TournamentChoices[side] != 255 && state.TournamentChoices[side] != choice)
                return false;
            state.TournamentChoices[side] = choice;
            view = TournamentView(duplicate: state.TournamentClaimed);
            if (state.TournamentClaimed || state.TournamentChoices.Contains((byte)255)) return true;
            dungeon = formalDungeon;
            expected = Selected;
            pending = state.TournamentClaim;
            policy = state.CardPolicy;
            return true;
        });
        if (expected is null) return view;
        // 物品目录、钱包归并和满包邮件规划不占用账号锁；提交前复验原实例、选择与角色快照。
        var claim = pending ?? new TournamentCardClaim(expected, view!.Reward, view.Choices,
            (policy ?? throw new InvalidOperationException("大会缺少资产发放策略"))
                .PlanDelivery(expected, view.Reward.Selected(view.Choices), expected.Level));
        return owner.Execute(() =>
        {
            if (!TournamentReady() || !ReferenceEquals(formalDungeon, dungeon) || !ReferenceEquals(Selected, claim.Expected) ||
                !formalDungeon!.Settlement!.TournamentChoices.SequenceEqual(claim.Choices)) return null;
            var state = formalDungeon.Settlement;
            state.TournamentClaim ??= claim;
            if (!ReferenceEquals(state.TournamentClaim, claim)) return null;
            var committed = repository.CommitTournamentCards(owner, claim.Expected, state.Pending, claim);
            Selected = committed.Character;
            state.TournamentClaimed = true;
            state.TournamentMail = committed.MailId;
            state.TournamentClaim = null;
            var inventory = new InventoryResult(true, 0, committed.Inventory)
            {
                Receipt = new(Selected, owner.Generation, SelectionEpoch)
                {
                    Phase = Phase, DungeonIdentity = formalDungeon.Run.Snapshot.Identity,
                    PreviousQuests = claim.Expected.ActiveMainQuests,
                    PreviousSkillSwitchInventory = claim.Expected.SkillSwitchInventory
                }
            };
            return TournamentView(inventory, committed.Duplicate);
        });
    }

    public bool ScheduleTournamentCard(FormalDungeonReceipt receipt) => owner.Execute(() =>
    {
        if (!TournamentReady() || formalDungeon!.Run.Snapshot.Identity != receipt.Snapshot.Identity ||
            receipt.Generation != owner.Generation || receipt.SelectionEpoch != SelectionEpoch ||
            formalDungeon.Settlement is not { CardsShown: true, AutoRevealScheduled: false, TournamentClaimed: false } state)
            return false;
        state.AutoRevealScheduled = true;
        return true;
    });

    public FormalTournamentCardResult? RevealTournamentCards(FormalDungeonReceipt expected)
    {
        var state = TournamentRewardState();
        if (state is null || state.Complete || state.Receipt.Snapshot.Identity != expected.Snapshot.Identity) return null;
        if (!state.Choices.Contains((byte)255))
            return SelectTournamentCard(1, state.Choices[1], expected);
        FormalTournamentCardResult? result = null;
        for (byte side = 0; side < 2; side++)
            if (state.Choices[side] == 255)
                result = SelectTournamentCard(side, checked((byte)Random.Shared.Next(state.Reward.Row(side).Length)), expected);
        return result;
    }
}
