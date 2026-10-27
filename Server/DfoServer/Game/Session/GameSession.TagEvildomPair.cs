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

    // 入场使用真正发完1393→1394的双Actor资料，不重新加载一份客户端从未安装的备用快照。
    TagEvildomCharacters? CurrentTagEvildomPair(ITagEvildomConfigurationStore store, DateTimeOffset now)
    {
        if (!Ready || Selected is not { } current || tagEvildomPair is not { } published || ChannelEntry?.Type != 53 ||
            published.Characters.Projection.Generation != owner.Generation || published.Characters.Projection.Epoch != SelectionEpoch ||
            published.Characters.Version != tagEvildomCharacterVersion ||
            !ReferenceEquals(published.Characters.Projection.Roster, tagEvildomRoster)) return null;
        var original = published.Characters.Primary;
        // 普通走动/房间出口只改位置与revision，不改Actor资料；每日服务器道具补充只改Inventory.ServerItems，
        // 同属跨特性日切而不是玩家资料变化。其余背包、技能与外观字段仍须逐项一致，改变时重新完成真实入城投影。
        var comparable = current with
        {
            Revision = original.Revision, Position = original.Position, Return = original.Return,
            Inventory = current.Inventory with { ServerItems = original.Inventory.ServerItems }
        };
        if (comparable != original ||
            !published.PrimaryAppearance.Matches(current, now) || !published.StandbyAppearance.Matches(published.Characters.Standby, now)) return null;
        var rebased = published.Characters with { Primary = current };
        return store.MatchesTagEvildomCharacters(owner, rebased) ? rebased : null;
    }
}
