namespace DfoServer.GameWorld.Characters;

// 数值沿用当前 CHR 解析的十倍定点单位；包体宽度、排列和保留字段由 Builder 决定。
internal sealed record CharacterStats(
    uint MaxHp,
    uint MaxMp,
    short PhysicalAttack,
    short PhysicalDefense,
    short MagicalAttack,
    short MagicalDefense,
    short FireResistance,
    short WaterResistance,
    short DarkResistance,
    short LightResistance,
    uint InventoryLimit,
    ushort MpRecovery,
    uint MoveSpeed,
    ushort AttackSpeed,
    ushort CastSpeed,
    ushort HitRecovery,
    ushort JumpPower,
    uint Weight);
