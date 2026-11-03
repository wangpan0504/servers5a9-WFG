using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Guild;

internal sealed record GuildDungeonScrollPlan(Character Expected, InventoryItem Source,
    GuildDungeonClearScrollDefinition Definition, InventoryPlan Inventory, GuildDungeonMembership Membership);
internal sealed record GuildDungeonScrollCommit(ushort Error, GuildManagementState? State = null,
    GuildDungeonProgress? Progress = null, bool Replayed = false, Character? Character = null,
    InventoryPlan? Inventory = null)
{
    public bool Success => Error == 0;
}

internal interface IGuildDungeonScrollStore
{
    GuildDungeonMembership? ReadGuildDungeonScrollMembership(GuildManagementIdentity actor);
    GuildDungeonScrollCommit? ReplayGuildDungeonScroll(GuildManagementIdentity actor, string requestId, short slot, long now);
    GuildDungeonScrollCommit CommitGuildDungeonScroll(GuildManagementIdentity actor, GuildDungeonScrollPlan plan,
        GuildDungeonCatalog catalog, string requestId, long now);
}

internal sealed class GuildDungeonScrollService(IGuildDungeonScrollStore store, ItemCatalog items,
    GuildDungeonCatalog catalog, GuildManagementService management, TimeProvider? clock = null)
{
    public IGuildDungeonScrollStore Store => store;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    public long Now => Clock.GetUtcNow().ToUnixTimeSeconds();
    public GuildDungeonScrollPlan Prepare(GuildManagementIdentity actor, Character expected, InventorySlot slot)
    {
        var source = expected.Inventory.Find(slot);
        if (slot.List != InventoryListType.Main || source is null || source.Uid <= 0 || source.Core.Count <= 0 ||
            source.Core.EquipmentLockId != 0 || source.IsExpired(Now) ||
            !ItemSlotBoundService.Allows(expected.Inventory, slot, ItemKind.Consumable))
            throw new InvalidDataException("公会净化卷轴源实例无效");
        var definition = items.GuildDungeonClearScroll(source.Core.ItemId);
        if (definition is null || !definition.Item.AllowsUse(expected.Level, expected.Job) ||
            catalog.Find(definition.DungeonId) is null)
            throw new InvalidDataException("公会净化卷轴资源或使用资格无效");
        var membership = store.ReadGuildDungeonScrollMembership(actor)
            ?? throw new InvalidDataException("公会净化卷轴缺少当前会籍");
        var deletion = InventoryDeleteService.Plan(expected.Inventory, slot, source.Core.Value, 1);
        if (!deletion.Accepted) throw new InvalidDataException("公会净化卷轴不能扣除一件");
        return new(expected, source, definition, new(expected.Inventory, deletion.Plan!.After, "guild_dungeon_scroll",
            new(expected, source.ExpireTime > 0 ? source.ExpireTime : null, Clock, requirePosition: true)), membership);
    }
    public GuildDungeonScrollCommit Commit(GuildManagementIdentity actor, GuildDungeonScrollPlan plan, string requestId) =>
        store.CommitGuildDungeonScroll(actor, plan, catalog, requestId, Now);
    public void Publish(GuildDungeonScrollCommit result)
    {
        if (result.Success && result.State is { } state)
            management.Publish(new(0, state.Guild.Id, state.Revision, Members: state.Members));
    }
}
