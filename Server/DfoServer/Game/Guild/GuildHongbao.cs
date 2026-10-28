using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Guilds;

namespace DfoServer.Game.Guild;

internal sealed record GuildHongbaoRow(uint Id, int ItemId, string Sender, byte Remaining, long Expires);
internal sealed record GuildHongbaoClaim(byte Level, string Name, uint Amount);
internal sealed record GuildHongbaoList(ushort Members, uint SpentCera, uint Taken, GuildHongbaoRow[] Rows);
internal sealed record GuildHongbaoCommit(ushort Error, uint Id = 0, int Amount = 0, int Taken = 0,
    Character? Character = null, InventoryPlan? Inventory = null)
{
    public bool Success => Error == 0;
}

internal interface IGuildHongbaoStore
{
    GuildHongbaoList? ReadGuildHongbao(AccountOwners.Lease owner, ushort character, long now);
    GuildHongbaoClaim[]? ReadGuildHongbaoHistory(AccountOwners.Lease owner, ushort character, uint id);
    GuildHongbaoCommit IssueGuildHongbao(GuildManagementIdentity actor, Character expected,
        GuildHongbaoDefinition definition, byte count, long now, Func<Character, InventoryPlan?> prepare);
    GuildHongbaoCommit TakeGuildHongbao(GuildManagementIdentity actor, Character expected, uint id,
        int dailyLimit, long now, Func<Character, int, InventoryPlan?> prepare);
    GuildHongbaoCommit RefundGuildHongbao(GuildManagementIdentity actor, Character expected, long now,
        Func<Character, int, InventoryPlan?> prepare);
}

internal sealed class GuildHongbaoService(IGuildHongbaoStore store, GuildHongbaoCatalog catalog, TimeProvider? clock = null)
{
    public const int HappyTokenItem = 2681917;
    public const int MaximumRows = 500;
    public const int LifetimeSeconds = 24 * 3600;
    long Now => (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();

    public GuildHongbaoList? Read(AccountOwners.Lease owner, ushort character) => store.ReadGuildHongbao(owner, character, Now);
    public GuildHongbaoClaim[]? History(AccountOwners.Lease owner, ushort character, uint id) =>
        store.ReadGuildHongbaoHistory(owner, character, id);

    public GuildHongbaoCommit Issue(GuildManagementIdentity actor, Character expected, uint itemId, byte count)
    {
        var definition = catalog.ByCommodity.Values.SingleOrDefault(row => (uint)row.ItemId == itemId);
        if (definition is null || count is < 5 or > 30) return new(7);
        long now = Now;
        return store.IssueGuildHongbao(actor, expected, definition, count, now, character =>
        {
            var source = character.Inventory.Items.Where(item => item.Position.List == InventoryListType.Main &&
                item.Core.ItemId == definition.ItemId && item.Core.IsStackable && item.Core.EquipmentLockId == 0 &&
                !item.IsExpired(now)).OrderBy(item => item.Position.Index).FirstOrDefault();
            if (source is null) return null;
            var removed = InventoryDeleteService.Plan(character.Inventory, source.Position, definition.ItemId, 1);
            return removed.Accepted ? new(character.Inventory, removed.Plan!.After, "guild_hongbao_issue",
                new(character, source.ExpireTime > 0 ? source.ExpireTime : null, clock)) : null;
        });
    }

    public GuildHongbaoCommit Take(GuildManagementIdentity actor, Character expected, uint id) =>
        store.TakeGuildHongbao(actor, expected, id, catalog.DailyTakeLimit, Now,
            (character, amount) => Credit(character, CurrencyKind.HappyTokenCera, amount, "guild_hongbao_take"));

    public GuildHongbaoCommit Refund(GuildManagementIdentity actor, Character expected) =>
        store.RefundGuildHongbao(actor, expected, Now,
            (character, amount) => Credit(character, CurrencyKind.TokenCera, amount, "guild_hongbao_refund"));

    static InventoryPlan? Credit(Character character, CurrencyKind currency, int amount, string reason)
    {
        long after = CurrencyWallet.Get(character.Inventory, currency) + amount;
        return amount > 0 && after <= int.MaxValue
            ? new(character.Inventory, CurrencyWallet.Set(character.Inventory, currency, after), reason, new(character)) : null;
    }

    // 本服规则：等额分配，整数余数按领取先后每份加1，总到账与剩余严格守恒。
    public static int Share(int total, int count, int taken) => total / count + (taken < total % count ? 1 : 0);
}
