using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public SkillLearningFailure InitializeLearning(SkillLearningCatalog catalog) => InitializeLearning(character =>
    {
        var result = SkillLearning.Initialize(character, catalog.Get);
        if (!result.Success) return result;
        try
        {
            int contractLevels = catalog.ActiveExpertContractLevels(result.State!.ExpertContractExpiresAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var state = SkillPointLedger.Synchronize(character.Job, character.GrowType, character.Level, result.State!, catalog, contractLevels);
            return result with { State = state, TotalSp = state.BonusSp is int bonus ? checked(catalog.TotalSp(character.Level) + bonus) : null,
                TotalTp = state.BonusTp is int tpBonus ? checked(catalog.TotalTp(character.Level) + tpBonus) : null };
        }
        catch (NotSupportedException) { return result; }
        catch (KeyNotFoundException) { return result; }
    });

    internal SkillLearningFailure InitializeLearning(Func<byte, ushort, SkillLearningDefinition> definition) =>
        InitializeLearning(character => SkillLearning.Initialize(character, definition));

    private SkillLearningFailure InitializeLearning(Func<Character, SkillLearningResult> plan)
    {
        var captured = owner.Execute(() => (Character: Selected ?? throw new InvalidOperationException("No selected character"),
            SelectionEpoch, Ready, Phase));
        var result = plan(captured.Character);
        return owner.Execute(() =>
        {
            // 初始化发生在入城投影前，不能借用只接受 Town 的选角检查。
            if (pendingRental is not null || HasPendingTownQuestCommit || SelectionEpoch != captured.SelectionEpoch || !ReferenceEquals(Selected, captured.Character) ||
                Ready != captured.Ready || Phase != captured.Phase)
                throw new InvalidOperationException("Stale skill initialization state");
            if (result.Success && !ReferenceEquals(result.State, captured.Character.Learning))
                Selected = repository.CommitLearning(owner, captured.Character, result.State!, result.TotalSp, result.TotalTp);
            return result.Failure;
        });
    }

    public SkillLearningResult LearnSkills(SkillLearningCommand request, SkillLearningCatalog catalog) => ApplySkillLearning(character =>
        SkillLearning.Plan(character.Job, character.GrowType, character.Level, character.Learning, request, catalog));

    public SkillLearningResult CustomizeSkills(IReadOnlyList<SkillCommandChange> changes, bool reset, SkillLearningCatalog catalog) =>
        ApplySkillLearning(character => SkillCustomization.Commands(character.Job, character.Learning, changes, reset, catalog));

    public SkillLearningResult CustomizeCombo(byte context, IReadOnlyList<SkillCombo> combos, SkillLearningCatalog catalog) =>
        ApplySkillLearning(character => SkillCustomization.Combo(character.Job, character.Learning, context, combos, catalog));

    public SkillLearningResult ClearComboQuickSlots(byte context, SkillLearningCatalog catalog) =>
        ApplySkillLearning(character => SkillCustomization.ClearComboQuickSlots(character.Job, character.Learning, context, catalog));

    internal SkillLearningResult LearnSkills(SkillLearningCommand request, Func<byte, ushort, SkillLearningDefinition> definition) => ApplySkillLearning(character =>
        SkillLearning.Plan(character.Job, character.GrowType, character.Level, character.Learning, request, definition));

    public SkillLearningResult ResetSkills(byte context, byte mode, SkillLearningCatalog catalog) => ApplySkillLearning(character =>
        mode > 1 ? new(SkillLearningFailure.Unsupported, null, Array.Empty<LearnedSkill>()) :
        SkillPointLedger.Reset(character.Job, character.GrowType, character.Level, character.Learning, context, catalog,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private SkillLearningResult ApplySkillLearning(Func<Character, SkillLearningResult> plan)
    {
        var captured = owner.Execute(() => (Character: Selected ?? throw new InvalidOperationException("No selected character"),
            SelectionEpoch, Phase, Allowed: pendingRental is null && !HasPendingTownQuestCommit && Ready && !HasClassExperienceContext));
        if (!captured.Allowed)
            return new(SkillLearningFailure.Unsupported, null, Array.AsReadOnly(Array.Empty<LearnedSkill>()));

        var character = captured.Character;
        SkillLearningResult result;
        try { result = plan(character); }
        catch (NotSupportedException) { return new(SkillLearningFailure.Unsupported, null, Array.Empty<LearnedSkill>()); }
        catch (KeyNotFoundException) { return new(SkillLearningFailure.Unsupported, null, Array.Empty<LearnedSkill>()); }
        return owner.Execute(() =>
        {
            if (pendingRental is not null || HasPendingTownQuestCommit || !Ready || HasClassExperienceContext || Phase != captured.Phase || SelectionEpoch != captured.SelectionEpoch || !ReferenceEquals(Selected, character))
                throw new InvalidOperationException("Stale skill learning state");
            if (!result.Success)
                return result;
            if (!ReferenceEquals(result.State, character.Learning))
                Selected = repository.CommitLearning(owner, character, result.State!, result.TotalSp, result.TotalTp);
            return result with { State = Selected!.Learning, Receipt = new(Selected, owner.Generation, SelectionEpoch) { Phase = Phase } };
        });
    }

    public bool CanPublishLearning(SkillLearningReceipt receipt) => owner.Execute(() =>
        Ready && receipt.Generation == owner.Generation && Phase == receipt.Phase &&
        SelectionEpoch == receipt.SelectionEpoch && Selected?.Id == receipt.Character.Id &&
        ReferenceEquals(Selected, receipt.Character));

    public SkillLayoutResult MoveSkillSlot(SkillSlotChangeCommand command, SkillLearningCatalog catalog) => MoveSkillSlot(command, catalog.Get);

    internal SkillLayoutResult MoveSkillSlot(SkillSlotChangeCommand command, Func<byte, ushort, SkillLearningDefinition> definition)
    {
        var captured = owner.Execute(() => (Character: Selected ?? throw new InvalidOperationException("No selected character"),
            SelectionEpoch, Phase, Allowed: pendingRental is null && !HasPendingTownQuestCommit && Ready && !HasClassExperienceContext));
        if (!captured.Allowed)
            return new(SkillLearningFailure.Unsupported, null);
        var result = SkillLayout.Move(captured.Character.Job, captured.Character.Learning, command, definition);
        return owner.Execute(() =>
        {
            if (pendingRental is not null || HasPendingTownQuestCommit || !Ready || HasClassExperienceContext || Phase != captured.Phase || SelectionEpoch != captured.SelectionEpoch || !ReferenceEquals(Selected, captured.Character))
                throw new InvalidOperationException("Stale skill layout state");
            if (!result.Success)
                return result;
            // 同槽请求只确认原事实；没有布局变化不申请writer或递增revision。
            if (!ReferenceEquals(result.State, captured.Character.Learning))
                Selected = repository.CommitSkillLayout(owner, captured.Character, result.State!);
            return result with { State = Selected!.Learning, Receipt = new(Selected, owner.Generation, SelectionEpoch) { Phase = Phase } };
        });
    }
}
