using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public ExpertJobTradeCommit CommitExpertJobTrade(AccountOwners.Lease customerLease, AccountOwners.Lease ownerLease,
        ExpertJobTradePlan plan) => AccountOwners.Lease.ExecuteTogether(customerLease, ownerLease, () =>
    {
        if (!customerLease.IsCurrent || !ownerLease.IsCurrent)
            throw new InvalidOperationException("副职业交易租约已失效");
        var customer = plan.CustomerExpected;
        var owner = plan.OwnerExpected;
        var debit = plan.CustomerInventory;
        var credit = plan.OwnerInventory;
        if (StringComparer.Ordinal.Equals(customerLease.Account, ownerLease.Account) || customer.Id == owner.Id ||
            owner.ExpertJobType is not (1 or 3) || plan.OwnerExperience < owner.ExpertJobExperience ||
            plan.OwnerEndurance < 0 || plan.OwnerEndurance >= owner.ExpertMachineEndurance || !debit.Changed ||
            checked((long)debit.Before.Gold + plan.CustomerGoldReward - plan.SystemGoldCost - plan.Fee) != debit.After.Gold ||
            (long)credit.After.Gold - credit.Before.Gold != plan.Fee ||
            !SameInventory(credit.After with { Gold = credit.Before.Gold }, credit) ||
            credit.OverflowItems.Length != 0 || credit.ExpBonus is not null || credit.EquipmentSlotFlagsAfter is not null)
            throw new InvalidDataException("副职业交易计划、参与者或费用不守恒");
        ValidateInventoryPlan(customer, debit);
        ValidateInventoryPlan(owner, credit);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitExpertJobTrade");
        using var transaction = write.Transaction;
        var currentCustomer = Require(db, transaction, customerLease.Account, customer.Name);
        var currentOwner = Require(db, transaction, ownerLease.Account, owner.Name);
        ValidateTradeQualification(currentCustomer, customer);
        ValidateTradeQualification(currentOwner, owner);
        var committedCustomer = CommitInventoryInTransaction(db, transaction, customerLease, currentCustomer, customer, debit);
        var committedOwner = CommitInventoryInTransaction(db, transaction, ownerLease, currentOwner, owner, credit);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", owner.Id);
        command.Parameters.AddWithValue("$account", ownerLease.Account);
        if (!credit.Changed)
        {
            // 免费服务仍消费机器状态，摊主也必须推进一次版本以拒绝旧计划重放。
            command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
            command.Parameters.AddWithValue("$revision", owner.Revision);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业摊主版本已变化");
            committedOwner = committedOwner with { Revision = checked(owner.Revision + 1) };
        }
        command.CommandText = "UPDATE character_expert_job SET expertness=$exp WHERE character_id=$id AND account=$account AND job_type=$type";
        command.Parameters.AddWithValue("$exp", plan.OwnerExperience);
        command.Parameters.AddWithValue("$type", owner.ExpertJobType);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业摊主资格已变化");
        UpsertExpertMeta(command, ownerLease.Account, owner.Id, owner.ExpertGiveUpState, owner.ExpertMachineGrade, plan.OwnerEndurance);
        transaction.Commit();
        return new ExpertJobTradeCommit(committedCustomer, committedOwner with
        {
            ExpertJobExperience = plan.OwnerExperience, ExpertMachineEndurance = plan.OwnerEndurance
        });
    });

    static void ValidateTradeQualification(Character current, Character expected)
    {
        // GM 资格调整未必推进资产版本，双方等级及副职业快照必须独立复验。
        if (current.Level != expected.Level || current.Job != expected.Job || current.GrowType != expected.GrowType ||
            current.EquipmentSlotFlags != expected.EquipmentSlotFlags || current.ExpertJobType != expected.ExpertJobType ||
            current.ExpertJobExperience != expected.ExpertJobExperience || current.ExpertGiveUpState != expected.ExpertGiveUpState ||
            current.ExpertMachineGrade != expected.ExpertMachineGrade || current.ExpertMachineEndurance != expected.ExpertMachineEndurance ||
            !current.ExpertJobRecipes.SetEquals(expected.ExpertJobRecipes))
            throw new InvalidOperationException("副职业交易参与者资格已经变化");
    }
}
