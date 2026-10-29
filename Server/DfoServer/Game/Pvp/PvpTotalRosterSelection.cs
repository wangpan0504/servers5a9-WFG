using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Social;

namespace DfoServer.Game.Pvp;

internal sealed record PvpTotalRosterMember(byte RosterIndex, CharacterRosterEntry Character);

// 仅保存客户端已发布行序及稳定身份，不把名册外观当作开战所需的完整角色。
internal sealed record PvpTotalRosterSelection(SocialActor Actor, CharacterRosterSnapshot Roster,
    ImmutableArray<PvpTotalRosterMember> Members)
{
    // 等候房允许编辑；回执不能跨离房、重入或开战发布。
    public PvpArenaMembership? Room { get; init; }
}
