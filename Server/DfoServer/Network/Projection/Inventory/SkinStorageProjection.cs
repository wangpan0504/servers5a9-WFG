using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

internal sealed class SkinStorageProjection(InventorySkinStorageService skins, PartyService? parties = null, TownPresence? presence = null)
{
    public async Task RefreshPartyFrameAsync(GameConnectionContext context, InventoryCommitReceipt receipt)
    {
        if (!context.Session.CanPublishInventory(receipt) ||
            context.Session.PublicCharacter(receipt.Character.Id) is not { } identity ||
            !ReferenceEquals(identity.Character, receipt.Character)) return;
        context.Owner.Execute(() =>
        {
            if (identity.IsUnchanged() && identity.Phase == SessionPhase.Town)
                presence?.Refresh(context.Owner, context.Sender, identity.Character, identity.AdventureName,
                    broadcast: false, pvpGrade: identity.PvpGrade, allowMissing: true);
            return true;
        });
        var notices = parties?.FrameChanged(context.Session, identity) ?? [];
        if (notices.Length != 0)
            parties!.Publish(notices, presenceChanged: false);
        else
            await context.Notify(NotiPacketType.USERINFO,
                TownPackets.Actor(identity.Character, identity.PvpGrade, identity.Channel, adventureName: identity.AdventureName,
                    sceneState: identity.Phase is SessionPhase.Dungeon or SessionPhase.Settlement
                        ? ActorSceneState.Dungeon : ActorSceneState.Town, premiumPcRoom: identity.PremiumPcRoom),
                "party_frame_actor", identity.IsUnchanged);
    }

