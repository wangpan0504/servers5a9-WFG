using System.Collections.Immutable;

namespace DfoServer.Game.Settings;

internal readonly record struct ChatEmoticonMacro(ushort Id, ImmutableArray<byte> Text);

internal sealed record ClientSettingsSnapshot(ImmutableArray<byte> GameOptions, ImmutableArray<byte> CharacterOptions,
    ImmutableArray<byte> CharacterGameOptions = default, ImmutableArray<byte> Hotkeys = default,
    ImmutableArray<byte> QuickChat0 = default, ImmutableArray<byte> QuickChat1 = default,
    ImmutableArray<ChatEmoticonMacro> ChatEmoticons = default, ImmutableArray<byte> RecentEmoticons = default,
    ImmutableArray<byte> QuestNotify = default)
{
    public bool IsDefault => GameOptions.IsDefault && CharacterOptions.IsDefault && CharacterGameOptions.IsDefault && Hotkeys.IsDefault;
}
