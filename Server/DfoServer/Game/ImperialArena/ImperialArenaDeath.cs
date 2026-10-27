using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.ImperialArena;

internal sealed record ImperialArenaDeathPlan(SinglePlayerDungeonRun Run, PreparedDungeonDeath Death,
    ImperialArenaAdmission Admission, int Round, ImmutableArray<ImperialArenaRewardItem> Reward,
    ImmutableArray<InventoryItem> Items, long Created, long ElapsedMilliseconds, int PreviousHardRound)
{
    public int MonsterId => Admission.Route.Opponents(Admission.Mode)[Round - 1].MonsterId;
    public void Validate()
    {
        Admission.Validate();
        if (Round < 1 || Round > Admission.Route.Opponents(Admission.Mode).Length ||
            !ReferenceEquals(Run.ImperialArenaAdmission, Admission) ||
            !Run.IsPendingDeath(Death) && !Run.IsConfirmedDeath(Death) || Death.Monster.MonsterId != MonsterId ||
            Death.ImperialArenaRound != Round || Death.Death.Identity.InstanceId != Run.Snapshot.Identity.InstanceId ||
            Created < Admission.AdmittedAt || ElapsedMilliseconds < 0 || Reward.IsDefault || Items.IsDefault || Items.Length > 10)
            throw new InvalidDataException("帝国竞技场奖励缺少本实例实际对手击杀事实");
        var actual = Items.GroupBy(item => item.Core.ItemId).OrderBy(row => row.Key).Select(row => (row.Key, row.Sum(item => item.Core.Count)));
        var expected = Reward.OrderBy(row => row.ItemId).Select(row => (row.ItemId, row.Count));
        if (!actual.SequenceEqual(expected) || Items.Any(item => item.Uid != 0 || !item.Core.IsStackable || item.Core.Count <= 0))
            throw new InvalidDataException("帝国竞技场邮件附件与冻结奖励不一致");
    }

    internal static ImmutableArray<InventoryItem> Materialize(ImmutableArray<ImperialArenaRewardItem> reward, ItemCatalog items, DateTimeOffset now)
    {
        var result = ImmutableArray.CreateBuilder<InventoryItem>();
        foreach (var row in reward)
        {
            var template = items.Get(row.ItemId);
            if (template.Kind != ItemKind.Material || !template.AttachType.Contains("[account]", StringComparison.Ordinal) ||
                template.StackLimit <= 0 || row.Count <= 0 || !template.Expiration.SupportsCreation)
                throw new InvalidDataException("帝国竞技场产物不是可邮件的账号材料");
            for (int remaining = row.Count; remaining > 0;)
            {
                int count = Math.Min(remaining, template.StackLimit);
                if (result.Count == 10) throw new InvalidDataException("帝国竞技场奖励超出邮件附件上限");
                var core = InventoryRewardGrantService.Create(template, count, now);
                result.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 9), core));
                remaining -= count;
            }
        }
        return result.ToImmutable();
    }
}

internal sealed record ImperialArenaDeathCommit(ImperialArenaDeathPlan Plan, uint MailId, bool Duplicate);
internal interface IImperialArenaRunStore
{
    ImperialArenaDeathCommit CommitImperialArenaDeath(AccountOwners.Lease owner, ImperialArenaDeathPlan plan);
}
