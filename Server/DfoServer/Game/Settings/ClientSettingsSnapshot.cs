using System.Collections.Immutable;

namespace DfoServer.Game.Settings;

internal sealed record ClientSettingsSnapshot(ImmutableArray<byte> GameOptions, ImmutableArray<byte> CharacterOptions,
    ImmutableArray<byte> CharacterGameOptions = default)
{
    public bool IsDefault => GameOptions.IsDefault && CharacterOptions.IsDefault && CharacterGameOptions.IsDefault;
}
