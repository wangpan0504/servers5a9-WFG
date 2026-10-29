using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Guild;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public InventoryResult CompoundEmblem(EmblemCompoundRequest request, InventoryEmblemCompoundService service,
        out EmblemCompoundReceipt? receipt, out string? rejection, GuildNpcBenefitService? benefits = null)
        => CompoundEmblem(request, service, out receipt, out rejection, out _, benefits);

    public InventoryResult CompoundEmblem(EmblemCompoundRequest request, InventoryEmblemCompoundService service,
        out EmblemCompoundReceipt? receipt, out string? rejection, out GuildNpcBenefitCommit? benefitCommit,
        GuildNpcBenefitService? benefits = null)
    {
        benefitCommit = null;
        EmblemCompoundReceipt? plannedReceipt = null;
        string? plannedRejection = null;
        if (benefits is not null)
        {
            var captured = owner.Execute(() => (Character: CanCompound() ? Selected : null,
                Actor: CaptureGuildManagement(), SelectionEpoch, Phase));
            if (captured.Character is null || captured.Actor is null)
            {
                receipt = null; rejection = "state-or-snapshot"; return InventoryResult.Reject(22);
            }
            // 公会读取与奖池随机规划均不持账号锁；写事务内只复核已冻结的结果。
            var selected = benefits.SelectEmblem(captured.Actor);
            if (selected is not null)
            {
                var planned = service.Apply(captured.Character, request, out plannedReceipt, out plannedRejection,
                    DateTimeOffset.FromUnixTimeSeconds(benefits.Now), selected.Definition.Parameters[0]);
                GuildNpcBenefitCommit? consumed = null;
                InventoryResult committed;
                if (!planned.Accepted || plannedReceipt is null || !service.UsesGuildPool(plannedReceipt, selected.Definition.Parameters[0]))
                {
                    // 无精确公会池时提交已生成的普通结果，不能重抽随机或消耗福利次数。
                    committed = PlanAndCommitInventory(character => ReferenceEquals(character, captured.Character) &&
                        captured.Actor.IsCurrent() ? planned : InventoryResult.Reject(22), 22);
                }
                else
                {
                    committed = owner.Execute(() =>
                    {
                        if (!CanCompound() || SelectionEpoch != captured.SelectionEpoch || Phase != captured.Phase ||
                            !ReferenceEquals(Selected, captured.Character) || !captured.Actor.IsCurrent()) return InventoryResult.Reject(22);
                        consumed = benefits.CommitEmblem(captured.Actor, captured.Character, planned.Plan!, selected);
                        if (!consumed.Success || consumed.Character is null) return InventoryResult.Reject(22);
                        Selected = consumed.Character;
                        return planned with { Receipt = new(Selected, owner.Generation, SelectionEpoch)
                        {
                            Phase = Phase, PreviousQuests = captured.Character.ActiveMainQuests,
                            PreviousSkillSwitchInventory = captured.Character.SkillSwitchInventory
                        } };
                    });
                }
                benefitCommit = consumed;
                receipt = committed.Accepted ? plannedReceipt : null;
                rejection = committed.Accepted ? null : plannedRejection ?? "state-or-snapshot";
                return committed;
            }
        }
        var result = PlanAndCommitInventory(character =>
            service.Apply(character, request, out plannedReceipt, out plannedRejection), 22);
        receipt = result.Accepted ? plannedReceipt : null;
        rejection = result.Accepted ? null : plannedRejection ?? "state-or-snapshot";
        return result;

        bool CanCompound() => pendingRental is null && pendingConsumableUse is null && Ready && Selected is not null &&
            Phase == SessionPhase.Town && !HasPendingTownQuestCommit && !HasPendingFormalCard &&
            formalDungeon?.PendingTutorialClear is null;
    }
}
