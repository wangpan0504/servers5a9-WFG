using DfoServer.Game.Dungeons;
using DfoServer.Game.TagEvildom;

namespace DfoServer.Game.Session;

internal sealed record TagEvildomScheduledRevival(ushort CharacterId, long Generation, long SelectionEpoch,
    TagEvildomRevivalTimeout Timeout);
internal sealed record TagEvildomLifeReceipt(ushort CharacterId, long Generation, long SelectionEpoch,
    DungeonRunIdentity Run, TagEvildomLifeResult Result)
{
    public System.Collections.Immutable.ImmutableArray<uint> ImperialArenaMails { get; init; } = [];
}

internal sealed partial class GameSession
{
    // 这些用例只消费已经安装的正式TAG Run，不由生命命令临时构造双角色实例。
    public bool HasFormalTagEvildom => owner.Execute(() => formalDungeon?.Run.TagEvildomLife is not null);
    public TagEvildomLifeReceipt? SwitchFormalTagEvildom(byte ordinal) => owner.Execute(() =>
        CanChangeTagEvildomLife() ? TagLifeReceipt(formalDungeon!.Run.SwitchTagEvildom(formalDungeon.Run.Snapshot.Identity, ordinal,
            (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds())) : null);

    public TagEvildomLifeReceipt? ReportFormalTagEvildomDeath(byte ordinal) => owner.Execute(() =>
    {
        if (!CanChangeTagEvildomLife()) return null;
        var dungeon = formalDungeon!;
        if (dungeon.PendingImperialArenaDefeat is { } pending)
        {
            if (ordinal != pending.Ordinal) return null;
            var replay = TagLifeReceipt(pending.Result);
            dungeon.PendingImperialArenaDefeat = null;
            return replay;
        }
        var result = dungeon.Run.ReportTagEvildomDeath(dungeon.Run.Snapshot.Identity, ordinal,
            (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds());
        if (result?.Snapshot.Defeated == true && dungeon.ImperialArenaEntry is not null)
            dungeon.PendingImperialArenaDefeat = (ordinal, result);
        var receipt = TagLifeReceipt(result);
        dungeon.PendingImperialArenaDefeat = null;
        return receipt;
    });

    public TagEvildomScheduledRevival? CurrentTagEvildomRevival() => owner.Execute(() =>
        CanChangeTagEvildomLife() && formalDungeon!.Run.CurrentTagEvildomRevival() is { } timeout
            ? new TagEvildomScheduledRevival(Selected!.Id, owner.Generation, SelectionEpoch, timeout) : null);

    public TagEvildomLifeReceipt? ReviveFormalTagEvildom(TagEvildomScheduledRevival expected, long now) => owner.Execute(() =>
        CurrentTagEvildomRevival() == expected ? TagLifeReceipt(formalDungeon!.Run.ReviveTagEvildom(expected.Timeout, now)) : null);

    public bool CanPublishTagEvildomLife(TagEvildomLifeReceipt receipt) => owner.Execute(() =>
        Ready && Selected?.Id == receipt.CharacterId && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        formalDungeon is { } dungeon && dungeon.Run.Snapshot.Identity == receipt.Run &&
        ReferenceEquals(dungeon.Run.TagEvildomLife, receipt.Result.Snapshot) && !receipt.Result.Snapshot.Cancelled);

    bool CanChangeTagEvildomLife() => ChannelEntry?.Type is 39 or 53 && PartyDungeon is null && CanChangeDungeonLife() &&
        formalDungeon!.Run.TagEvildomLife is { Cancelled: false };

    TagEvildomLifeReceipt? TagLifeReceipt(TagEvildomLifeResult? result)
    {
        if (result is null) return null;
        var dungeon = formalDungeon!;
        var mails = result.Snapshot.Defeated && dungeon.ImperialArenaEntry is { } entry
            ? entry.Store.CompleteImperialArenaRuns(owner, dungeon.Run.Snapshot.Identity.InstanceId) : [];
        return new(Selected!.Id, owner.Generation, SelectionEpoch, dungeon.Run.Snapshot.Identity, result) { ImperialArenaMails = mails };
    }
}
