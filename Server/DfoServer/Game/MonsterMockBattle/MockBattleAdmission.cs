using System.Collections.Immutable;
using DfoServer.Game.Events;
using DfoServer.GameWorld.MonsterMockBattle;
using DfoServer.Game.Mailbox;

namespace DfoServer.Game.MonsterMockBattle;

internal sealed record MockBattleSpawn(MockBattleCard Card, int MonsterId, uint ObjectKey, uint Team, uint SpawnType = 0, byte RawFlag = 0);

// 由活动准入冻结；普通C16中的对手序号自身不提供任何匹配或付款资格。
internal sealed record MockBattleAdmission(MockBattleWorldDefinition World, MockBattleAccount Before,
    MockBattleAccount Paid, ImmutableArray<MockBattleCard> Lineup, MockBattleOpponent? Opponent,
    ServerEventSchedule Schedule, DateTimeOffset At, MonsterMockBattleCatalog Catalog);

internal sealed record MockBattleVictory(MockBattleAdmission Admission, MockBattleAccount Before,
    MockBattleAccount After, MailboxDelivery? Mail, DfoServer.Game.Inventory.Models.InventoryPlan Inventory);
