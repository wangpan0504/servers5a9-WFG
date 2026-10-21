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
        if (!CanChangeDungeonLife()) return null;
        if (formalDungeon!.CharacterDead) return null;
        var character = Selected!;
        long now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        bool blessing = character.Inventory.ConsumableStates.Concat(formalDungeon!.FreeConsumableEffects).Any(row => row.Kind == ConsumableStateKind.Effect &&
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
        return FormalReceipt() with { AutomaticRevival = blessing };
    });

    public DungeonDeathReturnReceipt? CurrentDungeonDeathReturn() => owner.Execute(() =>
        Ready && Selected is { } character && formalDungeon is { CharacterDead: true } dungeon
            ? new DungeonDeathReturnReceipt(character.Id, owner.Generation, SelectionEpoch,
                dungeon.Run.Snapshot.Identity.InstanceId, dungeon.DeathSerial) : null);

    // 不绑定角色快照或房间epoch：现场购买和死亡后的残留攻击都不能丢失回城计时。
    public TownTravelReceipt? ReturnFromFormalDungeonOnDeath(DungeonDeathReturnReceipt scheduled) => owner.Execute(() =>
        Ready && Selected?.Id == scheduled.CharacterId && owner.Generation == scheduled.Generation &&
        SelectionEpoch == scheduled.SelectionEpoch && formalDungeon is { CharacterDead: true } dungeon &&
        dungeon.DeathSerial == scheduled.DeathSerial && dungeon.Run.Snapshot.Identity.InstanceId == scheduled.InstanceId
            ? ReturnFromFormalDungeon() : null);

    // 扣币提交失败时保留死亡状态；只有实际提交结果才能发布复活。
    public InventoryResult? ReviveFormalCharacter(ushort targetKey) => owner.Execute(() =>
    {
        if (!CanChangeDungeonLife() || !formalDungeon!.CharacterDead || Selected!.Id != targetKey) return null;
        var result = CommitInventory(Selected, DungeonRevive.Plan(Selected,PreservesDungeonConsumables));
        if (result.Accepted) formalDungeon.CharacterDead = false;
        return result;
    });

    bool CanChangeDungeonLife() => Ready && Selected is not null && Phase == SessionPhase.Dungeon &&
        formalDungeon is { } dungeon && dungeon.Run.Phase == SinglePlayerDungeonPhase.Dungeon &&
        !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement && !dungeon.HasPendingFatigue &&
        !dungeon.Ground.HasPendingOperation && dungeon.PendingTutorialClear is null && !HasPendingQuestScene;
}
