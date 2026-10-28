using DfoServer.Game.Accounts;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;

namespace DfoServer.Game.Guild;

internal sealed record GuildAppearance(uint Mark = 0, uint AuraMark = uint.MaxValue, uint AuraFrame = uint.MaxValue);
internal sealed record GuildAppearanceChange(bool Aura, uint Mark = 0, uint AuraMark = uint.MaxValue,
    uint AuraFrame = uint.MaxValue, bool LegacyMark = false);
internal sealed record GuildAppearanceCommit(ushort Error, GuildAppearance? Appearance = null,
    GuildManagementCommit? Refresh = null)
{
    public bool Success => Error == 0;
}
internal interface IGuildAppearanceStore
{
    GuildAppearance? ReadGuildAppearance(AccountOwners.Lease owner, ushort characterId);
    GuildAppearanceCommit ChangeGuildAppearance(GuildManagementIdentity actor, GuildAppearanceChange change,
        GuildAppearanceCatalog catalog);
}

internal sealed class GuildAppearanceService(IGuildAppearanceStore store, GuildAppearanceCatalog catalog,
    GuildManagementService management)
{
    public GuildManagementIdentity? Capture(GameSession session) => session.CaptureGuildManagement();
    public GuildAppearanceCommit Change(GuildManagementIdentity actor, GuildAppearanceChange change) =>
        store.ChangeGuildAppearance(actor, change, catalog);
    public void Publish(GuildAppearanceCommit commit)
    {
        if (commit.Success && commit.Refresh is { } refresh) management.Publish(refresh);
    }
}
