using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public SkillLearningFailure InitializeLearning(SkillLearningCatalog catalog) => InitializeLearning(catalog.Get);

    internal SkillLearningFailure InitializeLearning(Func<byte, ushort, SkillLearningDefinition> definition)
    {
        var captured = owner.Execute(() => (Character: Selected ?? throw new InvalidOperationException("No selected character"),
            SelectionEpoch, Ready, Phase));
        var result = SkillLearning.Initialize(captured.Character, definition);
        return owner.Execute(() =>
        {
            // 初始化发生在入城投影前，不能借用只接受 Town 的选角检查。
            if (SelectionEpoch != captured.SelectionEpoch || !ReferenceEquals(Selected, captured.Character) ||
                Ready != captured.Ready || Phase != captured.Phase)
                throw new InvalidOperationException("Stale skill initialization state");
            if (result.Success && !ReferenceEquals(result.State, captured.Character.Learning))
                Selected = repository.CommitLearning(owner, captured.Character, result.State!);
            return result.Failure;
        });
    }

    public SkillLearningResult LearnSkills(SkillLearningCommand request, SkillLearningCatalog catalog) => LearnSkills(request, catalog.Get);

    internal SkillLearningResult LearnSkills(SkillLearningCommand request, Func<byte, ushort, SkillLearningDefinition> definition)
    {
        var captured = owner.Execute(() => (Character: Selected ?? throw new InvalidOperationException("No selected character"),
            SelectionEpoch, Allowed: Ready && Phase == SessionPhase.Town));
        if (!captured.Allowed)
            return new(SkillLearningFailure.Unsupported, null, Array.AsReadOnly(Array.Empty<LearnedSkill>()));

        var character = captured.Character;
        var result = SkillLearning.Plan(character.Job, character.GrowType, character.Level, character.Learning, request, definition);
        return owner.Execute(() =>
        {
            if (!Ready || !MatchesSelection(captured.SelectionEpoch, character.Id) || !ReferenceEquals(Selected, character))
                throw new InvalidOperationException("Stale skill learning state");
            if (!result.Success)
                return result;
            Selected = repository.CommitLearning(owner, character, result.State!);
            return result with { State = Selected.Learning, Receipt = new(Selected, owner.Generation, SelectionEpoch) };
        });
    }

    public bool CanPublishLearning(SkillLearningReceipt receipt) => owner.Execute(() =>
        Ready && receipt.Generation == owner.Generation && MatchesSelection(receipt.SelectionEpoch, receipt.Character.Id) &&
        ReferenceEquals(Selected, receipt.Character));
}
