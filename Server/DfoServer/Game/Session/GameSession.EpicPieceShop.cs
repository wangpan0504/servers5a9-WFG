using System.Collections.Immutable;
using System.Security.Cryptography;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed record PendingEpicPieceShop(EpicPieceShopAction Action, int Argument, Character Character,
        EpicPieceShopState Before, EpicPieceShopPlan Plan);

    public (bool Visible, FormalDungeonReceipt Scene)? EpicPieceShopPresentation(EpicPieceShopService service, bool prepare)
    {
        var party = PartyDungeon;
        if (prepare && party is not null && owner.Execute(() => formalDungeon is { EpicPieceShop: null } candidate &&
            service.Supports(candidate.Entry) && CanUseEpicPieceShop(candidate)))
            return party.Execute<(bool, FormalDungeonReceipt)?>(members =>
                members.Any(member => ReferenceEquals(member.Actor.Session, this)) ? Read(party, true) : null);
        return owner.Execute(() => Read(null, prepare && party is null));

        (bool, FormalDungeonReceipt)? Read(PartyDungeonInstance? shared, bool initialize)
        {
            if (!Ready || Selected is null || Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement) ||
                formalDungeon is not { } dungeon) return null;
            if (initialize && dungeon.EpicPieceShop is null)
            {
                if (!ReferenceEquals(PartyDungeon, shared) || !service.Supports(dungeon.Entry) || !CanUseEpicPieceShop(dungeon)) return null;
                bool Roll() => service.Appears(dungeon.Draw);
                dungeon.EpicPieceShop = new(shared?.ResolveEpicPieceShop(Roll) ?? Roll());
            }
            if (dungeon.EpicPieceShop is not { } state || dungeon.EpicPieceShopPresentation == dungeon.Run.Snapshot.Identity) return null;
            return (state.Available && !state.Completed && CanUseEpicPieceShop(dungeon), FormalReceipt());
        }
    }

    public void ConfirmEpicPieceShopPresentation(FormalDungeonReceipt scene) => owner.Execute(() =>
    {
        if (CanPublishFormalDungeon(scene) && formalDungeon is { } dungeon)
            dungeon.EpicPieceShopPresentation = scene.Snapshot.Identity;
        return true;
    });

    public (InventoryResult Result, ImmutableArray<int> Materials, FormalDungeonReceipt? Scene) OperateEpicPieceShop(
        EpicPieceShopService service, EpicPieceShopAction action, int argument, Func<int, int>? draw = null)
    {
        var captured = owner.Execute(() => (Character: formalDungeon is { } dungeon && CanUseEpicPieceShop(dungeon) ? Selected : null,
            Dungeon: formalDungeon, State: formalDungeon?.EpicPieceShop, Pending: formalDungeon?.PendingEpicPieceShop,
            SelectionEpoch, Phase, Identity: formalDungeon?.Run.Snapshot.Identity));
        (InventoryResult, ImmutableArray<int>, FormalDungeonReceipt?) Reject() =>
            (InventoryResult.Reject(19) with { RejectReason = "shop_context_unavailable" }, [], null);
        if (captured.Character is not { } character || captured.Dungeon is not { } original || captured.State is not { } state)
            return Reject();
        if (captured.Pending is { } pending && (pending.Action != action || pending.Argument != argument ||
            !ReferenceEquals(pending.Character, character) || !ReferenceEquals(pending.Before, state))) return Reject();
        // 候选筛选与随机规划在锁外；忙锁重试只复用这一份计划，不重新抽付费清单。
        var plan = captured.Pending?.Plan ?? service.Plan(character, state, action, argument,
            draw ?? RandomNumberGenerator.GetInt32, DateTimeOffset.UtcNow);
        return owner.Execute<(InventoryResult, ImmutableArray<int>, FormalDungeonReceipt?)>(() =>
        {
            if (!ReferenceEquals(formalDungeon, original) || !CanUseEpicPieceShop(original) ||
                !ReferenceEquals(original.EpicPieceShop, state) || !ReferenceEquals(Selected, character) ||
                SelectionEpoch != captured.SelectionEpoch || Phase != captured.Phase || original.Run.Snapshot.Identity != captured.Identity)
                return Reject();
            if (!plan.Inventory.Accepted) return (plan.Inventory, [], FormalReceipt());
            original.PendingEpicPieceShop ??= new(action, argument, character, state, plan);
            var result = CommitInventory(character, plan.Inventory, 19);
            original.PendingEpicPieceShop = null;
            if (result.Accepted) original.EpicPieceShop = plan.After;
            return (result, result.Accepted ? plan.After.Materials : [], FormalReceipt());
        });
    }

    bool CanUseEpicPieceShop(FormalDungeon dungeon) => Ready && Selected is not null &&
        Phase is SessionPhase.Dungeon or SessionPhase.Settlement && !dungeon.CharacterDead &&
        pendingRental is null && !HasPendingTownQuestCommit && !HasPendingQuestScene && !HasPendingFormalCard &&
        pendingQuestCompletion is null && pendingQuestLifecycle is null && !HasUncommittedQuestQuickClear &&
        !HasActiveDungeonContinuation && !HasPendingQuestReplay && !dungeon.HasPendingFatigue &&
        !dungeon.Ground.HasPendingOperation && !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement &&
        dungeon.Entry.HellParty is { } hell && dungeon.Run.Snapshot.CurrentMap.Id == hell.MapId &&
        dungeon.Run.CurrentMonsterPlan.Monsters
            .All(monster => !monster.IsBlocking || !dungeon.Run.Snapshot.LivingMonsters.Contains(monster.ObjectKey));
}
