using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Strongway2019;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed record Strongway2019RewardPreparation(Character Character, long Generation, long SelectionEpoch,
    SinglePlayerDungeonRun Run, DungeonRunIdentity Identity, DungeonClearFact? Fact,
    Strongway2019Admission Admission, Strongway2019Competition? Competition, Strongway2019Terminal? Terminal, long Created);
internal sealed record Strongway2019RewardReceipt(Strongway2019RewardPreparation Preparation, Strongway2019RewardCommit Commit);

internal sealed partial class GameSession
{
    public Strongway2019RewardPreparation? PrepareStrongway2019Reward() => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || formalDungeon is not { } dungeon ||
            !Strongway2019RewardEligible(dungeon, character, out var competition, out var terminal)) return null;
        if (dungeon.PendingStrongway2019Reward is { } pending)
            return CurrentStrongway2019Reward(pending) ? pending : null;
        return dungeon.PendingStrongway2019Reward = new(character, owner.Generation, SelectionEpoch,
            dungeon.Run, dungeon.Run.Scene.Identity, dungeon.Run.ClearFact, dungeon.Strongway2019Admission!, competition, terminal,
            terminal?.Created ?? (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds());
    });
    public Strongway2019RewardReceipt? CommitStrongway2019Reward(Strongway2019RewardPreparation expected,
        ItemCatalog items, IStrongway2019RewardStore store)
    {
        var captured = owner.Execute(() => CurrentStrongway2019Reward(expected)
            ? (Valid: true, Plan: formalDungeon!.Strongway2019RewardPlan, Receipt: formalDungeon.CommittedStrongway2019Reward)
            : (Valid: false, Plan: (Strongway2019RewardPlan?)null, Receipt: (Strongway2019RewardReceipt?)null));
        if (!captured.Valid) return null;
        if (captured.Receipt is not null) return captured.Receipt;
        var plan = captured.Plan;
        if (plan is null)
        {
            int id = expected.Admission.Mode switch
            {
                Strongway2019Mode.Simple => 490012735,
                Strongway2019Mode.Hero => 490012736,
                Strongway2019Mode.Matching => 490012737,
                _ => throw new InvalidDataException("首期奖励模式无效")
            };
            var template = items.Get(id);
            if (InventoryRewardGrantService.GrantDefinitionError(template, new(id, 1)) is not null)
                throw new InvalidDataException("首期奖励资源不支持完整附件创建");
            var core = InventoryRewardGrantService.Create(template, 1, DateTimeOffset.FromUnixTimeSeconds(expected.Created));
            var attachment = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 9), core);
            plan = new(expected.Character, expected.Run, expected.Fact, attachment, expected.Created)
            { Identity = expected.Identity, Competition = expected.Competition, Terminal = expected.Terminal };
        }
        return owner.Execute(() =>
        {
            if (!CurrentStrongway2019Reward(expected)) return null;
            var dungeon = formalDungeon!;
            if (dungeon.CommittedStrongway2019Reward is { } committed) return committed;
            // 在存储调用前保存原计划；异常不重算附件、日期或终局证明。
            dungeon.Strongway2019RewardPlan ??= plan;
            var result = store.CommitStrongway2019Reward(owner, dungeon.Strongway2019RewardPlan);
            return dungeon.CommittedStrongway2019Reward = new(expected, result);
        });
    }

    internal (DfoServer.Game.Accounts.AccountOwners.Lease Owner, Strongway2019RewardPlan Plan)? PrepareStrongway2019Completion(ItemCatalog items)
    {
        if (PrepareStrongway2019Reward() is not { } expected) return null;
        var captured = owner.Execute(() => CurrentStrongway2019Reward(expected)
            ? (Valid: true, Plan: formalDungeon!.Strongway2019RewardPlan, Receipt: formalDungeon.CommittedStrongway2019Reward)
            : (Valid: false, Plan: (Strongway2019RewardPlan?)null, Receipt: (Strongway2019RewardReceipt?)null));
        if (!captured.Valid) return null;
        var plan = captured.Plan;
        if (plan is null)
        {
            int id = expected.Admission.Mode switch
            {
                Strongway2019Mode.Simple => 490012735,
                Strongway2019Mode.Hero => 490012736,
                Strongway2019Mode.Matching => 490012737,
                _ => throw new InvalidDataException("首期奖励模式无效")
            };
            var template = items.Get(id);
            if (InventoryRewardGrantService.GrantDefinitionError(template, new(id, 1)) is not null)
                throw new InvalidDataException("首期奖励资源不支持完整附件创建");
            var core = InventoryRewardGrantService.Create(template, 1, DateTimeOffset.FromUnixTimeSeconds(expected.Created));
            var attachment = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 9), core);
            plan = new(expected.Character, expected.Run, expected.Fact, attachment, expected.Created)
            { Identity = expected.Identity, Competition = expected.Competition, Terminal = expected.Terminal };
        }
        return owner.Execute<(DfoServer.Game.Accounts.AccountOwners.Lease Owner, Strongway2019RewardPlan Plan)?>(() =>
        {
            if (!CurrentStrongway2019Reward(expected)) return null;
            formalDungeon!.Strongway2019RewardPlan ??= plan;
            return (owner, formalDungeon.Strongway2019RewardPlan);
        });
    }

    internal Func<ushort, Task<bool>>? Strongway2019RecoveryArrived { get; set; }

    internal Strongway2019Recovery RecoverStrongway2019Completion(IStrongway2019CompletionStore store) =>
        owner.Execute(() => Ready && Selected is { } character ? store.RecoverStrongway2019Completion(owner, character.Id) : new(false, []));

    internal DfoServer.Game.Accounts.AccountOwners.Lease? CaptureStrongway2019CompletionReporter()
    {
        try { return owner.Execute(() => owner); }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }

    public bool CanPublishStrongway2019Reward(Strongway2019RewardReceipt receipt) => owner.Execute(() =>
        CurrentStrongway2019Reward(receipt.Preparation) &&
        ReferenceEquals(formalDungeon!.CommittedStrongway2019Reward, receipt));

    bool CurrentStrongway2019Reward(Strongway2019RewardPreparation expected) =>
        Ready && owner.Generation == expected.Generation && SelectionEpoch == expected.SelectionEpoch &&
        ReferenceEquals(Selected, expected.Character) && formalDungeon is { } dungeon &&
        ReferenceEquals(dungeon.PendingStrongway2019Reward, expected) && ReferenceEquals(dungeon.Run, expected.Run) &&
        dungeon.Run.Scene.Identity == expected.Identity && ReferenceEquals(dungeon.Run.ClearFact, expected.Fact) &&
        ReferenceEquals(dungeon.Strongway2019Admission, expected.Admission) &&
        Strongway2019RewardEligible(dungeon, expected.Character, out var competition, out var terminal) &&
        ReferenceEquals(competition, expected.Competition) && ReferenceEquals(terminal, expected.Terminal);

    bool Strongway2019RewardEligible(FormalDungeon dungeon, Character character,
        out Strongway2019Competition? competition, out Strongway2019Terminal? terminal)
    {
        competition = null; terminal = null;
        var run = dungeon.Run;
        bool loadingExit = Phase == SessionPhase.Loading && run.Phase == SinglePlayerDungeonPhase.Loading &&
            dungeon.Strongway2019Admission?.Mode == Strongway2019Mode.Matching &&
            CurrentStrongway2019Match?.Competition.Snapshot.Terminal?.Reason == Strongway2019FinishReason.Exit;
        if (dungeon.Strongway2019Admission is not { } admission || admission.Character.Id != character.Id ||
            admission.DungeonId != run.Entry.DungeonId || run.Entry.CharacterId != character.Id || run.HasPendingDeath ||
            run.Scene.Identity.CharacterId != character.Id || Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement) && !loadingExit) return false;
        if (admission.Mode == Strongway2019Mode.Matching)
        {
            if (admission.DungeonId != 5360 || CurrentStrongway2019Match is not { } match) return false;
            competition = match.Competition;
            terminal = competition.Snapshot.Terminal;
            var member = competition.Participants.SingleOrDefault(row => ReferenceEquals(row.Run, run));
            return member is not null && member.Identity.CharacterId == character.Id &&
                member.Identity.InstanceId == run.Scene.Identity.InstanceId && member.Identity.TaskAttempt == run.Scene.Identity.TaskAttempt &&
                terminal is { Reason: Strongway2019FinishReason.Clear or Strongway2019FinishReason.Exit } &&
                terminal.WinnerSide == member.Side && terminal.RewardParticipants.Any(row => ReferenceEquals(row, member)) &&
                (run.Phase is SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement ||
                    terminal.Reason == Strongway2019FinishReason.Exit && loadingExit);
        }
        return (admission.Mode == Strongway2019Mode.Simple && admission.DungeonId == 5359 ||
            admission.Mode == Strongway2019Mode.Hero && admission.DungeonId == 5363) &&
            Phase == SessionPhase.Settlement && run.Phase == SinglePlayerDungeonPhase.Settlement &&
            run.ClearFact is { } fact && fact.Identity == run.Scene.Identity && fact.DungeonId == admission.DungeonId &&
            fact.ClearMap is { } map && run.Entry.IsTerminalMap(map) && fact.ConfirmedDeathEvents.Count > 0;
    }
}
