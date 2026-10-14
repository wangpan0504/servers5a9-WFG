namespace DfoServer.Game.Inventory.Models;

internal sealed record CubeContractState(byte Selected, sbyte CubeType)
{
    public static CubeContractState Empty { get; } = new(0, -1);
    public void Validate()
    {
        if (Selected > 1 || CubeType is < -1 or > 5)
            throw new InvalidDataException("晶体契约选择无效");
    }
}
