namespace DfoServer.Game.Party;

internal sealed record PartySettings(byte TitleCode, string Title, byte Capacity, uint Dungeon, byte Difficulty,
    ushort Argument, byte Flag, byte Kind, ushort KindArgument, byte? Option)
{
    public static PartySettings Default { get; } = new(0, "队伍", 4, 0, 0, 0, 0, 0, 0, 7);
    public bool DirectoryVisible => Kind != 5 || KindArgument != 2;
}
