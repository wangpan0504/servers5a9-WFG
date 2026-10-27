using DfoServer.Game.Accounts;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public CreatureTransportCommit CommitCreatureTransport(AccountOwners.Lease owner, CreatureTransportPlan plan,
        CreatureTransportMailPolicy currentPolicy) => owner.Execute<CreatureTransportCommit>(() =>
    {
        if (plan.Inventory.Reason != "creature_transport" || plan.Pet.Uid <= 0 ||
            plan.Inventory.BeforeIndex.Find(plan.Pet.Position) != plan.Pet || plan.Inventory.AfterIndex.FindUid(plan.Pet.Uid) is not null ||
            plan.Stone is not { Uid: > 0, Position.List: InventoryListType.Main, Core.Kind: ItemKind.Consumable, Core.Count: > 0 } ||
            plan.Inventory.BeforeIndex.Find(plan.Stone.Position) != plan.Stone ||
            plan.MailExpiry != currentPolicy.Create(plan.Stone.Core.ItemId, plan.Created))
            throw new InvalidOperationException("宠物跨界计划与原实例不符");
        var remainingStone = plan.Stone.Core.Count == 1 ? null :
            plan.Stone with { Core = plan.Stone.Core with { Value = plan.Stone.Core.Count - 1 } };
        if (plan.Inventory.AfterIndex.Find(plan.Stone.Position) != remainingStone ||
            plan.Inventory.AfterIndex.FindUid(plan.Stone.Uid) != remainingStone)
            throw new InvalidOperationException("宠物跨界计划未准确消费原跨界石");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CreatureTransport");
        using var tx = write.Transaction;
        var recipient = FindMailRecipientById(db, tx, plan.Recipient.Id);
        if (recipient is null || recipient.Id == plan.Character.Id || recipient.Account != owner.Account || recipient.Name != plan.Recipient.Name)
            throw new InvalidOperationException("宠物跨界收件角色已变化或不属于本账号");
        using (var identity = db.CreateCommand())
        {
            identity.Transaction = tx;
            identity.CommandText = "SELECT slot FROM characters WHERE id=$id";
            identity.Parameters.AddWithValue("$id", recipient.Id);
            if (Convert.ToInt32(identity.ExecuteScalar()) != plan.Recipient.Slot)
                throw new InvalidOperationException("宠物跨界收件角色名册槽位已变化");
        }
        var character = CommitInventoryInTransaction(db, tx, owner, plan.Character, plan.Inventory, new HashSet<long> { plan.Pet.Uid });
        uint id = InsertMail(db, tx, recipient, character.Id, character.Name, "宠物跨界石转移的宠物，请领取附件。", 0,
            plan.Created, plan.MailExpiry.ExpiresAt, null, null);
        var held = HoldMailItem(db, tx, recipient, plan.Pet, owner.Account, character.Id);
        InsertMailAttachment(db, tx, id, 0, plan.Pet.Uid, held);
        AuditMail(db, tx, id, owner.Account, character.Id, owner.Generation, "creature_transport", plan.Created,
            new { Recipient = recipient.Id, Item = held });
        tx.Commit();
        return new(character, id);
    });
}
