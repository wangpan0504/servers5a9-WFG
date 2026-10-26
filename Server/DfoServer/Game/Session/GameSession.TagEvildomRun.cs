using DfoServer.Game.Dungeons;
using DfoServer.Game.TagEvildom;

namespace DfoServer.Game.Session;

internal sealed record TagEvildomScheduledRevival(ushort CharacterId, long Generation, long SelectionEpoch,
    TagEvildomRevivalTimeout Timeout);
internal sealed record TagEvildomLifeReceipt(ushort CharacterId, long Generation, long SelectionEpoch,
    DungeonRunIdentity Run, TagEvildomLifeResult Result);

internal sealed partial class GameSession
{
    // 这些用例只消费已经安装的正式TAG Run，不由生命命令临时构造双角色实例。
    public bool HasFormalTagEvildom => owner.Execute(() => formalDungeon?.Run.TagEvildomLife is not null);
    public TagEvildomLifeReceipt? SwitchFormalTagEvildom(byte ordinal) => owner.Execute(() =>
        CanChangeTagEvildomLife() ? TagLifeReceipt(formalDungeon!.Run.SwitchTagEvildom(formalDungeon.Run.Snapshot.Identity, ordinal)) : null);

    public TagEvildomLifeReceipt? ReportFormalTagEvildomDeath(byte ordinal) => owner.Execute(() =>
        CanChangeTagEvildomLife() ? TagLifeReceipt(formalDungeon!.Run.ReportTagEvildomDeath(formalDungeon.Run.Snapshot.Identity,
            ordinal, (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds())) : null);

    public TagEvildomScheduledRevival? CurrentTagEvildomRevival() => owner.Execute(() =>
        CanChangeTagEvildomLife() && formalDungeon!.Run.CurrentTagEvildomRevival() is { } timeout
            ? new TagEvildomScheduledRevival(Selected!.Id, owner.Generation, SelectionEpoch, timeout) : null);

    public TagEvildomLifeReceipt? ReviveFormalTagEvildom(TagEvildomScheduledRevival expected, long now) => owner.Execute(() =>
        CurrentTagEvildomRevival() == expected ? TagLifeReceipt(formalDungeon!.Run.ReviveTagEvildom(expected.Timeout, now)) : null);

    public bool CanPublishTagEvildomLife(TagEvildomLifeReceipt receipt) => owner.Execute(() =>
        Ready && Selected?.Id == receipt.CharacterId && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        formalDungeon is { } dungeon && dungeon.Run.Snapshot.Identity == receipt.Run &&
        ReferenceEquals(dungeon.Run.TagEvildomLife, receipt.Result.Snapshot) && !receipt.Result.Snapshot.Cancelled);

    bool CanChangeTagEvildomLife() => ChannelEntry?.Type == 53 && PartyDungeon is null && CanChangeDungeonLife() &&
        formalDungeon!.Run.TagEvildomLife is { Cancelled: false };

    TagEvildomLifeReceipt? TagLifeReceipt(TagEvildomLifeResult? result) => result is null ? null :
        new(Selected!.Id, owner.Generation, SelectionEpoch, formalDungeon!.Run.Snapshot.Identity, result);
}
