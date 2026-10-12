using System.Buffers.Binary;
using System.Collections.Immutable;

namespace DfoServer.Game.Settings;

internal static class ClientSettingsRules
{
    static readonly int[] CharacterSlots = [1, 13, 55, 74, 22, 68, 89, 94];

    public static ImmutableArray<byte> ForCharacterSave(ImmutableArray<byte> main, ImmutableArray<byte> character)
    {
        if (character.IsDefault)
            return main;
        byte[] copy = main.ToArray();
        // 角色选项先于主设置保存；这两项以客户端实际角色值为准，避免写回旧账号值。
        ushort alpha = Read(character, 46), illustration = Read(character, 49);
        if (alpha <= 100)
            Write(copy, 68, alpha);
        if (illustration <= 1)
            Write(copy, 13, illustration);
        return [.. copy];
    }

    public static ClientSettingsSnapshot Project(ClientSettingsSnapshot saved)
    {
        var source = saved.GameOptions.IsDefault ? saved.CharacterGameOptions : saved.GameOptions;
        if (source.IsDefault)
            return saved;
        byte[] main = source.ToArray();
        if (!saved.CharacterGameOptions.IsDefault)
            foreach (int slot in CharacterSlots)
                Write(main, slot, Read(saved.CharacterGameOptions, slot));
        var effective = ForCharacterSave([.. main], saved.CharacterOptions);
        var character = saved.CharacterOptions;
        if (!character.IsDefault)
        {
            byte[] copy = character.ToArray();
            // 390会再写这两项，必须与本次173使用相同的角色值。
            Write(copy, 46, Read(effective, 68));
            Write(copy, 49, Read(effective, 13));
            character = [.. copy];
        }
        return saved with { GameOptions = effective, CharacterOptions = character };
    }

    public static byte VisibilityFlags(ImmutableArray<byte> main)
    {
        if (main.IsDefault)
            return 6;
        byte flags = 6;
        if (Read(main, 1) == 0)
            flags &= unchecked((byte)~2);
        if (Read(main, 55) == 0)
            flags |= 8;
        if (Read(main, 74) == 0)
            flags |= 16;
        return flags;
    }

    static ushort Read(ImmutableArray<byte> values, int slot) => BinaryPrimitives.ReadUInt16LittleEndian(values.AsSpan().Slice(slot * 2, 2));
    static void Write(Span<byte> values, int slot, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(values.Slice(slot * 2, 2), value);
}
