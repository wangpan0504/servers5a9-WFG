using DfoServer.Game.TagEvildom;

namespace DfoServer.Game.Session;

internal sealed record TagEvildomPublishedPair(TagEvildomCharacters Characters,
    TagEvildomAppearance PrimaryAppearance, TagEvildomAppearance StandbyAppearance);

internal sealed partial class GameSession
{
    TagEvildomPublishedPair? tagEvildomPair;

    public bool MarkTagEvildomPairPublished(ITagEvildomConfigurationStore store, TagEvildomCharacters characters,
        TagEvildomAppearance primary, TagEvildomAppearance standby, DateTimeOffset now) => owner.Execute(() =>
    {
        if (!CanPublishTagEvildomCharacters(store, characters) || !primary.Matches(characters.Primary, now) ||
            !standby.Matches(characters.Standby, now)) return false;
        tagEvildomPair = new(characters, primary, standby);
        return true;
    });

    // 已发布的双Actor是否仍可直接用于入场；入场前据此决定要不要就地重建一次投影。
    public bool HasCurrentTagEvildomPair(ITagEvildomConfigurationStore store, DateTimeOffset now) =>
        CurrentTagEvildomPair(store, now) is not null;

    // 入场使用真正发完1393→1394的双Actor资料，不重新加载一份客户端从未安装的备用快照。
    TagEvildomCharacters? CurrentTagEvildomPair(ITagEvildomConfigurationStore store, DateTimeOffset now)
    {
        if (!Ready || Selected is not { } current || tagEvildomPair is not { } published || ChannelEntry?.Type != 53 ||
            published.Characters.Projection.Generation != owner.Generation || published.Characters.Projection.Epoch != SelectionEpoch ||
            published.Characters.Version != tagEvildomCharacterVersion ||
            !ReferenceEquals(published.Characters.Projection.Roster, selectedRoster)) return null;
        // 1394只安装备用角色：主角由客户端已持有的真实Actor提供。走动、修理、金币与体力恢复只改动主角
        // 自己的记录，不改变已装好的双Actor；按整条记录判等会让一次装备修理把投影判死，逼玩家重登。
        // 真正让已发布资料过时的只有主角身份、账号钱包版本和备用投影的到期。
        if (current.Id != published.Characters.Primary.Id ||
            current.Inventory.AccountWallet.Revision != published.PrimaryAppearance.WalletRevision ||
            !published.StandbyAppearance.Matches(published.Characters.Standby, now)) return null;
        var rebased = published.Characters with { Primary = current };
        return store.MatchesTagEvildomCharacters(owner, rebased) ? rebased : null;
    }
}
