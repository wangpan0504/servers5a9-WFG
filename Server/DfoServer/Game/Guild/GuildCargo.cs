using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Guild;

internal sealed record GuildCargoHistory(long Id, long Time, byte Kind, string Name, ItemCore Core, int Count);
internal sealed record GuildCargoView(uint GuildId, long Revision, int Capacity, ImmutableArray<InventoryItem> Items,
    ImmutableArray<GuildCargoHistory> History);
internal sealed record GuildCargoRequest(bool Deposit, InventoryListType List, short InventorySlot, short CargoSlot, int ItemId, int Count)
{
    public bool Move { get; init; }
    public short TargetSlot { get; init; }
    public int TargetItemId { get; init; }
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
        $"{Deposit}:{(byte)List}:{InventorySlot}:{CargoSlot}:{ItemId}:{Count}:{Move}:{TargetSlot}:{TargetItemId}"))));
}
internal sealed record GuildCargoActor(AccountOwners.Lease Owner, Character Character, long Epoch, Func<bool> IsCurrent);
internal sealed record GuildCargoPlan(ushort Error, GuildCargoRequest Request, GuildCargoView? Before = null,
    ImmutableArray<InventoryItem> After = default, InventoryPlan? Inventory = null, short Destination = 0, ItemCore? Core = null);
internal sealed record GuildCargoRead(ushort Error, GuildCargoView? View = null);
internal sealed record GuildCargoCommit(ushort Error, GuildCargoView? View = null, Character? Character = null,
    InventoryPlan? Inventory = null, bool Replayed = false, short Destination = 0, GuildManagementTarget[]? Members = null)
{
    public bool Success => Error == 0;
}
internal sealed record GuildCargoReceipt(GuildCargoView View, Character Character, Func<bool> IsCurrent);
internal sealed record GuildCargoTransferReceipt(GuildCargoCommit Commit, GuildCargoReceipt? Cargo, InventoryResult? Inventory);
internal sealed record GuildCargoNotice(uint GuildId);

internal interface IGuildCargoStore
{
    GuildCargoRead ReadGuildCargo(GuildCargoActor actor, bool initialize);
    GuildCargoCommit CommitGuildCargo(GuildCargoActor actor, GuildCargoPlan plan, string requestId, long now);
}

