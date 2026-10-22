using System.Collections.Immutable;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 718只读本账号名册；选角期与已就绪角色都可查询，不改变会话阶段。
    public ImmutableArray<(uint Id, string Name)> ReadAdventureCharacterList() => repository.ReadAdventureCharacterList(owner);
    public ImmutableArray<byte> ReadAdventureWeeklyConfig() => repository.ReadAdventureWeeklyConfig(owner);

    // 每周地下城配置是账号级排列，保存后不推进冒险团信息包，也不重建Actor。
    public AdventureWeeklyResult SaveAdventureWeeklyConfig(ImmutableArray<byte> slots) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } selected) return new AdventureWeeklyResult(false, 3);
        long generation = owner.Generation;
        repository.WriteAdventureWeeklyConfig(owner, slots);
        return new AdventureWeeklyResult(true, 0)
            { Generation = generation, Epoch = SelectionEpoch, Phase = Phase, Selected = selected };
    });

    public bool CanPublishAdventureWeekly(AdventureWeeklyResult result) => owner.Execute(() =>
        result.Accepted && Ready && result.Generation == owner.Generation && result.Epoch == SelectionEpoch &&
        result.Phase == Phase && ReferenceEquals(Selected, result.Selected));
}

internal sealed record AdventureWeeklyResult(bool Accepted, ushort Error)
{
    public long Generation { get; init; }
    public long Epoch { get; init; }
    public SessionPhase Phase { get; init; }
    public Character? Selected { get; init; }
}
