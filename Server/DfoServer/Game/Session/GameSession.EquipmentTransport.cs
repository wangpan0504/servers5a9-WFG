using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public CreatureTransportResult TransportEquipment(EquipmentTransportService service, CreatureTransportRequest request)
    {
        var capture = owner.Execute(() =>
        {
            // 收件值是最近下发名册的行索引，不是角色ID、持久槽位或排除自身后的下拉框序号。
            var recipient = publishedRoster is { } roster && roster.Generation == owner.Generation &&
                request.RecipientIndex < roster.Entries.Length ? roster.Entries[(int)request.RecipientIndex] : null;
            return (Character: CanTransport() ? Selected : null, Recipient: recipient, Epoch: SelectionEpoch);
        });
        if (capture.Character is not { } character || request.Mode != 0 && capture.Recipient is null)
            return new(InventoryResult.Reject(7));
        int sourceId = request.Mode == 2 ? 0 :
            character.Inventory.Find(new(InventoryListType.Main, unchecked((short)request.StoneSlot)))?.Core.ItemId ?? 0;
        if (!CanUseStackableItem(character, sourceId)) return new(InventoryResult.Reject(21));
        string place = towns.IsRoom(character.Position, QuestEligibility.Completed(character)) ? "[seria room]" : "[village]";
        ushort error = service.Plan(character, capture.Recipient, request, place, out var plan);
        if (error != 0) return new(InventoryResult.Reject(error));
        return owner.Execute(() =>
        {
            if (!CanTransport() || SelectionEpoch != capture.Epoch || !ReferenceEquals(Selected, character))
                return new CreatureTransportResult(InventoryResult.Reject(7));
            if (!CanUseStackableItem(character, sourceId)) return new CreatureTransportResult(InventoryResult.Reject(21));
            var committed = service.Commit(owner, plan!);
            Selected = committed.Character;
            return new CreatureTransportResult(new(true, 0, plan!.Inventory)
            {
                Receipt = new(Selected, owner.Generation, SelectionEpoch)
                {
                    PreviousQuests = character.ActiveMainQuests,
                    PreviousSkillSwitchInventory = character.SkillSwitchInventory
                }
            }, capture.Recipient?.Id ?? 0, committed.MailId);
        });

        bool CanTransport() => Ready && Phase == SessionPhase.Town && Selected is not null &&
            pendingRental is null && !HasPendingTownQuestCommit && !HasPendingFormalCard &&
            (request.Mode != 2 || HasEquipmentSoulNpc(false));
    }

    public bool HasEquipmentSoulNpc(bool reseal) => owner.Execute(() => Ready && Phase == SessionPhase.Town &&
        Selected is { } character && towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) &&
        area.Map(QuestEligibility.Completed(character)) is { Error: null } map &&
        map.Npcs.Any(npc => (reseal ? towns.EquipmentResealNpcs : towns.EquipmentTransportNpcs).Contains(npc.Id)));
}
