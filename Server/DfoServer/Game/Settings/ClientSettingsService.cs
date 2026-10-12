using System.Collections.Immutable;
using DfoServer.Game.Accounts;

namespace DfoServer.Game.Settings;

internal sealed class ClientSettingsService(IClientSettingsStore store)
{
    public const int GameOptionLength = 204;
    public const int CharacterOptionLength = 512;

    public ClientSettingsSnapshot Load(AccountOwners.Lease owner, ushort characterId)
    {
        var saved = store.Load(owner, characterId);
        if (!saved.Hotkeys.IsDefault) HotkeySettingsRules.Validate(saved.Hotkeys);
        return saved;
    }
    public bool SaveHotkeys(AccountOwners.Lease owner, byte profile, ImmutableArray<byte> text)
    {
        if (profile > 1) throw new InvalidDataException("键位配置类型无效");
        HotkeySettingsRules.Validate(text);
        return store.SaveHotkeys(owner, profile, text);
    }
    public bool SaveGameOptions(AccountOwners.Lease owner, ImmutableArray<byte> payload, ushort? characterId = null)
    {
        Validate(payload, GameOptionLength);
        if (!characterId.HasValue)
            return store.SaveGameOptions(owner, payload);
        return owner.Execute(() =>
        {
            var saved = store.Load(owner, characterId.Value);
            var characterOptions = ClientSettingsRules.ForCharacterSave(payload, saved.CharacterOptions);
            return store.SaveGameOptions(owner, payload, characterId, characterOptions);
        });
    }
    public bool SaveCharacterOptions(AccountOwners.Lease owner, ushort characterId, ImmutableArray<byte> payload)
    {
        Validate(payload, CharacterOptionLength);
        return store.SaveCharacterOptions(owner, characterId, payload);
    }

    static void Validate(ImmutableArray<byte> payload, int length)
    {
        if (payload.IsDefault || payload.Length != length)
            throw new InvalidDataException("客户端设置长度无效");
    }
}
