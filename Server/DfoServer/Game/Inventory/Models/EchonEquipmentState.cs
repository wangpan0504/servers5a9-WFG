using System.Text.Json.Serialization;

namespace DfoServer.Game.Inventory.Models;

internal readonly record struct EchonEquipmentState(byte First, byte Second, byte Reserved, byte SetIndex, byte Grade)
{
    public static EchonEquipmentState Read(ItemCore core) => new((byte)core.Extension114.Value,
        (byte)(core.Extension114.Value >> 8), (byte)(core.Extension114.Value >> 16),
        (byte)(core.Extension114.Value >> 24), core.Extension114.Flag);
    public ItemCore Apply(ItemCore core) => core with
    {
        Extension114 = new(Grade, First | (uint)Second << 8 | (uint)Reserved << 16 | (uint)SetIndex << 24)
    };
}

// 套装定义随完整实例保存，转移、邮件和仓库均不另建资产所有者。
internal sealed record EchonSet(byte Index, byte Parts, byte First, byte Second, byte Third, byte Fourth, byte Grade)
{
    [JsonIgnore] public byte[] Options => new[] { First, Second, Third, Fourth }.TakeWhile(value => value != 0).ToArray();
    public void Validate()
    {
        if (Index == 0 || Parts is < 3 or > 6 || Grade > 3 || First == 0 ||
            Second == 0 && (Third != 0 || Fourth != 0) || Third == 0 && Fourth != 0 ||
            Options.Distinct().Count() != Options.Length)
            throw new InvalidDataException("无效艾肯套装实例");
    }
}
internal sealed record EchonOptionRequest(short Slot, byte Position, byte Option);
internal sealed record EchonSetRequest(byte[] Options, short[] Slots);
