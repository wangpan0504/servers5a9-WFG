using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

internal sealed record FatigueBatteryCatalog(ushort ChargingLimit, float EndLevelWeight, byte LevelConstant,
    byte MinimumLevel, ushort ActivationMinimum, ushort ChargingPerBuff, ushort BuffPerCharging,
    ImmutableArray<ushort> BuffLimits)
{
    public const string ResourcePath = "etc/(r)serverparameter.etc";

    public static FatigueBatteryCatalog Load(PvfArchive archive) =>
        Parse(new PvfTokenDocument(archive.ReadTokens(ResourcePath), archive.ResolveString).Sections);

    internal static FatigueBatteryCatalog Parse(ImmutableArray<PvfTokenSection> sections)
    {
        PvfTokenSection Field(string name)
        {
            var fields = sections.Where(section => section.Name == name).ToArray();
            if (fields is not [{ Children.Length: 0 } field] || field.Values.IsEmpty)
                throw new InvalidDataException("疲劳蓄电池资源缺失、重复或包含子块：" + name);
            return field;
        }
        int Integer(string name, int maximum)
        {
            var field = Field(name);
            if (field.Values is not [{ Type: 0 } token] || token.Value <= 0 || token.Value > maximum)
                throw new InvalidDataException("疲劳蓄电池整数无效：" + name);
            return token.Value;
        }
        var weight = Field("end level fatigue battery weight");
        if (weight.Values is not [{ Type: 2 } value]) throw new InvalidDataException("疲劳蓄电池满级权重无效");
        float factor = BitConverter.Int32BitsToSingle(value.Value);
        if (!float.IsFinite(factor) || factor < 0) throw new InvalidDataException("疲劳蓄电池满级权重非有限正数");
        var limits = Field("limit grownup buff cnt fatigue battery").Values;
        if (limits.Length < CharacterGrowthCatalog.MaximumSupportedLevel ||
            limits.Any(token => token.Type != 0 || token.Value is <= 0 or > ushort.MaxValue))
            throw new InvalidDataException("疲劳蓄电池等级上限表无效");
        return new((ushort)Integer("limit fatigue battery charging amount", short.MaxValue), factor,
            (byte)Integer("character level fatigue battery constance", byte.MaxValue),
            (byte)Integer("limit fatigue battery level", CharacterGrowthCatalog.MaximumSupportedLevel - 1),
            (ushort)Integer("min fatigue battery per buff active", short.MaxValue),
            (ushort)Integer("fatigue battery per grownup buff active", ushort.MaxValue),
            (ushort)Integer("grownup buff per fatigue battery active", ushort.MaxValue),
            limits.Select(token => (ushort)token.Value).ToImmutableArray());
    }

    public bool Eligible(int level) => level >= MinimumLevel && level < CharacterGrowthCatalog.MaximumSupportedLevel;
    public ushort BuffLimit(int level) => level > 0 && level <= BuffLimits.Length ? BuffLimits[level - 1] : (ushort)0;
}
