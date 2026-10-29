using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Guild;

internal sealed record GuildNpcBenefitProgress(uint BenefitId, int Remaining, long Deadline, bool Selected = false);
internal sealed record GuildNpcBenefitState(GuildManagementState Management,
    GuildNpcBenefitProgress[] Common, GuildNpcBenefitProgress[] Individual)
{
    public GuildNpcProgress[] Npcs { get; init; } = [];
}
internal sealed record GuildNpcBenefitCommit(int Error, GuildNpcBenefitState? State = null, bool Replayed = false,
    Character? Character = null, InventoryPlan? Inventory = null)
{
    public bool Success => Error == 0;
}
internal partial interface IGuildNpcBenefitStore
{
    GuildNpcBenefitState? ReadGuildNpcBenefits(GuildManagementIdentity actor, long now);
    GuildNpcBenefitCommit PurchaseGuildNpcBenefit(GuildManagementIdentity actor, Character expected,
        GuildNpcCatalog npcs, GuildNpcBenefitDefinition definition, string requestId, long now,
        Func<Character, InventoryPlan?> prepare);
    GuildNpcBenefitCommit SelectGuildNpcTraining(GuildManagementIdentity actor, GuildNpcBenefitCatalog catalog,
        uint[] benefits, string requestId, long now);
}

internal sealed partial class GuildNpcBenefitService(IGuildNpcBenefitStore store, GuildNpcCatalog npcs,
    GuildNpcBenefitCatalog catalog, EpicPieceCatalog pieces, GuildManagementService management, TimeProvider? clock = null,
    ItemCatalog? items = null, Action<Game.Mailbox.MailboxResult>? craftMailArrived = null)
{
    public GuildNpcBenefitCatalog Catalog => catalog;
    public long Now => (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
    public GuildNpcBenefitState? Read(GuildManagementIdentity actor) => store.ReadGuildNpcBenefits(actor, Now);
    public int SuccessPercent(GuildManagementIdentity? actor, string effect, int targetLevel)
    {
        if (actor is null || Read(actor) is not { } state) return 0;
        long now = Now;
        return state.Common.Concat(state.Individual).Select(row => (Progress: row, Definition: catalog.Find(row.BenefitId)))
            .Where(row => row.Progress.Deadline > now && row.Definition is { Parameters.Length: 2 } definition &&
                definition.Effect == effect && definition.Parameters[1] == targetLevel &&
                npcs.Find(definition.NpcId) is { } npc &&
                (!npc.Contract || state.Npcs.Any(progress => progress.NpcId == npc.Id && progress.ContractUntil > now)))
            .Select(row => row.Definition!.Parameters[0]).DefaultIfEmpty(0).Max();
    }
    public static bool Training(GuildNpcBenefitDefinition definition) =>
        definition.Effect is "guild trainer attack raid buff" or "guild trainer critical raid buff" or
            "guild trainer speed raid buff" or "guild trainer attribute raid buff";
    public static bool Timed(GuildNpcBenefitDefinition definition) => definition.Effect is
        "equip upgrade buff" or "equip upgrade separate buff" or "equip amplify upgrade buff" or "emblem compound buff";

    public GuildNpcBenefitCommit Buy(GuildManagementIdentity actor, Character expected, uint npcId,
        uint benefitId, string requestId)
    {
        if (catalog.Find(benefitId) is not { } definition ||
            definition.NpcId != npcId && !(npcId == 0 && Training(definition)) ||
            !(Timed(definition) || Training(definition))) return new(14);
        long now = Now;
        return store.PurchaseGuildNpcBenefit(actor, expected, npcs, definition, requestId, now,
            character => PreparePayment(character, definition, now));
    }
    InventoryPlan? PreparePayment(Character character, GuildNpcBenefitDefinition definition, long now)
    {
            var after = character.Inventory;
            foreach (var (currency, amount) in new[] { (definition.FirstCurrency, definition.FirstAmount),
                (definition.SecondCurrency, definition.SecondAmount) })
            {
                if (amount == 0) continue;
                if (currency == "member coins")
                {
                    var paid = InventoryMaterialConsumptionService.Plan(after,
                        [new(GuildNpcCatalog.MemberCoinItem, amount)], pieces, DateTimeOffset.FromUnixTimeSeconds(now));
                    if (!paid.Accepted) return null;
                    after = paid.Plan!.After;
                }
                else if (currency == "gold")
                {
                    var paid = CurrencyService.Spend(after, CurrencyKind.Gold, amount);
                    if (!paid.Accepted) return null;
                    after = paid.Plan!.After;
                }
                else if (currency != "none") return null;
            }
            return new(character.Inventory, after, "guild_npc_benefit_purchase", new(character));
    }
    public GuildNpcBenefitCommit Select(GuildManagementIdentity actor, uint[] selected, string requestId) =>
        store.SelectGuildNpcTraining(actor, catalog, selected, requestId, Now);
    public void Publish(GuildNpcBenefitCommit commit)
    {
        if (commit.Success && commit.State is { } state)
            management.Publish(new(0, state.Management.Guild.Id, state.Management.Revision,
                Members: state.Management.Members) { NpcsChanged = true });
    }
}
