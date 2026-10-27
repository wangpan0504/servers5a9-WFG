using DfoServer.Game.Accounts;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public CreatureTransportCommit CommitEquipmentTransport(AccountOwners.Lease owner, EquipmentTransportPlan plan) => owner.Execute<CreatureTransportCommit>(() =>
    {
        bool split = plan.Transferred.Uid == 0 && plan.Target.Core.Kind == GameWorld.Items.ItemKind.Emblem &&
            plan.Target.Core.Count > 1 && plan.Transferred.Core == plan.Target.Core with { Value = 1 } &&
            plan.Inventory.AfterIndex.FindUid(plan.Target.Uid) == plan.Target with { Core = plan.Target.Core with { Value = plan.Target.Core.Count - 1 } };
        if (plan.Inventory.Reason != "equipment_transport" || plan.Target.Uid <= 0 ||
            !split && plan.Transferred.Uid != plan.Target.Uid || plan.Inventory.BeforeIndex.Find(plan.Target.Position) != plan.Target ||
            (plan.Recipient is null ? plan.Inventory.AfterIndex.FindUid(plan.Target.Uid) != plan.Transferred :
                !split && plan.Inventory.AfterIndex.FindUid(plan.Target.Uid) is not null))
            throw new InvalidOperationException("装备跨界计划与原实例不符");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.EquipmentTransport");
        using var tx = write.Transaction;
        var recipient = plan.Recipient is null ? null : FindMailRecipientById(db, tx, plan.Recipient.Id);
        if (plan.Recipient is not null && (recipient is null || recipient.Id == plan.Character.Id ||
            recipient.Account != owner.Account || recipient.Name != plan.Recipient.Name))
            throw new InvalidOperationException("装备跨界收件角色已变化或不属于本账号");
        var character = CommitInventoryInTransaction(db, tx, owner, plan.Character, plan.Inventory,
            recipient is null || split ? null : new HashSet<long> { plan.Target.Uid });
        uint mailId = 0;
        if (recipient is not null)
        {
            mailId = InsertMail(db, tx, recipient, character.Id, character.Name, "跨界转移的物品，请领取附件。", 0,
                plan.Created, plan.Created + 15 * 86400, null, null);
            var held = HoldMailItem(db, tx, recipient, plan.Transferred, owner.Account, character.Id);
            InsertMailAttachment(db, tx, mailId, 0, plan.Target.Uid, held);
            AuditMail(db, tx, mailId, owner.Account, character.Id, owner.Generation, "equipment_transport", plan.Created,
                new { Recipient = recipient.Id, Item = held });
        }
        tx.Commit();
        return new(character, mailId);
    });
}
