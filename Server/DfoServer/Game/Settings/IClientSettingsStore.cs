using System.Collections.Immutable;
using DfoServer.Game.Accounts;

namespace DfoServer.Game.Settings;

internal interface IClientSettingsStore
{
    ClientSettingsSnapshot Load(AccountOwners.Lease owner, ushort characterId);
    bool SaveGameOptions(AccountOwners.Lease owner, ImmutableArray<byte> payload, ushort? characterId = null,
        ImmutableArray<byte> characterGameOptions = default);
    bool SaveCharacterOptions(AccountOwners.Lease owner, ushort characterId, ImmutableArray<byte> payload);
}