internal sealed class GuildCargoService(IGuildCargoStore store, ItemCatalog items, GuildCargoCatalog cargo,
    OnlineSessionDirectory online, int maxCapacity = 56, TimeProvider? clock = null)
{
    public IGuildCargoStore Store => store;
    public IReadOnlySet<uint> StorageNpcs => cargo.StorageNpcs;
    public long Now => (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
    public GuildCargoPlan Plan(Character character, GuildCargoView? view, GuildCargoRequest request)
    {
        if (view is null) return new(196, request);
        if (request.Move) return GuildCargoRules.Move(character, view, request, maxCapacity);
        InventoryItem? source = request.Deposit ? character.Inventory.Find(new(request.List, request.InventorySlot)) :
            view.Items.FirstOrDefault(item => item.Position.Index == request.CargoSlot);
        if (source is null) return new(198, request, view);
        ItemTemplate template;
        try { template = items.Get(source.Core.ItemId); }
        catch (InvalidDataException) { return new(198, request, view); }
        bool allowed;
        try { allowed = cargo.Allows(source.Core.ItemId); }
        catch (InvalidDataException) { allowed = false; }
        return GuildCargoRules.Plan(character, view, request, template, allowed, Now, maxCapacity);
    }

    public void Publish(GuildCargoCommit commit, GameSession actor)
    {
        if (!commit.Success || commit.Replayed || commit.View is not { } view) return;
        // 已释放发起者账号锁；每个目标仅在自己的锁内读取与入队，不等待网络。
        foreach (var member in commit.Members ?? [])
            if (online.Find(member.Account) is { } session && !ReferenceEquals(session, actor))
                session.TryDeliverGuildCargo(member.Id, new(view.GuildId));
    }
}

internal static class GuildCargoRules
{
    public const uint Permission = 1u << 15;

    public static GuildCargoPlan Move(Character character, GuildCargoView view, GuildCargoRequest request, int maxCapacity)
    {
        GuildCargoPlan Fail() => new(198, request, view);
        if (!request.Move || request.Deposit || view.Capacity is < 8 || view.Capacity > maxCapacity ||
            request.CargoSlot < 0 || request.CargoSlot >= view.Capacity || request.TargetSlot < 0 || request.TargetSlot >= view.Capacity ||
            request.CargoSlot == request.TargetSlot || request.Count != 0) return Fail();
        var stored = view.Items.ToDictionary(item => item.Position.Index);
        if (!stored.TryGetValue(request.CargoSlot, out var source) || source.Core.ItemId != request.ItemId ||
            source.Core.Count <= 0 || source.Core.EquipmentLockId != 0) return Fail();
        stored.TryGetValue(request.TargetSlot, out var target);
        if ((target?.Core.ItemId ?? 0) != request.TargetItemId || target is not null &&
            (target.Core.Count <= 0 || target.Core.EquipmentLockId != 0)) return Fail();
        stored.Remove(request.CargoSlot);
        stored[request.TargetSlot] = source with { Position = new(InventoryListType.Main, request.TargetSlot) };
        if (target is not null) stored[request.CargoSlot] = target with { Position = new(InventoryListType.Main, request.CargoSlot) };
        var inventory = new InventoryPlan(character.Inventory, character.Inventory, "guild_cargo_move",
            new(character, requirePosition: true));
        return new(0, request, view, stored.Values.OrderBy(item => item.Position.Index).ToImmutableArray(), inventory,
            request.TargetSlot, source.Core);
    }

    public static bool Eligible(InventoryItem item, ItemTemplate template, bool allowed, long now)
    {
        var core = item.Core;
        string attach = template.AttachType.Replace("`", "").Trim().Trim('[', ']').Trim().ToLowerInvariant();
        return allowed && core.ItemId == template.Id && core.Kind == template.Kind &&
            core.ItemId is not (3033 or 3034 or 3035 or 3036 or 3037 or 3262) &&
            ItemSlotBoundService.List(core.Kind) is InventoryListType.Main or InventoryListType.Pet &&
            core.Kind is not (ItemKind.Avatar or ItemKind.Creature) && item.Avatar is null && item.Creature is null &&
            !item.IsExpired(now) && !core.IsRental && core.EquipmentLockId == 0 && core.TradeRestriction == 0 &&
            (attach is "free" or "trade" && core.SealFlag == 0 || attach == "sealing" &&
                core.Kind == ItemKind.Equipment && core.SealFlag == 1) &&
            core.Count > 0 && (!core.IsStackable || template.StackLimit > 0 && core.Count <= template.StackLimit);
    }

    public static GuildCargoPlan Plan(Character character, GuildCargoView view, GuildCargoRequest request,
        ItemTemplate template, bool allowed, long now, int maxCapacity)
    {
        if (request.Move) return Move(character, view, request, maxCapacity);
        GuildCargoPlan Fail(ushort error = 198) => new(error, request, view);
        if (view.Capacity is < 8 || view.Capacity > maxCapacity || request.Count <= 0 || request.CargoSlot < 0 ||
            request.CargoSlot >= view.Capacity || request.List is not (InventoryListType.Main or InventoryListType.Pet)) return Fail();
        var inventory = character.Inventory.Items.ToDictionary(item => item.Position);
        var stored = view.Items.ToDictionary(item => item.Position.Index);
        var source = request.Deposit ? inventory.GetValueOrDefault(new(request.List, request.InventorySlot)) : stored.GetValueOrDefault(request.CargoSlot);
        if (source is null || source.Core.ItemId != request.ItemId || request.Count > source.Core.Count ||
            !source.Core.IsStackable && request.Count != 1 || !Eligible(source, template, allowed, now)) return Fail();
        short destinationSlot = request.CargoSlot;
        InventoryItem? target;
        if (request.Deposit)
            target = stored.GetValueOrDefault(destinationSlot);
        else
        {
            if (request.List != ItemSlotBoundService.List(source.Core.Kind)) return Fail();
            target = inventory.Values.Where(item => item.Position.List == request.List &&
                InventoryStackRuleService.CanMerge(source.Core, item.Core, request.Count, template.StackLimit))
                .OrderBy(item => item.Position.Index).FirstOrDefault();
            if (target is not null) destinationSlot = target.Position.Index;
            else
            {
                var range = ItemSlotBoundService.Range(source.Core.Kind, character.Inventory);
                int empty = Enumerable.Range(range.Start, range.End - range.Start + 1)
                    .FirstOrDefault(slot => !inventory.ContainsKey(new(request.List, (short)slot)), -1);
                if (empty < 0) return Fail(204);
                destinationSlot = checked((short)empty);
            }
        }
        if (target is not null && !InventoryStackRuleService.CanMerge(source.Core, target.Core, request.Count, template.StackLimit)) return Fail();
        InventoryItem moving = source with { Position = new(request.List, destinationSlot) };
        if (target is not null) moving = target with { Core = target.Core with { Value = checked(target.Core.Value + request.Count) } };
        else if (request.Count < source.Core.Count) moving = moving with { Uid = 0, Core = moving.Core with { Value = request.Count } };
        InventoryItem? remaining = request.Count == source.Core.Count ? null : source with { Core = source.Core with { Value = source.Core.Value - request.Count } };
        if (request.Deposit)
        {
            if (remaining is null) inventory.Remove(source.Position); else inventory[source.Position] = remaining;
            stored[destinationSlot] = moving with { Position = new(InventoryListType.Main, destinationSlot) };
        }
        else
        {
            if (remaining is null) stored.Remove(request.CargoSlot); else stored[request.CargoSlot] = remaining;
            inventory[new(request.List, destinationSlot)] = moving;
        }
        var after = character.Inventory with { Items = inventory.Values.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray() };
        after.Validate(allocating: true);
        var plan = new InventoryPlan(character.Inventory, after, request.Deposit ? "guild_cargo_deposit" : "guild_cargo_withdraw",
            new(character, source.ExpireTime > 0 ? source.ExpireTime : null, requirePosition: true));
        return new(0, request, view, stored.Values.OrderBy(item => item.Position.Index).ToImmutableArray(), plan, destinationSlot, source.Core);
    }
}
