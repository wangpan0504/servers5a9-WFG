using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Social;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Pvp;

internal enum PvpTotalTeamRenameFailure { None, InvalidName, Unavailable, MissingTeam, InvalidCard, NameTaken, Expired }
internal sealed record PvpTotalTeamRenamePlan(Character Expected, PvpTotalTeam Team, string Name,
    InventoryItem? Source, InventoryPlan? Inventory);
internal sealed record PvpTotalTeamRenameCommit(PvpTotalTeam Team, Character Character);
internal sealed record PvpTotalTeamRenameResult(PvpTotalTeamRenameFailure Failure,
    PvpTotalTeam? Team = null, SocialActor? Actor = null, InventoryResult? Inventory = null);

internal static class PvpTotalTeamRename
{
    // 0157B3EC按此模板查找Main容器，875回传查找结果的低16位；不是角色UID。
    public const int CardId = 490002850;

    public static PvpTotalTeamRenamePlan? Prepare(Character character, PvpTotalTeam team, string name, ushort slot,
        ItemTemplate? card, TimeProvider clock)
    {
        if (!PvpTotalTeam.ValidName(name)) return null;
        // 已提交名称的重放不再次消费；不凭同名请求创建队伍。
        if (name == team.Name) return new(character, team, name, null, null);
        if (slot > short.MaxValue || card is not { Id: CardId, Kind: ItemKind.Material } ||
            !card.AllowsActivation(character.Level, character.Job)) return null;
        var position = new InventorySlot(InventoryListType.Main, (short)slot);
        var source = character.Inventory.Find(position);
        if (source is not { Uid: > 0, Core: { ItemId: CardId, Kind: ItemKind.Material, Count: > 0, EquipmentLockId: 0 } } ||
            source.IsExpired(clock.GetUtcNow().ToUnixTimeSeconds()) ||
            !ItemSlotBoundService.Allows(character.Inventory, position, ItemKind.Material)) return null;
        var consumed = InventoryDeleteService.Plan(character.Inventory, position, CardId, 1);
        if (!consumed.Accepted) return null;
        return new(character, team, name, source, new(character.Inventory, consumed.Plan!.After, "pvp_total_team_rename",
            new(character, source.ExpireTime > 0 ? source.ExpireTime : null, clock)));
    }
}
