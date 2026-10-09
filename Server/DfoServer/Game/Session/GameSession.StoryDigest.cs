using DfoServer.Game.Characters;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public Character? AcceptStoryDigest() => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } before) return null;
        var updated = repository.AdvanceAcceptedStoryLevel(owner,before);
        Selected = updated;
        if (ReferenceEquals(before,updated) || pendingDungeonContinuation is not { } pending) return updated;
        // 剧情接受只改变一个标量；连图等待中的角色快照同步提交结果，保留已经准备的合法路线。
        var prepared = pending.Dungeon;
        if (pending.Entered is null && prepared is not null && ReferenceEquals(prepared.Character,before))
        {
            var entry = prepared.Dungeon.Entry with { CharacterRevision = checked(updated.Revision + 1) };
            prepared.Dungeon.Run.RebasePreparedRevision(entry.CharacterRevision);
            prepared = prepared with { Character = updated, Dungeon = prepared.Dungeon with { Entry = entry } };
        }
        pendingDungeonContinuation = pending with
        {
            Dungeon = prepared,
            Previous = ReferenceEquals(pending.Previous.Character,before) ? pending.Previous with { Character = updated } : pending.Previous,
            Return = pending.Return is { } returning && ReferenceEquals(returning.Character,before) ? returning with { Character = updated } : pending.Return,
            Entered = pending.Entered is { } entered && ReferenceEquals(entered.Character,before) ? entered with { Character = updated } : pending.Entered
        };
        return updated;
    });
}
