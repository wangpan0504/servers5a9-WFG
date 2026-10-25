using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

// 专用建角与普通建角分开保存资源声明；缺失的分支、惩罚和物品不补默认值。
internal sealed record PvpPrivateEquipment(string Slot,int ItemId,int Flag);
internal sealed record PvpPrivateCharacterDefinition(byte Job, byte GrowType, string Path,
    ImmutableArray<PvpPrivateEquipment> Equipment, int? RecreatePenalty);

internal sealed class PvpPrivateCharacterCatalog
{
    readonly ImmutableDictionary<(byte Job, byte Grow), PvpPrivateCharacterDefinition> definitions;
    public int AccountLevelRequirement { get; }
    public IEnumerable<PvpPrivateCharacterDefinition> Definitions => definitions.Values;

    PvpPrivateCharacterCatalog(int accountLevel, ImmutableDictionary<(byte, byte), PvpPrivateCharacterDefinition> values)
    {
        AccountLevelRequirement = accountLevel;
        definitions = values;
    }

    public bool TryGet(byte job, byte grow, out PvpPrivateCharacterDefinition definition) =>
        definitions.TryGetValue((job,grow), out definition!);

    public static PvpPrivateCharacterCatalog Load(PvfArchive pvf)
    {
        var parameters = new PvfTokenDocument(pvf.ReadTokens("etc/pvpparameter.etc"), pvf.ResolveString);
        var fields = parameters.Sections.Where(section => section.Name == "check level create private charac").ToArray();
        if (fields is not [{ Children.Length: 0, Values.Length: 1 } field] || field.Values[0].Type != 0 ||
            field.Values[0].Value is < 1 or > 95)
            throw new InvalidDataException("决斗专用角色建角等级检查资源缺失或非法");
        var values = ImmutableDictionary.CreateBuilder<(byte,byte),PvpPrivateCharacterDefinition>();
        foreach (var job in CharacterCatalog.ReadJobList(pvf))
        {
            string path = "character/" + job.Value;
            foreach (var row in PvpPrivateCharacterFile.Parse(pvf.ReadTokens(path),pvf.ResolveString))
            {
                if (row.ResourceGrowType is < 1 or > 6 || row.Equipment.IsEmpty) continue;
                byte grow = checked((byte)(row.ResourceGrowType - 1));
                // 与现有CHR成长目录一致：资源growtype 1对应基础分支0，不代表一觉。
                var definition = new PvpPrivateCharacterDefinition(checked((byte)job.Key),grow,path,row.Equipment.Select(item=>new PvpPrivateEquipment(item.Slot,item.ItemId,item.Flag)).ToImmutableArray(),row.RecreatePenalty);
                if (!values.TryAdd((definition.Job,grow),definition))
                    throw new InvalidDataException("决斗专用角色创建分支重复");
            }
        }
        return new(field.Values[0].Value,values.ToImmutable());
    }
}
