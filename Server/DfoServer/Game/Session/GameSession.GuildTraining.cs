using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed record GuildTrainingSnapshot(ImmutableArray<byte> States, long Deadline, int Coins)
{
    public int Remaining(long now) => checked((int)Math.Max(0, Deadline - now));
}
internal sealed record GuildTrainingEntryPlan(GuildTrainingSnapshot Snapshot, InventoryPlan Inventory);
internal sealed record GuildTrainingProjectionReceipt(ImmutableArray<byte> States, int RemainingSeconds,
    Guid InstanceId, long Deadline, Func<bool> IsCurrent);

// 仅记录本会话已规划的训练投影，避免给未发动训练的玩家额外发清除包。
internal sealed class GuildTrainingProjectionState
{
    long selectionEpoch = -1;
    long version;
    bool projected;

    public long? Plan(long epoch, bool active)
    {
        if (selectionEpoch != epoch)
        {
            selectionEpoch = epoch;
            projected = false;
            version++;
        }
        if (!active && !projected) return null;
        projected = active;
        return ++version;
    }

    public bool IsCurrent(long epoch, long expectedVersion) => selectionEpoch == epoch && version == expectedVersion;
}

internal sealed partial class GameSession
{
    readonly GuildTrainingProjectionState guildTrainingProjection = new();

    GuildTrainingEntryPlan? PrepareGuildTraining(Character character, InventoryPlan? entryItems)
    {
        if (guildNpcBenefits is null || pieces is null ||
            CaptureGuildManagement(requireTown: false) is not { } actor || guildNpcBenefits.Read(actor) is not { } state)
            return null;
        long now = guildNpcBenefits.Now;
        if (!state.Npcs.Any(npc => npc.NpcId == 400001064 && npc.ContractUntil > now)) return null;
        var selected = state.Individual.Where(progress => progress.Selected)
            .Select(progress => guildNpcBenefits.Catalog.Find(progress.BenefitId))
            .Where(definition => definition is not null && GuildNpcBenefitService.Training(definition))
            .Cast<GuildNpcBenefitDefinition>().ToArray();
        return PlanGuildTraining(entryItems?.After ?? character.Inventory, selected, pieces, now,
            guildNpcBenefits.Catalog.TrainingIntervalSeconds);
    }

    internal static GuildTrainingEntryPlan? PlanGuildTraining(InventorySnapshot inventory,
        IReadOnlyList<GuildNpcBenefitDefinition> selected, EpicPieceCatalog pieces, long now, int seconds)
    {
        if (selected.Count == 0) return null;
        if (seconds <= 0 || selected.Count > 4 || selected.Select(row => row.Effect).Distinct().Count() != selected.Count ||
            selected.Any(row => row.Id is < 5001 or > 5016 || !GuildNpcBenefitService.Training(row) ||
                row.Parameters is not [1281, >= 1 and <= 4, > 0]))
            throw new InvalidDataException("公会训练入场定义无效");
        int coins = selected.Sum(row => row.Parameters[1]);
        var payment = InventoryMaterialConsumptionService.Plan(inventory, [new(GuildNpcCatalog.MemberCoinItem, coins)],
            pieces, DateTimeOffset.FromUnixTimeSeconds(now));
        // 本服约定：总额不足时整组不发动且不扣币，正常副本入场仍继续。
        if (!payment.Accepted || payment.Plan is not { } plan) return null;
        byte[] states = new byte[16];
        foreach (var row in selected) states[row.Id - 5001] = 3;
        return new(new([.. states], checked(now + seconds), coins), plan);
    }

    public GuildTrainingProjectionReceipt? PlanGuildTrainingProjection(bool clear = false) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character) return null;
        long epoch = SelectionEpoch;
        var dungeon = formalDungeon;
        Guid instance = dungeon?.Run.Snapshot.Identity.InstanceId ?? Guid.Empty;
        long now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        var active = !clear && Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement &&
            dungeon?.GuildTraining is { } training && training.Remaining(now) > 0 ? dungeon.GuildTraining : null;
        int remaining = active?.Remaining(now) ?? 0;
        if (guildTrainingProjection.Plan(epoch, remaining > 0) is not { } version) return null;
        bool Current() => owner.Execute(() => owner.IsCurrent && Ready && SelectionEpoch == epoch &&
            Selected?.Id == character.Id && guildTrainingProjection.IsCurrent(epoch, version) &&
            (formalDungeon?.Run.Snapshot.Identity.InstanceId ?? Guid.Empty) == instance &&
            (remaining == 0 || Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement &&
                ReferenceEquals(formalDungeon?.GuildTraining, active) &&
                active!.Remaining((timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()) > 0));
        return new GuildTrainingProjectionReceipt(active?.States ?? [], remaining, instance, active?.Deadline ?? 0, Current);
    });
}
