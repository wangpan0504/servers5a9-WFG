using System.Collections.Immutable;
using DfoServer.Game.Accounts;

namespace DfoServer.Game.Settings;

internal sealed class ClientSettingsService(IClientSettingsStore store, DfoServer.GameWorld.Characters.CharacterEmotionCatalog? emotions = null,
    DfoServer.GameWorld.Quests.TitleCatalog? titles = null)
{
    public const int GameOptionLength = 204;
    public const int CharacterOptionLength = 512;
    public const int QuickChatLength = 300;
    public const int RecentEmoticonLength = 18 * sizeof(uint);
    public bool? SaveCloneTitle(AccountOwners.Lease owner, ushort characterId, uint itemId, long? expiresAt = null) =>
        store.SaveCloneTitle(owner, characterId, itemId, expiresAt);
    public (ushort Error, bool Changed) SaveEmotion(AccountOwners.Lease owner, DfoServer.Game.Characters.Character character, ushort emotion)
    {
        ushort error = EmotionSelectionRules.Error(emotions, titles, character, emotion);
        return error == 0 ? ((ushort)0, store.SaveEmotion(owner, character.Id, emotion)) : (error, false);
    }

    public ClientSettingsSnapshot Load(AccountOwners.Lease owner, ushort characterId)
    {
        var saved = store.Load(owner, characterId);
        if (!saved.Hotkeys.IsDefault) HotkeySettingsRules.Validate(saved.Hotkeys);
        if (!saved.RecentEmoticons.IsDefault) ValidateRecentEmoticons(saved.RecentEmoticons);
        if (!saved.QuestNotify.IsDefault) QuestNotifyRules.Validate(saved.QuestNotify);
        return saved;
    }
    public bool SaveQuestNotify(AccountOwners.Lease owner, ushort characterId, ImmutableArray<byte> entries)
    {
        QuestNotifyRules.Validate(entries);
        return store.SaveQuestNotify(owner, characterId, entries);
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
    public bool SaveQuickChat(AccountOwners.Lease owner, byte bank, ImmutableArray<byte> text)
    {
        // 两个银行独立保存；客户端每次提交完整300B原文，服务端不做编码解释。
        if (bank > 1) throw new InvalidDataException("快捷聊天银行编号无效");
        Validate(text, QuickChatLength);
        return store.SaveQuickChat(owner, bank, text);
    }

    public bool SaveChatEmoticons(AccountOwners.Lease owner, ImmutableArray<ChatEmoticonMacro> macros) =>
        store.SaveChatEmoticons(owner, macros);

    public bool SaveRecentEmoticons(AccountOwners.Lease owner, ImmutableArray<byte> entries)
    {
        ValidateRecentEmoticons(entries);
        return store.SaveRecentEmoticons(owner, entries);
    }

    public static void ValidateRecentEmoticons(ImmutableArray<byte> entries)
    {
        Validate(entries, RecentEmoticonLength);
        for (int offset = 0; offset < entries.Length; offset += sizeof(uint))
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan()[offset..]) > short.MaxValue)
                throw new InvalidDataException("最近表情编号超出当前名册读宽");
    }

    static void Validate(ImmutableArray<byte> payload, int length)
    {
        if (payload.IsDefault || payload.Length != length)
            throw new InvalidDataException("客户端设置长度无效");
    }
}
