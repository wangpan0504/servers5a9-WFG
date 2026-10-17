namespace DfoServer.Game.Inventory.Models;

internal readonly record struct AvatarSocket(ushort Type, int EmblemId)
{
    public static AvatarSocket Empty(ushort type) => new(type, type == 0 ? 0 : -1);
}

// Client 每件时装有五个独立孔位；使用值类型，规划快照之间不共享可变数组。
internal sealed record AvatarDetail
{
    public int ExpireTime { get; init; }
    public int ClearAvatarId { get; init; }
    public AvatarSocket Socket0 { get; init; }
    public AvatarSocket Socket1 { get; init; }
    public AvatarSocket Socket2 { get; init; }
    public AvatarSocket Socket3 { get; init; }
    public AvatarSocket Socket4 { get; init; }
    public ushort Color1 { get; init; }
    public ushort Color2 { get; init; }
    public void Validate()
    {
        if (ExpireTime < 0 || ClearAvatarId < 0 ||
            new[] { Socket0, Socket1, Socket2, Socket3, Socket4 }.Any(socket => socket.EmblemId < -1))
            throw new InvalidDataException("无效时装详情");
    }
}

// 宠物成长、饱食和改名由对应系统修改；容器只负责无损保存与同步。
internal sealed record CreatureDetail
{
    public int ExpireTime { get; init; }
    public string Name { get; init; } = "";
    public byte Satiety { get; init; } = 100;
    public byte Mode { get; init; }
    public byte Mode1Field0 { get; init; }
    public byte Mode1Field1 { get; init; }
    public int Experience { get; init; }
    public byte Level { get; init; } = 1;
    public byte TailFlag { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int SatietyFraction { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long FeedEffectExpiresAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int FeedSkillRecoveryRate { get; init; }
    public void Validate()
    {
        if (ExpireTime < 0 || Experience < 0 || Name is null || Name.Contains('\0') ||
            SatietyFraction is < 0 or >= 36000 || FeedEffectExpiresAt < 0)
            throw new InvalidDataException("无效宠物详情");
        try
        {
            if (DfoServer.Game.Characters.CharacterName.Encoding.GetByteCount(Name) > 30)
                throw new InvalidDataException("宠物名称超过客户端字节上限");
        }
        catch (System.Text.EncoderFallbackException error)
        {
            throw new InvalidDataException("宠物名称不能使用客户端代码页表示", error);
        }
    }
}
