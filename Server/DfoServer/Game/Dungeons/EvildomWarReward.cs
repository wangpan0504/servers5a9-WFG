using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Dungeons;

internal sealed record EvildomWarRewardPlan(EvildomWarCompletion Completion, SinglePlayerDungeonRun Run,
    EvildomWarRewardSelection Reward, ImmutableArray<InventoryItem> Items)
{
    public EvildomWarRewardSnapshot Snapshot => new(Completion, Reward, Items);
    public DungeonClearFact Fact => Completion.Facts.Single(fact => fact.Identity.CharacterId == Run.Snapshot.Identity.CharacterId);

    public void Validate()
    {
        var fact = Fact;
        if (Completion.BattleId == Guid.Empty || Completion.Mode > 2 || Reward.Mode != Completion.Mode ||
            Run.ClearFact is not { } actual || Run.HasPendingDeath || actual.Identity != fact.Identity || actual.DungeonId != fact.DungeonId ||
            !actual.ConfirmedDeathEvents.SequenceEqual(fact.ConfirmedDeathEvents) ||
            !Run.Entry.IsTerminalMap(Run.Snapshot.CurrentMap))
            throw new InvalidDataException("魔界奖励必须冻结在同一战役真实王座终点");
        Snapshot.Validate();
    }
}

// 仅在核验真实Run时首次写入；恢复交付读取原快照，不依赖已结束的会话或重新抽样。
internal sealed record EvildomWarRewardSnapshot(EvildomWarCompletion Completion,
    EvildomWarRewardSelection Reward, ImmutableArray<InventoryItem> Items)
{
    public string Rule { get; init; } = EvildomWarRewardPolicy.Rule;

    public void Validate()
    {
        if (Rule is not (EvildomWarRewardPolicy.Rule or EvildomWarRewardPolicy.LegacyRule or EvildomWarRewardPolicy.HiddenRule) ||
            Completion.BattleId == Guid.Empty || Completion.Mode > 2 ||
            Reward.Mode != Completion.Mode || Completion.Facts.IsDefaultOrEmpty || Completion.Facts.Length > 4 ||
            Completion.Facts.Select(fact => fact.Identity.CharacterId).Distinct().Count() != Completion.Facts.Length ||
            Reward.Items.IsDefault || Items.IsDefault ||
            Reward.Items.Length > EvildomWarRewardPolicy.MaximumProducts || Items.Length > EvildomWarRewardPolicy.MaximumProducts ||
            Reward.Items.Any(row => row.Group > 3 || row.ItemId <= 0 || row.Count <= 0))
            throw new InvalidDataException("魔界奖励必须冻结在同一战役真实王座终点");
        if (Rule == EvildomWarRewardPolicy.Rule && Reward.Items.Any(row => row.Group == 1) ||
            Rule == EvildomWarRewardPolicy.HiddenRule && (Reward.Items.Length != 1 || Reward.Items[0].Group != 1))
            throw new InvalidDataException("魔界实验室和王座奖励组不能混用");
        foreach (var item in Items)
        {
            if (item.Uid != 0 || !item.Core.IsStackable && item.Core.Kind != GameWorld.Items.ItemKind.Talisman || item.Core.Count <= 0)
                throw new InvalidDataException("魔界奖励附件不是完整新产物");
            item.Core.Validate(); item.ValidateDetails(allocating: true);
        }
        var expected = Reward.Items.GroupBy(row => row.ItemId).OrderBy(group => group.Key)
            .Select(group => (group.Key, Count: group.Sum(row => (long)row.Count)));
        var delivered = Items.GroupBy(row => row.Core.ItemId).OrderBy(group => group.Key)
            .Select(group => (group.Key, Count: group.Sum(row => (long)row.Core.Count)));
        if (!expected.SequenceEqual(delivered)) throw new InvalidDataException("魔界展示产物与真实附件不一致");
    }
}

internal sealed record EvildomWarRewardCommit(EvildomWarRewardPlan Plan, ImmutableArray<uint> MailIds, bool Duplicate);
internal sealed record EvildomWarHiddenRewardCommit(ImmutableArray<uint> MailIds, bool Duplicate);
internal interface IEvildomWarRewardStore
{
    ImmutableArray<EvildomWarCompletion> LoadUnrewardedEvildomWarHiddenCompletions(AccountOwners.Lease owner, ushort characterId);
    EvildomWarHiddenRewardCommit CommitEvildomWarHiddenReward(AccountOwners.Lease owner, ushort characterId,
        EvildomWarRewardSnapshot snapshot);
    EvildomWarRewardCommit CommitEvildomWarReward(AccountOwners.Lease owner, EvildomWarRewardPlan plan);
    ImmutableArray<uint> RecoverEvildomWarRewards(AccountOwners.Lease owner, ushort characterId);
    ImmutableArray<EvildomWarCompletion> LoadUnrewardedEvildomWarCompletions(AccountOwners.Lease owner, ushort characterId);
    void FreezeRecoveredEvildomWarReward(AccountOwners.Lease owner, ushort characterId, EvildomWarRewardSnapshot snapshot);
}
