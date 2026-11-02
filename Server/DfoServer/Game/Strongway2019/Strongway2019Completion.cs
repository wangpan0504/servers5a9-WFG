using System.Collections.Immutable;
using DfoServer.Game.Accounts;

namespace DfoServer.Game.Strongway2019;

internal sealed record Strongway2019Recovery(bool Changed, ImmutableArray<uint> MailIds);

// 只承载本活动服务端已鉴权终局；恢复不接收客户端资格或旧Run副本。
internal interface IStrongway2019CompletionStore
{
    void FreezeStrongway2019Completions(IReadOnlyList<(AccountOwners.Lease Owner, Strongway2019RewardPlan Plan)> plans);
    void FreezeStrongway2019MatchingCheckpoint(AccountOwners.Lease reporter, Strongway2019Competition competition,
        IReadOnlyList<(string Account, ushort Character)> members, DfoServer.Game.Inventory.Models.InventoryItem item);
    Strongway2019Recovery RecoverStrongway2019Completion(AccountOwners.Lease owner, ushort character);
    ImmutableArray<uint> RecoverStrongway2019Rewards(AccountOwners.Lease owner, ushort character);
}
