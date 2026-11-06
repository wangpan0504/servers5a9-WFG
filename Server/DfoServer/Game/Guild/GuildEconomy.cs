using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Guild;

internal sealed record GuildBenefit(byte Type, uint Value, long ExpiresUnix)
{
    public bool Active(long now) => Value > 0 && (Type == 7 || ExpiresUnix == 0 || now < ExpiresUnix);
}
internal sealed record GuildMileageEntry(long UnixSeconds, string Name, byte Kind, uint ContentId, int Amount);
internal sealed record GuildEconomyState(uint GuildId, int PublicFunds, int MemberLimit, long Revision,
    GuildBenefit[] Benefits, GuildManagementTarget[] Members);
internal sealed record GuildEconomyCommit(ushort Error, GuildEconomyState? State = null, bool Replayed = false,
    Character? Character = null, InventoryPlan? Inventory = null)
{
    public bool Success => Error == 0;
    public bool CargoChanged { get; init; }
}
internal sealed record GuildMileageGiftPlan(Character Expected, InventoryItem Source, GuildMileageGiftDefinition Definition,
    InventoryPlan Inventory);

internal interface IGuildEconomyStore
{
    GuildEconomyState? ReadGuildEconomy(AccountOwners.Lease owner, ushort characterId, long now);
    GuildMileageEntry[] ReadGuildMileageHistory(AccountOwners.Lease owner, ushort characterId);
    int ReadGuildMemberLimit(AccountOwners.Lease owner, ushort characterId);
    int ReadGuildKillBonus(ushort characterId, long now);
    GuildExperienceEntitlement? ReadGuildKillEntitlement(ushort characterId, long now);
    GuildEconomyCommit PurchaseGuildContent(GuildManagementIdentity actor, GuildContentDefinition content,
        string requestId, long now, int cargoLimit);
    GuildEconomyCommit CommitGuildMileageGift(GuildManagementIdentity actor, GuildMileageGiftPlan plan,
        string requestId, long now);
    GuildEconomyCommit? ReplayGuildMileageGift(GuildManagementIdentity actor, string requestId, short slot, long now);
}

internal sealed class GuildExperienceBonusProvider(IGuildEconomyStore store) : IExperienceBonusProvider
{
    public ExperienceBonusContext Capture(ExperienceBonusRequest request)
    {
        if (request.Event != ExperienceEvent.Kill) return ExperienceBonusContext.Empty;
        var entitlement = store.ReadGuildKillEntitlement(request.Character.Id, request.UnixSeconds);
        return entitlement is null ? ExperienceBonusContext.Empty : new()
            { Rates = [new(ExperienceBonusSource.Guild, entitlement.Percent)], GuildEntitlement = entitlement };
    }
}

internal sealed class GuildEconomyService(IGuildEconomyStore store, GuildContentCatalog catalog, ItemCatalog items,
    GuildManagementService management, TimeProvider? clock = null, Action<GuildEconomyState>? cargoChanged = null)
{
    public IGuildEconomyStore Store => store;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    public long Now => Clock.GetUtcNow().ToUnixTimeSeconds();
    public GuildEconomyState? Read(GuildManagementIdentity actor) =>
        actor.IsCurrent() ? store.ReadGuildEconomy(actor.Owner, actor.CharacterId, Now) : null;
    public GuildEconomyCommit Buy(GuildManagementIdentity actor, uint content, string requestId)
    {
        if (catalog.Find(content) is not { } definition) return new(14);
        var result = store.PurchaseGuildContent(actor, definition, requestId, Now, catalog.CargoLimit);
        return result with { CargoChanged = result.Success && !result.Replayed && definition.Type == 4 };
    }
    public GuildMileageGiftPlan PrepareGift(Character expected, InventorySlot slot)
    {
        var source = expected.Inventory.Find(slot);
        if (slot.List != InventoryListType.Main || source is null || source.Uid <= 0 || source.Core.Count <= 0 ||
            source.Core.EquipmentLockId != 0 || source.IsExpired(Now) ||
            !ItemSlotBoundService.Allows(expected.Inventory, slot, ItemKind.Consumable))
            throw new InvalidDataException("公会积分礼袋源实例无效");
        var definition = items.GuildMileageGift(source.Core.ItemId);
        if (definition is null || !definition.Item.AllowsActivation(expected.Level, expected.Job))
            throw new InvalidDataException("公会积分礼袋资源或使用资格无效");
        var deletion = InventoryDeleteService.Plan(expected.Inventory, slot, source.Core.Value, 1);
        if (!deletion.Accepted) throw new InvalidDataException("公会积分礼袋不能扣除一件");
        return new(expected, source, definition, new(expected.Inventory, deletion.Plan!.After, "guild_mileage_gift",
            new(expected, source.ExpireTime > 0 ? source.ExpireTime : null, Clock, requirePosition: true)));
    }
    public void Publish(GuildEconomyCommit commit)
    {
        if (commit.Success && commit.State is { } state)
        {
            management.Publish(new(0, state.GuildId, state.Revision, Members: state.Members));
            if (commit.CargoChanged) cargoChanged?.Invoke(state);
        }
    }
    public void PublishDungeonMileage(GameSession session, uint guildId)
    {
        if (guildId == 0 || session.CaptureGuildManagement(requireTown: false) is not { } actor ||
            !actor.IsCurrent() || store.ReadGuildEconomy(actor.Owner, actor.CharacterId, Now) is not { } state ||
            state.GuildId != guildId) return;
        management.Publish(new(0, state.GuildId, state.Revision, Members: state.Members));
    }
    internal static bool ValidRequestId(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
