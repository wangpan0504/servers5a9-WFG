using DfoServer.Game.Accounts;
using DfoServer.Game.Creatures;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public CreatureTransportCommit CommitCreatureTransport(AccountOwners.Lease owner, CreatureTransportPlan plan) => owner.Execute<CreatureTransportCommit>(() =>
    {
        if (plan.Inventory.Reason != "creature_transport" || plan.Pet.Uid <= 0 ||
            plan.Inventory.BeforeIndex.Find(plan.Pet.Position) != plan.Pet || plan.Inventory.AfterIndex.FindUid(plan.Pet.Uid) is not null)
            throw new InvalidOperationException("宠物跨界计划与原实例不符");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CreatureTransport");
        using var tx = write.Transaction;
        var recipient = FindMailRecipientById(db, tx, plan.Recipient.Id);
        if (recipient is null || recipient.Id == plan.Character.Id || recipient.Account != owner.Account || recipient.Name != plan.Recipient.Name)
            throw new InvalidOperationException("宠物跨界收件角色已变化或不属于本账号");
        var character = CommitInventoryInTransaction(db, tx, owner, plan.Character, plan.Inventory, new HashSet<long> { plan.Pet.Uid });
        uint id = InsertMail(db, tx, recipient, character.Id, character.Name, "宠物跨界石转移的宠物，请领取附件。", 0,
            plan.Created, plan.Created + 15 * 86400, null, null);
        var held = HoldMailItem(db, tx, recipient, plan.Pet, owner.Account, character.Id);
        InsertMailAttachment(db, tx, id, 0, plan.Pet.Uid, held);
        AuditMail(db, tx, id, owner.Account, character.Id, owner.Generation, "creature_transport", plan.Created,
            new { Recipient = recipient.Id, Item = held });
        tx.Commit();
        return new(character, id);
    });
}
