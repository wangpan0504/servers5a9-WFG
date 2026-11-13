using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Characters;

// 统一合成Player的CHR基础与永久来源；装备和技能仍由完整Core/技能树参与客户端派生。
internal sealed class CharacterStatComputer(CharacterCatalog characters)
{
    public CharacterStats Compute(Character character)
    {
        // 创建用例明确拥有副本附属状态与完整两树；存量无owner的角色维持原投影。
        if (character.Job > 15 || character.Level is < 1 or > CharacterGrowthCatalog.MaximumSupportedLevel ||
            character.DungeonState is null || !character.Learning.Initialized)
            return character.PermanentStats.Apply(characters.Get(character.Job).InitialStats);
        character.DungeonState.Validate();
        character.Learning.Validate();
        return character.PermanentStats.Apply(ComputeLevelBaseStats(character.Job, character.GrowType, character.Level));
    }

    public void ValidatePermanentBonuses(byte job, PermanentStatBonuses bonuses)
    {
        if (bonuses == PermanentStatBonuses.Empty) return;
        // 正常 CHR 在18级分段，chn_add_stat.etc 在 MaxLevel 封顶；端点覆盖各分段的极值及未来转职。
        int additionalCap = Math.Clamp(characters.AdditionalStats.MaxLevel, 1, CharacterGrowthCatalog.MaximumSupportedLevel);
        foreach (var growth in characters.Get(job).GrowthOptions.GrowTypes)
            foreach (byte level in new byte[] { 1, 18, (byte)additionalCap, CharacterGrowthCatalog.MaximumSupportedLevel }.Distinct())
                bonuses.Apply(ComputeLevelBaseStats(job, growth.Value, level));
    }

    public CharacterStats ComputeLevelBaseStats(byte job, byte growType, byte level)
    {
        var option = characters.Get(job).GrowthOptions.GrowTypes.SingleOrDefault(option => option.Value == (growType & 0x0f));
        if (option is null || (growType >> 4) > option.Awakenings.Count)
            throw new InvalidDataException("角色成长编码与CHR职业目录不一致");
        return ComputeLevelBaseStats(characters.GetAttributeDefinition(job), growType, level, characters.AdditionalStats);
    }

    internal static CharacterStats ComputeLevelBaseStats(CharacterAttributeDefinition definition, byte growType, byte level,
        CharacterAdditionalStatDefinition? additional = null)
    {
        if (level is < 1 or > CharacterGrowthCatalog.MaximumSupportedLevel)
            throw new ArgumentOutOfRangeException(nameof(level));
        int firstGrow = growType & 0x0f;
        if (firstGrow > 5 || (growType >> 4) > 2)
            throw new ArgumentOutOfRangeException(nameof(growType));
        if (additional is not null && additional.MaxLevel is < 1 or > CharacterGrowthCatalog.MaximumSupportedLevel)
            throw new ArgumentOutOfRangeException(nameof(additional));
        var initial = definition.Blocks.Single(block => block.ResourceGrowType == 0 && block.Awakening == 0);
        var ordinary = definition.Blocks.Single(block => block.ResourceGrowType == 1 && block.Awakening == 0);
        var growth = definition.Blocks.Single(block => block.ResourceGrowType == firstGrow + 1 && block.Awakening == 0);
        // 02B53A83..02B53C56：先将CHR各项转换为十倍整数，再整型乘加。
        // 匹配客户端正常模式用未转职块至18级，之后用firstGrow；觉醒不另套旧版50级分段。
        // 040BD87E..040BD976：character/chn_add_stat.etc 提供全职业额外初始与1..MaxLevel成长属性。
        // awakening资源声明仍独立保留；这里不包含永久、装备或技能修正。
        return new(
            checked((uint)Value(CharacterAttributeField.MaxHp)), checked((uint)Value(CharacterAttributeField.MaxMp)),
            checked((short)Value(CharacterAttributeField.PhysicalAttack)), checked((short)Value(CharacterAttributeField.PhysicalDefense)),
            checked((short)Value(CharacterAttributeField.MagicalAttack)), checked((short)Value(CharacterAttributeField.MagicalDefense)),
            checked((short)Value(CharacterAttributeField.FireResistance)), checked((short)Value(CharacterAttributeField.WaterResistance)),
            checked((short)Value(CharacterAttributeField.DarkResistance)), checked((short)Value(CharacterAttributeField.LightResistance)),
            checked((uint)Value(CharacterAttributeField.InventoryLimit)), checked((ushort)Value(CharacterAttributeField.MpRecovery)),
            checked((uint)Value(CharacterAttributeField.MoveSpeed)), checked((ushort)Value(CharacterAttributeField.AttackSpeed)),
            checked((ushort)Value(CharacterAttributeField.CastSpeed)), checked((ushort)Value(CharacterAttributeField.HitRecovery)),
            checked((ushort)Value(CharacterAttributeField.JumpPower)), checked((uint)Value(CharacterAttributeField.Weight)));

        int Value(CharacterAttributeField field)
        {
            // 040AF0A0初始化全部DTO分量为0；只有真实缺声明才沿用资源默认。
            int start = initial.Fields.TryGetValue(field, out float startValue) ? ToNativeFixed(startValue) : 0;
            int ordinaryIncrement = ordinary.Fields.TryGetValue(field, out float ordinaryValue) ? ToNativeFixed(ordinaryValue) : 0;
            int increment = growth.Fields.TryGetValue(field, out float growthValue) ? ToNativeFixed(growthValue) : 0;
            int value = checked(start + ordinaryIncrement * (Math.Min((int)level, 18) - 1) + increment * Math.Max(level - 18, 0));
            if (additional is null)
                return value;
            int additionalStart = additional.InitialFields.TryGetValue(field, out float addStartValue) ? ToNativeFixed(addStartValue) : 0;
            int additionalIncrement = additional.GrowthFields.TryGetValue(field, out float addGrowthValue) ? ToNativeFixed(addGrowthValue) : 0;
            return checked(value + additionalStart + additionalIncrement * (Math.Min((int)level, additional.MaxLevel) - 1));
        }
    }

    private static int ToNativeFixed(float value)
    {
        // 040BA025与01606490：float32乘10、按符号±0.5，然后向零截断。
        float scaled = value * 10f;
        float rounded = scaled >= 0 ? scaled + 0.5f : scaled - 0.5f;
        if (!float.IsFinite(rounded) || rounded < int.MinValue || (double)rounded > int.MaxValue)
            throw new InvalidDataException("角色属性超出原生定点转换范围");
        return checked((int)rounded);
    }
}
