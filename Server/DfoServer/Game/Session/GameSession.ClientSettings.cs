using System.Collections.Immutable;
using DfoServer.Game.Settings;

namespace DfoServer.Game.Session;

internal sealed record ClientSettingsReceipt(ClientSettingsSnapshot Settings, long Generation, long SelectionEpoch, ushort CharacterId, byte VisibilityFlags);

internal sealed partial class GameSession
{
    public bool SaveGameOptions(ClientSettingsService settings, ImmutableArray<byte> options) => owner.Execute(() =>
    {
        bool changed = settings.SaveGameOptions(owner, options, Selected?.Id);
        if (Selected is not null)
            LoadClientSettings(settings);
        return changed;
    });

    public bool SaveCharacterOptions(ClientSettingsService settings, ImmutableArray<byte> options) => owner.Execute(() =>
    {
        var character = Selected ?? throw new InvalidDataException("保存角色设置时没有选中角色");
        return settings.SaveCharacterOptions(owner, character.Id, options);
    });

    public ClientSettingsReceipt LoadClientSettings(ClientSettingsService settings) => owner.Execute(() =>
    {
        var character = Selected ?? throw new InvalidOperationException("回放角色设置时没有选中角色");
        var restored = ClientSettingsRules.Project(settings.Load(owner, character.Id));
        byte flags = ClientSettingsRules.VisibilityFlags(restored.GameOptions);
        if (character.VisibilityFlags != flags)
            Selected = character with { VisibilityFlags = flags };
        return new ClientSettingsReceipt(restored, owner.Generation, SelectionEpoch, character.Id, flags);
    });

    public bool CanPublishClientSettings(ClientSettingsReceipt receipt) => owner.Execute(() =>
        owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch && Selected is { } character &&
        character.Id == receipt.CharacterId && character.VisibilityFlags == receipt.VisibilityFlags);
}
