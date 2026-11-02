using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Town;
using System.Collections.Immutable;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public RevivalBlessingTimeout? CurrentRevivalBlessingTimeout() => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || formalDungeon is not { } dungeon) return null;
        // 保留已到期的原截止供客户端清理；普通库存 revision 和过房不能让计时失效。
        long until = character.Inventory.ConsumableStates.Concat(dungeon.FreeConsumableEffects)
            .Where(row => row.Kind == ConsumableStateKind.Effect && GameWorld.Items.ConsumableSpecialItems.IsRevivalBlessing(row.ItemId))
            .Select(row => row.UntilMilliseconds).DefaultIfEmpty().Max();
        return until > 0 ? new RevivalBlessingTimeout(character.Id, owner.Generation, SelectionEpoch,
            dungeon.Run.Snapshot.Identity.InstanceId, until) : null;
    });

    public bool CanPublishRevivalBlessingTimeout(RevivalBlessingTimeout scheduled, long now) => owner.Execute(() =>
        now >= scheduled.UntilMilliseconds && CurrentRevivalBlessingTimeout() == scheduled);

    public FormalDungeonReceipt? ReportFormalCharacterDeath() => owner.Execute(() =>
    {
        if (!CanChangeDungeonLife() || formalDungeon!.Run.TagEvildomLife is not null) return null;
        if (formalDungeon!.CharacterDead) return null;
        var character = Selected!;
        long now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        bool blessing = formalDungeon!.Entry.RaidEntry?.Kind is not (Raids.RaidKind.Luke or Raids.RaidKind.Fiendwar) && !formalDungeon.Entry.Maze.IsGriefTower && character.Inventory.ConsumableStates.Concat(formalDungeon.FreeConsumableEffects).Any(row => row.Kind == ConsumableStateKind.Effect &&
            GameWorld.Items.ConsumableSpecialItems.IsRevivalBlessing(row.ItemId) && row.Active(now));
        var after = ConsumableStatePolicy.CrossBoundary(character.Inventory, ConsumableMaintenance.Death,
            now);
        if (blessing)
            after = after with { ConsumableStates = after.ConsumableStates.Where(row => row.Kind != ConsumableStateKind.Effect ||
                !GameWorld.Items.ConsumableSpecialItems.IsRevivalBlessing(row.ItemId)).ToImmutableArray() };
        if (after != character.Inventory)
            CommitInventory(character, new(true, 0, new(character.Inventory, after, "consumable_death", new(character))));
        formalDungeon!.FreeConsumableEffects = ConsumableStatePolicy.CrossBoundary(character.Inventory with
            { ConsumableStates = formalDungeon.FreeConsumableEffects }, ConsumableMaintenance.Death, now).ConsumableStates;
        if (blessing)
            formalDungeon.FreeConsumableEffects = formalDungeon.FreeConsumableEffects.Where(row => row.Kind != ConsumableStateKind.Effect ||
                !GameWorld.Items.ConsumableSpecialItems.IsRevivalBlessing(row.ItemId)).ToImmutableArray();
        CrossConsumableCooldownBoundary(ConsumableMaintenance.Death);
        formalDungeon!.Run.CancelRidable();
        formalDungeon!.CharacterDead = !blessing;
        formalDungeon.DeathSerial++;
        if (formalDungeon.Entry.RaidEntry?.Kind == Raids.RaidKind.Anton &&
            raidCombat is { Battle.Kind: Raids.RaidKind.Anton } raid)
            raid.Battle.RecordAntonDeath(raid.Actor);
        return FormalReceipt() with { AutomaticRevival = blessing };
    });

    public DungeonDeathReturnReceipt? CurrentDungeonDeathReturn() => owner.Execute(() =>
        Ready && Selected is { } character && formalDungeon is { CharacterDead: true } dungeon && dungeon.Run.TagEvildomLife is null
            ? new DungeonDeathReturnReceipt(character.Id, owner.Generation, SelectionEpoch,
                dungeon.Run.Snapshot.Identity.InstanceId, dungeon.DeathSerial) : null);

    // 不绑定角色快照或房间epoch：现场购买和死亡后的残留攻击都不能丢失回城计时。
    public TownTravelReceipt? ReturnFromFormalDungeonOnDeath(DungeonDeathReturnReceipt scheduled) => owner.Execute(() =>
        Ready && Selected?.Id == scheduled.CharacterId && owner.Generation == scheduled.Generation &&
        SelectionEpoch == scheduled.SelectionEpoch && formalDungeon is { CharacterDead: true } dungeon &&
        dungeon.Run.TagEvildomLife is null && dungeon.DeathSerial == scheduled.DeathSerial && dungeon.Run.Snapshot.Identity.InstanceId == scheduled.InstanceId
            ? ReturnFromFormalDungeon() : null);

    // 扣币提交失败时保留死亡状态；只有实际提交结果才能发布复活。
    public InventoryResult? ReviveFormalCharacter(ushort targetKey)
    {
        Raids.AntonRaidBattle.RevivalReservation? reservation = null;
        Exception? revivalFailure = null;
        try
        {
            return owner.Execute(() =>
            {
                if (!CanChangeDungeonLife() || formalDungeon!.Run.TagEvildomLife is not null || !formalDungeon.CharacterDead || Selected!.Id != targetKey ||
                    formalDungeon.Entry.Maze.IsGriefTower) return null;
                bool luke = formalDungeon.Entry.RaidEntry?.Kind is Raids.RaidKind.Luke or Raids.RaidKind.Fiendwar;
                if (luke)
                {
                    if (raidCombat is not { Actor.IsCurrent: true, Battle.Kind: Raids.RaidKind.Luke or Raids.RaidKind.Fiendwar } raid ||
                        formalDungeon.Entry.RaidEntry!.InstanceId != raid.Battle.Instance || PreservesDungeonConsumables)
                        return InventoryResult.Reject();
                    reservation = raid.Battle.ReserveLukeRevival(raid.Actor, formalDungeon.Run.Snapshot.Identity, formalDungeon.DeathSerial);
                    if (reservation is null) return InventoryResult.Reject();
                }
                else if (formalDungeon.Entry.CoinLimit is { } maximum && formalDungeon.ReviveCount >= maximum)
                    return InventoryResult.Reject();
                try
                {
                    var result = CommitInventory(Selected, DungeonRevive.Plan(Selected, PreservesDungeonConsumables));
                    if (result.Accepted)
                    {
                        reservation?.Commit();
                        formalDungeon.CharacterDead = false;
                        formalDungeon.AltarRevivalDue = null;
                        formalDungeon.ReviveCount++;
                        formalDungeon.Run.QuestFacts.Revived();
                        if (formalDungeon.Entry.RaidEntry?.Kind == Raids.RaidKind.Anton)
                            PartyDungeon?.RecordAntonRevival();
                    }
                    return result;
                }
                finally { reservation?.Dispose(); }
            });
        }
        catch (Exception error) { revivalFailure = error; throw; }
        finally
        {
            // 扣币SQL与本人owner已经释放；只发布本次token产生的已提交快照。
            if (reservation?.Change is { } change)
            {
                try { PublishRaidCombat(change); }
                catch (Exception publicationError) when (revivalFailure is not null)
                { revivalFailure.Data["RaidPublicationFailure"] = publicationError.GetType().Name; }
            }
        }
    }

    // 清图后角色仍留在原房间，残留攻击可能致死；结算菜单不结束死亡与扣币资格。
    bool CanChangeDungeonLife() => pendingRental is null && pendingConsumableUse is null &&
        !HasPendingTownQuestCommit && !HasPendingFormalCard &&
        Ready && Selected is not null && Phase is SessionPhase.Dungeon or SessionPhase.Settlement &&
        formalDungeon is { } dungeon && dungeon.Run.Phase is SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement &&
        !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement && !dungeon.HasPendingFatigue &&
        !dungeon.Ground.HasPendingOperation && dungeon.PendingTutorialClear is null &&
        dungeon.PendingInventoryDiscard is null && dungeon.AltarSettlement is not { Stage: < 2 } && !HasPendingQuestScene;
}
