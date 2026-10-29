using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Guilds;

namespace DfoServer.Game.Guild;

internal sealed record GuildNpcEmblemSnapshot(GuildNpcBenefitState Benefits, long JoinedAt);
internal sealed record GuildNpcEmblemSelection(uint GuildId, long JoinedAt, long Revision,
    GuildNpcBenefitDefinition Definition, GuildNpcBenefitProgress Progress);

internal partial interface IGuildNpcBenefitStore
{
    GuildNpcEmblemSnapshot? ReadGuildNpcEmblem(GuildManagementIdentity actor, long now);
    GuildNpcBenefitCommit CommitGuildNpcEmblem(GuildManagementIdentity actor, Character expected,
        InventoryPlan inventory, GuildNpcEmblemSelection selected, GuildNpcCatalog npcs,
        GuildNpcBenefitCatalog catalog, long now);
}

internal sealed partial class GuildNpcBenefitService
{
    public GuildNpcEmblemSelection? SelectEmblem(GuildManagementIdentity actor) =>
        store.ReadGuildNpcEmblem(actor, Now) is { } snapshot ? SelectEmblem(snapshot, npcs, catalog, Now) : null;

    internal static GuildNpcEmblemSelection? SelectEmblem(GuildNpcEmblemSnapshot snapshot,
        GuildNpcCatalog npcs, GuildNpcBenefitCatalog catalog, long now)
    {
        var state = snapshot.Benefits;
        return state.Common.Select(row => (Progress: row, Common: true))
            .Concat(state.Individual.Select(row => (Progress: row, Common: false)))
            .Select(row => (row.Progress, row.Common, Definition: catalog.Find(row.Progress.BenefitId)))
            .Where(row => row.Progress.Remaining > 0 && row.Progress.Deadline > now &&
                row.Definition is { Effect: "emblem compound buff", Parameters: [> 0, > 0] } definition &&
                definition.Common == row.Common && npcs.Find(definition.NpcId) is { } npc &&
                (!npc.Contract || state.Npcs.Any(progress => progress.NpcId == npc.Id && progress.ContractUntil > now)))
            .OrderByDescending(row => row.Definition!.Parameters[0]).ThenBy(row => row.Common).ThenBy(row => row.Progress.BenefitId)
            .Select(row => new GuildNpcEmblemSelection(state.Management.Guild.Id, snapshot.JoinedAt, state.Management.Revision,
                row.Definition!, row.Progress)).FirstOrDefault();
    }

    public GuildNpcBenefitCommit CommitEmblem(GuildManagementIdentity actor, Character expected,
        InventoryPlan inventory, GuildNpcEmblemSelection selected) =>
        store.CommitGuildNpcEmblem(actor, expected, inventory, selected, npcs, catalog, Now);
}
