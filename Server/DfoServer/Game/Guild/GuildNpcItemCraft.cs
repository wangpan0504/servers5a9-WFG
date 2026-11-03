using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Guilds;

namespace DfoServer.Game.Guild;

internal sealed record GuildNpcItemCraftCompletion(MailboxResult Mail, GuildManagementCommit? Management);

internal partial interface IGuildNpcBenefitStore
{
    GuildNpcBenefitCommit ChangeGuildNpcItemCraft(GuildManagementIdentity actor, Character expected,
        GuildNpcCatalog npcs, GuildNpcBenefitDefinition definition, uint action, string requestId, long now,
        ItemCore? reward, Func<Character, InventoryPlan?> prepare);
    GuildNpcItemCraftCompletion[] CompleteGuildNpcItemCrafts(long now);
}

internal sealed partial class GuildNpcBenefitService
{
    public bool ItemCraft(uint benefitId) => catalog.Find(benefitId) is { } row && IndividualItemCraft(row);

    public static bool IndividualItemCraft(GuildNpcBenefitDefinition definition) =>
        definition is { Common: false, DurationSeconds: > 0, Parameters: [> 0, > 0, ..] } &&
        (definition.Effect == "crafting item" && definition.NpcId == GuildNpcCatalog.Alchemist &&
            definition.Parameters.Length == 2 ||
         definition.Effect == "purified teranium craft" && definition.NpcId == GuildNpcCatalog.Spirit &&
            definition.Parameters.Length >= 2 && (definition.Parameters.Length == 2 || definition.Parameters[2] is 0 or 1));

    public static bool WeeklyItemCraft(GuildNpcBenefitDefinition definition) =>
        IndividualItemCraft(definition) && definition.Parameters is [_, _, 1, ..];

    public GuildNpcBenefitCommit Craft(GuildManagementIdentity actor, Character expected, uint benefitId,
        uint action, string requestId)
    {
        if (!ItemCraft(benefitId) || action is not (4 or 5) || items is null) return new(14);
        var definition = catalog.Find(benefitId)!;
        long now = Now;
        ItemCore? core = action == 4 ? InventoryRewardGrantService.Create(items.Get(definition.Parameters[0]),
            definition.Parameters[1], DateTimeOffset.FromUnixTimeSeconds(now + definition.DurationSeconds)) : null;
        if (core is not null && (!core.IsStackable || core.IsExpired(now + definition.DurationSeconds)))
            throw new InvalidDataException("公会NPC个人生产需要有效堆叠产物");
        return store.ChangeGuildNpcItemCraft(actor, expected, npcs, definition, action, requestId, now, core,
            character => PreparePayment(character, definition, now));
    }

    public void CompleteItemCrafts()
    {
        foreach (var completion in store.CompleteGuildNpcItemCrafts(Now))
        {
            if (completion.Management is { } commit) management.Publish(commit);
            craftMailArrived?.Invoke(completion.Mail);
        }
    }
}
