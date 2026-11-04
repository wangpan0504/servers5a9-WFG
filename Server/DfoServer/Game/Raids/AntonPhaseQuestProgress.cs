using System.Collections.Immutable;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Raids;

// 只发布任务接取身份和剩余值，不把可变Session或角色数组暴露给Battle锁内消费者。
internal sealed record AntonQuestBindings(ushort Character, long Epoch, ImmutableArray<ActiveMainQuest> Active);
internal sealed record AntonPhaseQuestSnapshot(ushort Character, string Account, long Revision,
    ImmutableArray<ActiveMainQuest> Active, ImmutableArray<ActiveMainQuest> Bindings);
// null表示历史回执未承诺阶段任务；非null与阶段资格在同一事务持久化，零变化不增加版本。
internal sealed record AntonPhaseQuestProgress(long BeforeRevision, long Revision,
    ImmutableArray<ActiveMainQuest> Before, ImmutableArray<ActiveMainQuest> After)
{
    public ImmutableArray<ActiveMainQuest> Bindings { get; init; } = [];
}
