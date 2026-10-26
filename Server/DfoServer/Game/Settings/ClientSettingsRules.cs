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
        if (!source.IsDefault && !saved.CharacterGameOptions.IsDefault)
        {
            byte[] main = source.ToArray();
            foreach (int slot in CharacterSlots)
                Write(main, slot, Read(saved.CharacterGameOptions, slot));
            source = [.. main];
        }
        var character = saved.CharacterOptions;
        bool initializeCharacter = character.IsDefault;
        if (initializeCharacter)
        {
            // 原生角色选项初始化为未设置值，43槽另置零；光环48槽没有客户端缺省回退。
            bool nativeDefaults = source.IsDefault || Enumerable.Range(0, ClientSettingsService.GameOptionLength / 2)
                .All(slot => Read(source, slot) == 0x7fff);
            byte[] initial = Enumerable.Repeat((byte)255, ClientSettingsService.CharacterOptionLength).ToArray();
            Write(initial, 43, 0);
            Write(initial, 46, nativeDefaults ? (ushort)100 : Read(source, 68));
            Write(initial, 49, nativeDefaults ? (ushort)1 : Read(source, 13));
            character = [.. initial];
        }
        if (Read(character, 48) == ushort.MaxValue)
        {
            // 02614ACC直接安装此值，02607970仅接受1；未设置时默认显示，显式关闭0原样保留。
            byte[] copy = character.ToArray();
            Write(copy, 48, 1);
            character = [.. copy];
        }
        saved = saved with { CharacterOptions = character };
        // 合成角色选项不能反向改造主区，尤其不能破坏全7FFF的原生初始化标记。
        if (source.IsDefault || initializeCharacter)
            return saved with { GameOptions = source };
        var effective = ForCharacterSave(source, character);
        byte[] coordinated = character.ToArray();
        // 390会再写这两项，必须与本次173使用相同的角色值。
        Write(coordinated, 46, Read(effective, 68));
        Write(coordinated, 49, Read(effective, 13));
        return saved with { GameOptions = effective, CharacterOptions = [.. coordinated] };
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
