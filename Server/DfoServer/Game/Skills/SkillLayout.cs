using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Skills;

internal sealed record SkillSlotChangeCommand(byte Context, byte From, byte To, int ContextIndex, byte Mode);
internal sealed record SkillLayoutResult(SkillLearningFailure Failure, CharacterLearningState? State)
{
    public bool Success => Failure == SkillLearningFailure.None;
    public SkillLearningReceipt? Receipt { get; init; }
}

internal static class SkillLayout
{
    public static SkillLayoutResult Move(byte job, CharacterLearningState current, SkillSlotChangeCommand command,
        Func<byte, ushort, SkillLearningDefinition> definition)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(definition);
        if (command.Context > 1 || !OrdinarySlot(command.From) || !OrdinarySlot(command.To))
            return Fail(SkillLearningFailure.InvalidRequest);
        // 普通换槽的实收分支；额外上下文和其它模式尚不能套用普通交换。
        if (job > 15 || command.ContextIndex != -1 || command.Mode != 0 || !current.Initialized)
            return Fail(SkillLearningFailure.Unsupported);
        try { current.Validate(); }
        catch (InvalidDataException) { return Fail(SkillLearningFailure.InvalidRequest); }
        var tree = current.Trees.Single(value => value.Context == command.Context);
        LearnedSkill? from = tree.Skills.SingleOrDefault(skill => skill.Slot == command.From);
        LearnedSkill? to = tree.Skills.SingleOrDefault(skill => skill.Slot == command.To);
        if (from is null)
            return Fail(SkillLearningFailure.MissingSkill);
        foreach ((LearnedSkill? skill, byte slot) in new[] { (from, command.To), (to, command.From) })
        {
            if (skill is null)
                continue;
            SkillLearningDefinition resource;
            try { resource = definition(job, skill.Id); }
            catch (KeyNotFoundException) { return Fail(SkillLearningFailure.MissingSkill); }
            if (resource.Job != job || resource.SkillId != skill.Id || resource.HasComboDeclaration && job != 9 ||
                resource.Type is not (SkillType.Active or SkillType.Passive) || skill.Id is >= 200 and <= 211 ||
                resource.HasComboDeclaration && slot >= 6 ||
                (SkillSlotAllocator.QuickSlot(slot) && resource.Type != SkillType.Active))
                return Fail(SkillLearningFailure.Unsupported);
        }
        if (command.From == command.To)
            return new(SkillLearningFailure.None, current);
        var bindings = tree.Skills.Select(skill => skill.Slot == command.From ? skill with { Slot = command.To } :
            skill.Slot == command.To ? skill with { Slot = command.From } : skill).OrderBy(skill => skill.Slot).ToArray();
        var trees = current.Trees.Select(value => value.Context == command.Context
            ? value with { Skills = Array.AsReadOnly(bindings) }
            : value.Freeze()).ToArray();
        return new(SkillLearningFailure.None, current with { Trees = Array.AsReadOnly(trees) });
    }

    private static bool OrdinarySlot(byte slot) => slot < 204 && slot is not (>= 138 and < 150);
    private static SkillLayoutResult Fail(SkillLearningFailure failure) => new(failure, null);
}
