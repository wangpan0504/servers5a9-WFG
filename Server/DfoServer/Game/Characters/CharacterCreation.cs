using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Characters;

internal sealed record CharacterCreationReceipt(Character Created, CharacterRosterSnapshot Roster);

internal static class CharacterCreation
{
    public static NewCharacter Prepare(string name, CharacterProfile profile, Location room, Location initialReturn,
        Func<byte, ushort, SkillLearningDefinition>? definition, TutorialFlow? tutorial = null)
    {
        profile.Validate();
        var skills = profile.Skills.ToArray();
        var bindingSkills = skills.ToArray();
        if (definition is not null && skills.Length > 1)
        {
            // 建角先排列绑定输入，Z技能直接占第一格；原技能列表和后续学习规则不变。
            var vacant = new HashSet<byte>();
            try
            {
                var quick = Enumerable.Range(0, skills.Length).Where(index =>
                    SkillSlotAllocator.Allocate(definition(profile.Job, skills[index].Id), vacant, initial: true) == 0).Take(2).ToArray();
                if (quick.Length == 2 && quick.All(index => skills[index].Id != 169))
                    (bindingSkills[quick[0]], bindingSkills[quick[1]]) = (bindingSkills[quick[1]], bindingSkills[quick[0]]);
            }
            catch (KeyNotFoundException)
            {
                // 缺失定义继续由原初始化入口返回失败，不生成部分绑定。
                bindingSkills = skills;
            }
        }
        var initialization = definition is null ? null : SkillLearning.Initialize(profile.Job, bindingSkills, definition);
        return new(name, room, initialReturn)
        {
            Job = profile.Job,
            Skills = skills,
            AcceptedStoryLevel = 1,
            Tutorial = tutorial is null ? null : new(tutorial.DungeonId),
            InitialLearning = initialization?.State ?? CharacterLearningState.Uninitialized,
            DungeonState = CharacterDungeonState.NewOrdinary,
            Inventory = InventoryRewardGrantService.CreateStarter(profile)
        };
    }
}
