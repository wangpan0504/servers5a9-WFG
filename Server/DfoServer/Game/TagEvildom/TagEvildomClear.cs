using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.TagEvildom;

internal sealed record TagEvildomClearFact(DungeonRunIdentity Identity, Guid EventId, TagEvildomAdmission Admission);

// 冻结在真实终点的同一准备对象上；数据库忙、回执丢失或发送失败均复用此对象，不重新抽奖或改日周归属。
internal sealed record TagEvildomClearPlan(SinglePlayerDungeonRun Run, PreparedDungeonDeath Death,
    TagEvildomRewardSelection Reward, ImmutableArray<InventoryItem> Items, long Created, long ElapsedMilliseconds)
{
    public TagEvildomClearFact Fact => Death.TagEvildomClear ?? throw new InvalidDataException("再战奖励缺少真实终点候选");
    public TagEvildomPeriod Period => TagEvildomPeriod.At(DateTimeOffset.FromUnixTimeSeconds(Created));
    public void Validate()
    {
        var fact = Fact;
        fact.Admission.Validate();
        if ((!Run.IsPendingDeath(Death) && !Run.IsConfirmedDeath(Death)) ||
            !ReferenceEquals(Run.TagEvildomAdmission, fact.Admission) || fact.EventId != Death.Death.EventId ||
            fact.Identity != Death.Result.Snapshot.Identity || fact.Identity.CharacterId != fact.Admission.Primary.Id ||
            fact.Identity.InstanceId == Guid.Empty || fact.Identity.TaskAttempt <= 0 ||
            Reward.DungeonId != fact.Admission.DungeonId || Reward.ResourceKey != fact.Admission.ResourceKey || Reward.DefinitionId <= 0 ||
            Created < fact.Admission.AdmittedAt || ElapsedMilliseconds < 0 || Items.IsDefaultOrEmpty || Items.Length > 10 ||
            Reward.Items.IsDefaultOrEmpty || Reward.Items.Length > 10)
            throw new InvalidDataException("再战冻结奖励与真实准入、终点或耗时不一致");
        foreach (var item in Items)
        {
            if (item.Uid != 0 || !item.Core.IsStackable || item.Core.Count <= 0) throw new InvalidDataException("再战附件必须是完整新产物");
            item.Core.Validate(); item.ValidateDetails(allocating: true);
        }
        var actual = Items.GroupBy(item => item.Core.ItemId).OrderBy(group => group.Key)
            .Select(group => (group.Key, Count: group.Sum(item => (long)item.Core.Count)));
        var expected = Reward.Items.OrderBy(item => item.ItemId).Select(item => (item.ItemId, Count: (long)item.Count));
        if (!actual.SequenceEqual(expected)) throw new InvalidDataException("再战真实附件与冻结产物不一致");
    }
}

internal sealed record TagEvildomClearCommit(TagEvildomClearPlan Plan, uint MailId, bool Duplicate);
internal interface ITagEvildomClearStore
{
    TagEvildomClearCommit CommitTagEvildomClear(AccountOwners.Lease owner, TagEvildomClearPlan plan);
}
