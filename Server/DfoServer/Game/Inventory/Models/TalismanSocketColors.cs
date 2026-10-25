namespace DfoServer.Game.Inventory.Models;

// 027EDDD0从原生核心+BC/+BD/+BE读取三个阵营编号，123B条目对应偏移114–116。
internal readonly record struct TalismanSocketColors(byte First, byte Second, byte Third)
{
    public byte this[int index] => index switch
    {
        0 => First,
        1 => Second,
        2 => Third,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public bool IsComplete => First != 0 && Second != 0 && Third != 0;

    public static TalismanSocketColors Read(ItemCore core) => new((byte)core.Extension114.Value,
        (byte)(core.Extension114.Value >> 8), (byte)(core.Extension114.Value >> 16));

    public ItemCore Apply(ItemCore core)
    {
        Validate();
        // 第四个字节及独立flag尚无孔色语义；改造仅替换已确认的三个字节。
        uint value = (core.Extension114.Value & 0xFF000000) | First | ((uint)Second << 8) | ((uint)Third << 16);
        return core with { Extension114 = core.Extension114 with { Value = value } };
    }

    public void Validate()
    {
        if (First > 5 || Second > 5 || Third > 5)
            throw new InvalidDataException("护石孔位阵营编号无效");
    }
}
