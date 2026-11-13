using System.Collections.Immutable;
using DfoServer.Game.Accounts;

namespace DfoServer.Game.Settings;

internal interface IClientSettingsStore
{
    ClientSettingsSnapshot Load(AccountOwners.Lease owner, ushort characterId);
    bool SaveGameOptions(AccountOwners.Lease owner, ImmutableArray<byte> payload, ushort? characterId = null,
        ImmutableArray<byte> characterGameOptions = default);
    bool SaveCharacterOptions(AccountOwners.Lease owner, ushort characterId, ImmutableArray<byte> payload);
    bool SaveHotkeys(AccountOwners.Lease owner, ushort characterId, byte profile, ImmutableArray<byte> text);
    bool SaveSeenNotice(AccountOwners.Lease owner, byte id);
    bool SaveQuickChat(AccountOwners.Lease owner, byte bank, ImmutableArray<byte> text);
    bool SaveChatEmoticons(AccountOwners.Lease owner, ImmutableArray<ChatEmoticonMacro> macros);
    bool SaveRecentEmoticons(AccountOwners.Lease owner, ImmutableArray<byte> entries);
    bool SaveQuestNotify(AccountOwners.Lease owner, ushort characterId, ImmutableArray<byte> entries);
    bool SaveNpcBookmarks(AccountOwners.Lease owner, ushort characterId, ImmutableArray<byte> entries);
    bool SaveEmotion(AccountOwners.Lease owner, ushort characterId, ushort emotion);
    bool? SaveCloneTitle(AccountOwners.Lease owner, ushort characterId, uint itemId, long? expiresAt = null);
}
