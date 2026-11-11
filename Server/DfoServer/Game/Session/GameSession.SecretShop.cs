using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed record PendingSecretShopPurchase(SecretShopPurchase Request, Character Character,
        SecretShopState Before, SecretShopPurchasePlan Plan);

    public (int Merchant, FormalDungeonReceipt Scene)? SecretShopPresentation(SecretShopService service, bool prepare)
    {
        var party = PartyDungeon;
        if (prepare && party is not null && owner.Execute(() => formalDungeon is { SecretShop: null } candidate &&
            service.Supports(candidate.Entry) && CanUseSecretShop(candidate)))
            return party.Execute<(int, FormalDungeonReceipt)?>(members =>
                members.Any(member => ReferenceEquals(member.Actor.Session, this)) ? Read(party, true) : null);
        return owner.Execute(() => Read(null, prepare && party is null));

        (int, FormalDungeonReceipt)? Read(PartyDungeonInstance? shared, bool initialize)
        {
            if (formalDungeon is not { } dungeon || !CanUseSecretShop(dungeon)) return null;
            if (initialize && dungeon.SecretShop is null)
            {
                if (!ReferenceEquals(PartyDungeon, shared) || !service.Supports(dungeon.Entry)) return null;
                int Roll() => service.Merchant(dungeon.Entry.DungeonId, Selected!.Level,
                    dungeon.Entry.Difficulty, dungeon.Draw);
                int merchant = shared?.ResolveSecretShop(Roll) ?? Roll();
                // 只冻结商人；各玩家的商品报价在无owner锁的快照规划阶段生成。
                dungeon.SecretShop = new(merchant, []);
            }
            if (dungeon.SecretShop is not { } state || dungeon.SecretShopPresentation == dungeon.Run.Snapshot.Identity) return null;
            return (state.Merchant, FormalReceipt());
        }
    }

    public void ConfirmSecretShopPresentation(FormalDungeonReceipt scene) => owner.Execute(() =>
    {
        if (CanPublishFormalDungeon(scene) && formalDungeon is { } dungeon)
            dungeon.SecretShopPresentation = scene.Snapshot.Identity;
        return true;
    });

    public (SecretShopState State, FormalDungeonReceipt Scene)? OpenSecretShop(SecretShopService service, bool open)
    {
        var capture = owner.Execute(() => (Dungeon: formalDungeon, Character: Selected,
            State: formalDungeon?.SecretShop, Quoted: formalDungeon?.SecretShopQuoted ?? false,
            SelectionEpoch, Phase, Identity: formalDungeon?.Run.Snapshot.Identity));
        if (capture.Dungeon is not { } original || capture.Character is not { } character ||
            capture.State is not { Available: true } state) return null;
        var offered = open && !capture.Quoted ? service.Offer(state.Merchant, original.Entry.DungeonId,
            character.Level, System.Security.Cryptography.RandomNumberGenerator.GetInt32) : state;
        return owner.Execute<(SecretShopState, FormalDungeonReceipt)?>(() =>
        {
            if (!ReferenceEquals(formalDungeon, original) || !CanUseSecretShop(original) ||
                !ReferenceEquals(Selected, character) || !ReferenceEquals(original.SecretShop, state) ||
                original.Run.Snapshot.Identity != capture.Identity || SelectionEpoch != capture.SelectionEpoch ||
                Phase != capture.Phase) return null;
            original.SecretShop = offered;
            original.SecretShopOpen = open;
            if (open) original.SecretShopQuoted = true;
            return (offered, FormalReceipt());
        });
    }

    public (InventoryResult Result, SecretShopPurchaseReceipt? Purchase, FormalDungeonReceipt? Scene) BuySecretShop(
        SecretShopService service, SecretShopPurchase request)
    {
        var capture = owner.Execute(() => (Dungeon: formalDungeon,
            Character: formalDungeon is { SecretShopOpen: true } dungeon && CanUseSecretShop(dungeon) ? Selected : null,
            State: formalDungeon?.SecretShop, Pending: formalDungeon?.PendingSecretShopPurchase,
            SelectionEpoch, Phase, Identity: formalDungeon?.Run.Snapshot.Identity));
        (InventoryResult, SecretShopPurchaseReceipt?, FormalDungeonReceipt?) Reject() =>
            (InventoryResult.Reject(17) with { RejectReason = "secret_shop_context_unavailable" }, null, null);
        if (capture.Dungeon is not { } original || capture.Character is not { } character || capture.State is not { } state)
            return Reject();
        if (capture.Pending is { } pending && (pending.Request != request ||
            !ReferenceEquals(pending.Character, character) || !ReferenceEquals(pending.Before, state))) return Reject();
        var plan = capture.Pending?.Plan ?? service.Buy(character, state, request, DateTimeOffset.UtcNow);
        return owner.Execute<(InventoryResult, SecretShopPurchaseReceipt?, FormalDungeonReceipt?)>(() =>
        {
            if (!ReferenceEquals(formalDungeon, original) || !CanUseSecretShop(original) || !original.SecretShopOpen ||
                !ReferenceEquals(Selected, character) || !ReferenceEquals(original.SecretShop, state) ||
                original.Run.Snapshot.Identity != capture.Identity || SelectionEpoch != capture.SelectionEpoch ||
                Phase != capture.Phase) return Reject();
            if (!plan.Inventory.Accepted) return (plan.Inventory, null, FormalReceipt());
            original.PendingSecretShopPurchase ??= new(request, character, state, plan);
            var committed = CommitInventory(character, plan.Inventory, 17);
            original.PendingSecretShopPurchase = null;
            if (committed.Accepted) original.SecretShop = plan.After;
            return (committed, committed.Accepted ? plan.Purchase : null, FormalReceipt());
        });
    }

    bool CanUseSecretShop(FormalDungeon dungeon) => Ready && Selected is not null &&
        Phase is SessionPhase.Dungeon or SessionPhase.Settlement && !dungeon.CharacterDead &&
        dungeon.Run.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement &&
        pendingRental is null && !HasPendingTownQuestCommit && !HasPendingQuestScene && !HasPendingFormalCard &&
        pendingQuestCompletion is null && pendingQuestLifecycle is null && !HasUncommittedQuestQuickClear &&
        !HasActiveDungeonContinuation && !HasPendingQuestReplay && !dungeon.HasPendingFatigue &&
        !dungeon.Ground.HasPendingOperation && !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement;
}
