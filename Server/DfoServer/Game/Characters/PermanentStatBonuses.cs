using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Characters;

// 只保存道具永久来源，单位与 CMD30 / Player 一致为十倍定点；不保存派生属性总额。
internal sealed record PermanentStatBonuses(uint MaxHp = 0, uint MaxMp = 0, int Strength = 0, int Vitality = 0,
    int Intelligence = 0, int Spirit = 0, uint MoveSpeed = 0, int AllElementResistance = 0)
{
    public static PermanentStatBonuses Empty { get; } = new();

    public void Validate()
    {
        if (Strength is < 0 or > short.MaxValue || Vitality is < 0 or > short.MaxValue ||
            Intelligence is < 0 or > short.MaxValue || Spirit is < 0 or > short.MaxValue ||
            AllElementResistance is < 0 or > short.MaxValue)
            throw new InvalidDataException("永久属性来源超出非负定点范围");
    }

    public PermanentStatBonuses Add(PermanentGrowthKind kind, int amount)
    {
        if (amount <= 0) throw new InvalidDataException("永久属性增量必须为正");
        var next = kind switch
        {
            PermanentGrowthKind.MaxHp => this with { MaxHp = checked(MaxHp + (uint)amount) },
            PermanentGrowthKind.MaxMp => this with { MaxMp = checked(MaxMp + (uint)amount) },
            PermanentGrowthKind.Strength => this with { Strength = checked(Strength + amount) },
            PermanentGrowthKind.Vitality => this with { Vitality = checked(Vitality + amount) },
            PermanentGrowthKind.Intelligence => this with { Intelligence = checked(Intelligence + amount) },
            PermanentGrowthKind.Spirit => this with { Spirit = checked(Spirit + amount) },
            PermanentGrowthKind.MoveSpeed => this with { MoveSpeed = checked(MoveSpeed + (uint)amount) },
            PermanentGrowthKind.AllElementResistance => this with { AllElementResistance = checked(AllElementResistance + amount) },
            _ => throw new InvalidDataException("技能点不能计入属性来源")
        };
        next.Validate();
        return next;
    }

    public CharacterStats Apply(CharacterStats basis)
    {
        Validate();
        if (this == Empty) return basis;
        return basis with
        {
            MaxHp = checked(basis.MaxHp + MaxHp), MaxMp = checked(basis.MaxMp + MaxMp),
            PhysicalAttack = checked((short)(basis.PhysicalAttack + Strength)),
            PhysicalDefense = checked((short)(basis.PhysicalDefense + Vitality)),
            MagicalAttack = checked((short)(basis.MagicalAttack + Intelligence)),
            MagicalDefense = checked((short)(basis.MagicalDefense + Spirit)),
            MoveSpeed = checked(basis.MoveSpeed + MoveSpeed),
            FireResistance = checked((short)(basis.FireResistance + AllElementResistance)),
            WaterResistance = checked((short)(basis.WaterResistance + AllElementResistance)),
            DarkResistance = checked((short)(basis.DarkResistance + AllElementResistance)),
            LightResistance = checked((short)(basis.LightResistance + AllElementResistance))
        };
    }
}
