using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record SharedEffectReward(byte Index, int Weight, int BoosterId);
internal sealed record SharedEffectConsumableDefinition(int CoolTime, int CoolGroup, byte CooltimeMaintenance,
    ImmutableArray<SharedEffectReward> Outcomes)
{
    public static SharedEffectConsumableDefinition? Parse(SharedEffectConsumableFile file)
    {
        if (file.Errors.Count != 0 || file.SharedEffect != 1 ||
            file.Stackable.TypeDeclaration is not { Label: "[random reward item]", Parameters: [0] } ||
            file.CooltimeMaintenance is < 0 or > 7 || file.Stackable.HasCoolTime && file.Stackable.DeclaredCoolTime is null ||
            file.Stackable.DeclaredCoolTime is < 0 ||
            file.Outcomes.Count is 0 or > 256 || file.Outcomes.Any(row => row.Weight < 0) ||
            file.Outcomes.Sum(row => (long)row.Weight) == 0) return null;
        var outcomes = ImmutableArray.CreateBuilder<SharedEffectReward>(file.Outcomes.Count);
        for (int i = 0; i < file.Outcomes.Count; i++)
        {
            var row = file.Outcomes[i];
            // 原生219既按序号查图，又按首列匹配特效；不将稀疏编号静默当成向量下标。
            if (row.Index != i) return null;
            outcomes.Add(new((byte)i, row.Weight, row.RewardItemId));
        }
        return new(file.Stackable.DeclaredCoolTime ?? 0, file.CoolGroup, (byte)file.CooltimeMaintenance, outcomes.MoveToImmutable());
    }
}