    public async Task InitializeAsync(GameConnectionContext context, Character character)
    {
        long epoch = context.Session.SelectionEpoch;
        bool Guard() => context.Owner.Execute(() => context.Session.SelectionEpoch == epoch && ReferenceEquals(context.Session.Selected, character));
        if (!Guard()) return;
        await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.Owned(character.Inventory.Skins, character.Inventory.AccountSkins), "weapon_skin_initialize", Guard);
        await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.Selection(skins.EffectiveSelection(character)), "weapon_skin_selection_initialize", Guard);
        var view = skins.View(character);
        var account = skins.AccountView(character);
        if (skins.HasDamageFonts)
        {
            await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.DamageFonts(view, account), "damage_font_initialize", Guard);
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.Selection(skins.EffectiveDamageFont(character),
                InventorySkinStorageService.DamageFontType), "damage_font_selection_initialize", Guard);
        }
        if (skins.HasCutscenes)
        {
            await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.Cutscenes(view, account), "skill_cutscene_initialize", Guard);
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.CutsceneSelection(skins.EffectiveCutscenes(character)),
                "skill_cutscene_selection_initialize", Guard);
        }
        if (skins.HasPartyFrames)
        {
            await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.PartyFrames(view, account), "party_frame_initialize", Guard);
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.Selection(skins.EffectivePartyFrame(character),
                InventorySkinStorageService.PartyFrameType), "party_frame_selection_initialize", Guard);
        }
        // 即时表情没有默认皮肤，角色与账号都为空时不发空列表，避免覆盖客户端本地保存。
        if (skins.NeedsEmoticonInitialize(character))
        {
            await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.Emoticons(view, account), "instant_emoticon_initialize", Guard);
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.EmoticonSelection(skins.EffectiveEmoticons(character)),
                "instant_emoticon_selection_initialize", Guard);
        }
        // 01764A90整份替换最近列表；空列表也须发送，清除同连接上一角色的记录。
        await context.Notify(NotiPacketType.RECENT_ADD_SKIN_LIST, SkinStoragePackets.Recent(skins.Recent(character)),
            "skin_storage_recent_initialize", Guard);
    }

    public async Task AddedAsync(GameConnectionContext context, InventoryCommitReceipt receipt, IReadOnlyList<SkinStorageKey> keys)
    {
        bool Guard() => context.Session.CanPublishInventory(receipt);
        if (!Guard() || keys.Count == 0) return;
        var key = keys[0];
        var view = key.Type == InventorySkinStorageService.Type ? null : skins.View(receipt.Character);
        var account = skins.AccountView(receipt.Character);
        byte[] owned = key.Type switch
        {
            InventorySkinStorageService.Type => SkinStoragePackets.Owned(receipt.Character.Inventory.Skins, receipt.Character.Inventory.AccountSkins),
            InventorySkinStorageService.CutsceneType => SkinStoragePackets.Cutscenes(view!, account),
            InventorySkinStorageService.DamageFontType => SkinStoragePackets.DamageFonts(view!, account),
            InventorySkinStorageService.PartyFrameType => SkinStoragePackets.PartyFrames(view!, account),
            _ => SkinStoragePackets.Emoticons(view!, account)
        };
        await context.Notify(NotiPacketType.SKIN_CARGO_INFO, owned, "skin_storage_added", Guard);
        await context.Notify(NotiPacketType.RECENT_ADD_SKIN_LIST, SkinStoragePackets.Recent(skins.Recent(receipt.Character)), "skin_storage_recent", Guard);
        if (key.Type == InventorySkinStorageService.DamageFontType)
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.Selection(skins.EffectiveDamageFont(receipt.Character),
                key.Type), "damage_font_selection_committed", Guard);
        if (key.Type == InventorySkinStorageService.CutsceneType)
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.CutsceneSelection(skins.EffectiveCutscenes(receipt.Character)),
                "skill_cutscene_selection_committed", Guard);
        if (key.Type == InventorySkinStorageService.PartyFrameType)
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.Selection(skins.EffectivePartyFrame(receipt.Character),
                key.Type), "party_frame_selection_committed", Guard);
        if (key.Type == InventorySkinStorageService.EmoticonType)
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.EmoticonSelection(skins.EffectiveEmoticons(receipt.Character)),
                "instant_emoticon_selection_committed", Guard);
    }

    public async Task RefreshAsync(GameConnectionContext context)
    {
        var character = context.Owner.Execute(() => context.Session.Ready ? context.Session.Selected : null);
        if (character is null || !skins.NeedsFontReset(character) && !skins.NeedsCutsceneReset(character) &&
            !skins.NeedsPartyFrameReset(character) && !skins.NeedsEmoticonReset(character)) return;
        var result = context.Session.PlanAndCommitInventory(skins.ExpireSelections, requireTown: false);
        if (result.Receipt is not { } receipt || result.Plan is not { Changed: true }) return;
        bool Guard() => context.Session.CanPublishInventory(receipt);
        var view = skins.View(receipt.Character);
        var account = skins.AccountView(receipt.Character);
        if (result.Plan.Before.Skins.SelectedDamageFont != receipt.Character.Inventory.Skins.SelectedDamageFont)
        {
            await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.DamageFonts(view, account), "damage_font_expired", Guard);
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.Selection(1, InventorySkinStorageService.DamageFontType),
                "damage_font_expired_selection", Guard);
        }
        if (!result.Plan.Before.Skins.SelectedCutscenes.SequenceEqual(receipt.Character.Inventory.Skins.SelectedCutscenes))
        {
            await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.Cutscenes(view, account), "skill_cutscene_expired", Guard);
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.CutsceneSelection(view.SelectedCutscenes),
                "skill_cutscene_expired_selection", Guard);
        }
        if (result.Plan.Before.Skins.SelectedPartyFrame != receipt.Character.Inventory.Skins.SelectedPartyFrame)
        {
            await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.PartyFrames(view, account), "party_frame_expired", Guard);
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.Selection(view.SelectedPartyFrame,
                InventorySkinStorageService.PartyFrameType), "party_frame_expired_selection", Guard);
            await RefreshPartyFrameAsync(context, receipt);
        }
        if (!result.Plan.Before.Skins.SelectedEmoticons.SequenceEqual(receipt.Character.Inventory.Skins.SelectedEmoticons))
        {
            await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.Emoticons(view, account), "instant_emoticon_expired", Guard);
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.EmoticonSelection(view.SelectedEmoticons),
                "instant_emoticon_expired_selection", Guard);
        }
    }
}
