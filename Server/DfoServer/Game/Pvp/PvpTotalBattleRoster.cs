using System.Collections.Immutable;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Pvp;

// 名册只决定稳定身份及顺序；战斗必须使用完整存档快照，不能拿外观行代替装备或技能。
internal sealed record PvpTotalBattleRoster(PvpTotalTeam Team, PvpTotalRosterSelection Selection,
    ImmutableArray<Character> Characters);
