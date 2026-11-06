using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Guild;

internal sealed record GuildExperienceGiftPlan(Character Expected, InventoryItem Source,
    GuildExperienceGiftDefinition Definition, InventoryPlan Inventory, GuildExperienceMembership Membership);
internal sealed record GuildExperienceMembership(uint GuildId, long JoinedAtUnix);
internal sealed record GuildExperienceCommit(ushort Error, GuildManagementState? State = null, bool Replayed = false,
    Character? Character = null, InventoryPlan? Inventory = null)
{
    public bool Success => Error == 0;
}
internal interface IGuildExperienceStore
{
    GuildExperienceMembership? ReadGuildExperienceMembership(GuildManagementIdentity actor);
    GuildExperienceCommit? ReplayGuildExperienceGift(GuildManagementIdentity actor, string requestId, short slot, long now);
    GuildExperienceCommit CommitGuildExperienceGift(GuildManagementIdentity actor, GuildExperienceGiftPlan plan,
        GuildParameterCatalog parameters, string requestId, long now);
}

internal sealed class GuildExperienceService(IGuildExperienceStore store, ItemCatalog items,
    GuildParameterCatalog parameters, GuildManagementService management, TimeProvider? clock = null)
{
    public IGuildExperienceStore Store => store;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    public long Now => Clock.GetUtcNow().ToUnixTimeSeconds();
    public GuildExperienceGiftPlan Prepare(GuildManagementIdentity actor, Character expected, InventorySlot slot)
    {
        var source = expected.Inventory.Find(slot);
        if (slot.List != InventoryListType.Main || source is null || source.Uid <= 0 || source.Core.Count <= 0 ||
            source.Core.EquipmentLockId != 0 || source.IsExpired(Now) ||
            !ItemSlotBoundService.Allows(expected.Inventory, slot, ItemKind.Consumable))
            throw new InvalidDataException("公会经验书源实例无效");
        var definition = items.GuildExperienceGift(source.Core.ItemId);
        if (definition is null || !definition.Item.AllowsActivation(expected.Level, expected.Job))
            throw new InvalidDataException("公会经验书资源或使用资格无效");
        var membership = store.ReadGuildExperienceMembership(actor)
            ?? throw new InvalidDataException("公会经验书缺少当前会籍");
        var deletion = InventoryDeleteService.Plan(expected.Inventory, slot, source.Core.Value, 1);
        if (!deletion.Accepted) throw new InvalidDataException("公会经验书不能扣除一件");
        return new(expected, source, definition, new(expected.Inventory, deletion.Plan!.After, "guild_experience_gift",
            new(expected, source.ExpireTime > 0 ? source.ExpireTime : null, Clock, requirePosition: true)), membership);
    }
    public GuildExperienceCommit Commit(GuildManagementIdentity actor, GuildExperienceGiftPlan plan, string requestId) =>
        store.CommitGuildExperienceGift(actor, plan, parameters, requestId, Now);
    public void Publish(GuildExperienceCommit result)
    {
        if (result.Success && result.State is { } state)
            management.Publish(new(0, state.Guild.Id, state.Revision, Members: state.Members));
    }
}
