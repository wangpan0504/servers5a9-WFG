using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.GameWorld.Characters;

// 技能编号和等级的不可变值；初始定义与角色存档共用，由角色用例管理学习状态。
internal sealed record CharacterSkill(ushort Id, byte Level);
internal sealed record CharacterGrowthOption(byte Value, string Label, IReadOnlyList<string> Awakenings);
internal sealed record CharacterGrowthOptions(string BaseName, IReadOnlyList<CharacterGrowthOption> GrowTypes);

internal sealed record CharacterProfile(byte Job, CharacterStats InitialStats, CharacterSkill[] Skills)
{
    public StarterEquipment[] Equipment { get; init; } = [];
    public ImmutableDictionary<byte, int> DefaultAvatarEquipment { get; init; } = ImmutableDictionary<byte, int>.Empty;
    public CharacterGrowthOptions GrowthOptions { get; init; } = new("", Array.Empty<CharacterGrowthOption>());
    public void Validate()
    {
        if (Job > 15 || Skills.Length > 255 || Skills.Any(skill => skill.Level == 0) ||
            Skills.Select(skill => skill.Id).Distinct().Count() != Skills.Length)
            throw new InvalidDataException("Invalid ordinary Client character profile");
    }
}
