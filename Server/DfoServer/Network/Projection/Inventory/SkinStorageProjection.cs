using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

internal sealed class SkinStorageProjection(InventorySkinStorageService skins)
{
    public async Task InitializeAsync(GameConnectionContext context, Character character)
    {
        long epoch = context.Session.SelectionEpoch;
        bool Guard() => context.Owner.Execute(() => context.Session.SelectionEpoch == epoch && ReferenceEquals(context.Session.Selected, character));
        if (!Guard()) return;
        await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.Owned(character.Inventory.Skins), "weapon_skin_initialize", Guard);
        await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.Selection(skins.EffectiveSelection(character)), "weapon_skin_selection_initialize", Guard);
        if (skins.HasDamageFonts)
        {
            await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.DamageFonts(skins.View(character)), "damage_font_initialize", Guard);
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.Selection(skins.EffectiveDamageFont(character),
                InventorySkinStorageService.DamageFontType), "damage_font_selection_initialize", Guard);
        }
        if (skins.HasCutscenes)
        {
            await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.Cutscenes(skins.View(character)), "skill_cutscene_initialize", Guard);
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.CutsceneSelection(skins.EffectiveCutscenes(character)),
                "skill_cutscene_selection_initialize", Guard);
        }
    }

    public async Task AddedAsync(GameConnectionContext context, InventoryCommitReceipt receipt, SkinStorageKey key)
    {
        bool Guard() => context.Session.CanPublishInventory(receipt);
        if (!Guard()) return;
        byte[] owned = key.Type == InventorySkinStorageService.Type ? SkinStoragePackets.Owned(receipt.Character.Inventory.Skins) :
            key.Type == InventorySkinStorageService.CutsceneType ? SkinStoragePackets.Cutscenes(skins.View(receipt.Character)) :
                SkinStoragePackets.DamageFonts(skins.View(receipt.Character));
        await context.Notify(NotiPacketType.SKIN_CARGO_INFO, owned, "skin_storage_added", Guard);
        await context.Notify(NotiPacketType.RECENT_ADD_SKIN_LIST, SkinStoragePackets.Recent(key.SkinId, key.Type), "skin_storage_recent", Guard);
        if (key.Type == InventorySkinStorageService.DamageFontType)
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.Selection(skins.EffectiveDamageFont(receipt.Character),
                key.Type), "damage_font_selection_committed", Guard);
        if (key.Type == InventorySkinStorageService.CutsceneType)
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.CutsceneSelection(skins.EffectiveCutscenes(receipt.Character)),
                "skill_cutscene_selection_committed", Guard);
    }

    public async Task RefreshAsync(GameConnectionContext context)
    {
        var character = context.Owner.Execute(() => context.Session.Ready ? context.Session.Selected : null);
        if (character is null || !skins.NeedsFontReset(character) && !skins.NeedsCutsceneReset(character)) return;
        var result = context.Session.PlanAndCommitInventory(skins.ExpireSelections, requireTown: false);
        if (result.Receipt is not { } receipt || result.Plan is not { Changed: true }) return;
        bool Guard() => context.Session.CanPublishInventory(receipt);
        var view = skins.View(receipt.Character);
        if (result.Plan.Before.Skins.SelectedDamageFont != receipt.Character.Inventory.Skins.SelectedDamageFont)
        {
            await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.DamageFonts(view), "damage_font_expired", Guard);
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.Selection(1, InventorySkinStorageService.DamageFontType),
                "damage_font_expired_selection", Guard);
        }
        if (!result.Plan.Before.Skins.SelectedCutscenes.SequenceEqual(receipt.Character.Inventory.Skins.SelectedCutscenes))
        {
            await context.Notify(NotiPacketType.SKIN_CARGO_INFO, SkinStoragePackets.Cutscenes(view), "skill_cutscene_expired", Guard);
            await context.Notify(NotiPacketType.SELECT_SKIN_LIST, SkinStoragePackets.CutsceneSelection(view.SelectedCutscenes),
                "skill_cutscene_expired_selection", Guard);
        }
    }
}
