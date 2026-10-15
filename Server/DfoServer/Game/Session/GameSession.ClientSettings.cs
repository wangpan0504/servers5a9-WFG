using System.Collections.Immutable;
using DfoServer.Game.Settings;

namespace DfoServer.Game.Session;

internal sealed record ClientSettingsReceipt(ClientSettingsSnapshot Settings, long Generation, long SelectionEpoch, ushort CharacterId, byte VisibilityFlags, byte HotkeyProfile);

internal sealed partial class GameSession
{
    public (DfoServer.Game.Characters.Character? Character, ushort Error, bool Changed) ChangeEmotion(ClientSettingsService settings, ushort emotion) => owner.Execute(() =>
    {
        if (pendingRental is not null || HasPendingTownQuestCommit || Selected is not { } character || !Ready)
            return ((DfoServer.Game.Characters.Character?)null, (ushort)3, false);
        bool? changed = settings.SaveEmotion(owner, character.Id, emotion);
        if (changed is null) return ((DfoServer.Game.Characters.Character?)null, (ushort)1, false);
        bool visibleChanged = character.Emotion != emotion;
        if (visibleChanged) Selected = character with { Emotion = emotion };
        return (Selected, (ushort)0, changed.Value || visibleChanged);
    });

    public bool SaveHotkeys(ClientSettingsService settings, ImmutableArray<byte> text) => owner.Execute(() =>
    {
        var character = Selected ?? throw new InvalidDataException("保存键位时没有选中角色");
        return settings.SaveHotkeys(owner, HotkeySettingsRules.Profile(character.Job), text);
    });

    public bool SaveGameOptions(ClientSettingsService settings, ImmutableArray<byte> options) => owner.Execute(() =>
    {
        if (pendingRental is not null || HasPendingTownQuestCommit) return false;
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
        if (pendingRental is null && !HasPendingTownQuestCommit && character.VisibilityFlags != flags)
            Selected = character with { VisibilityFlags = flags };
        return new ClientSettingsReceipt(restored, owner.Generation, SelectionEpoch, character.Id, flags, HotkeySettingsRules.Profile(character.Job));
    });

    public bool CanPublishClientSettings(ClientSettingsReceipt receipt) => owner.Execute(() =>
        owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch && Selected is { } character &&
        character.Id == receipt.CharacterId && character.VisibilityFlags == receipt.VisibilityFlags &&
        HotkeySettingsRules.Profile(character.Job) == receipt.HotkeyProfile);
}
