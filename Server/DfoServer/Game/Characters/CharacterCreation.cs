using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Characters;

internal static class CharacterCreation
{
    public static NewCharacter Prepare(string name, CharacterProfile profile, Location room, Location initialReturn,
        Func<byte, ushort, SkillLearningDefinition>? definition)
    {
        profile.Validate();
        var skills = profile.Skills.ToArray();
        var initialization = definition is null ? null : SkillLearning.Initialize(profile.Job, skills, definition);
        return new(name, room, initialReturn)
        {
            Job = profile.Job,
            Skills = skills,
            AcceptedStoryLevel = 1,
            ActiveMainQuests = [new(0, 3145, 1)],
            InitialLearning = initialization?.State ?? CharacterLearningState.Uninitialized,
            Inventory = InventoryRewardGrantService.CreateStarter(profile)
        };
    }
}
