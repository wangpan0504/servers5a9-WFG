using System.Collections.Immutable;
using DfoServer.Game.Accounts;

namespace DfoServer.Game.Settings;

internal interface IClientSettingsStore
{
    ClientSettingsSnapshot Load(AccountOwners.Lease owner, ushort characterId);
    bool SaveGameOptions(AccountOwners.Lease owner, ImmutableArray<byte> payload, ushort? characterId = null,
        ImmutableArray<byte> characterGameOptions = default);
    bool SaveCharacterOptions(AccountOwners.Lease owner, ushort characterId, ImmutableArray<byte> payload);
    bool SaveHotkeys(AccountOwners.Lease owner, byte profile, ImmutableArray<byte> text);
    bool SaveEmotion(AccountOwners.Lease owner, ushort characterId, ushort emotion);
}
