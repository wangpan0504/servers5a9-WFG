using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Town;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public FormalDungeonReceipt? ReportFormalCharacterDeath() => owner.Execute(() =>
    {
        if (!CanChangeDungeonLife()) return null;
        formalDungeon!.CharacterDead = true;
        formalDungeon.DeathSerial++;
        return FormalReceipt();
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
        var result = CommitInventory(Selected, DungeonRevive.Plan(Selected));
        if (result.Accepted) formalDungeon.CharacterDead = false;
        return result;
    });

    bool CanChangeDungeonLife() => Ready && Selected is not null && Phase == SessionPhase.Dungeon &&
        formalDungeon is { } dungeon && dungeon.Run.Phase == SinglePlayerDungeonPhase.Dungeon &&
        !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement && !dungeon.HasPendingFatigue &&
        !dungeon.Ground.HasPendingPickup && dungeon.PendingTutorialClear is null && !HasPendingQuestScene;
}
