using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Strongway2019;

internal sealed record Strongway2019MatchingEntry(Strongway2019Match Match,
    ImmutableArray<PartyDungeonNotice> Notices);

// 两个原四人实例共用一次八人入场事务，资源准备仍沿每个Session的原正式副本链。
internal sealed class Strongway2019MatchingEntryService(Strongway2019MatchingService matching, PartyService parties,
    IPartyDungeonStore partyStore, IStrongway2019MatchingStore store, TimeProvider? clock = null)
{
    readonly TimeProvider time = clock ?? TimeProvider.System;

    public Strongway2019MatchingEntry? Enter(Strongway2019MatchingPair pair, DungeonCatalog dungeons,
        DungeonRoomCatalog rooms, DungeonMonsterDropCatalog? drops, ItemCatalog? items, EpicPieceCatalog? pieces,
        Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? prepareDrops = null,
        Func<SemaphoreSlim, bool>? holdsCommandGate = null) =>
        matching.WithReservedCommands<Strongway2019MatchingEntry?>(pair, () =>
        {
            var teams = new[] { pair.First, pair.Second };
            var instances = teams.Select(ticket => new PartyDungeonInstance(Guid.NewGuid(), ticket.Party, partyStore)).ToArray();
            var prepared = new List<(int Side, PartyMember Member, GameSession.PartyDungeonPreparation Plan)>();
            bool committed = false;
            try
            {
                if (!dungeons.Get(5360).TryGetDesignatedDifficulty(out byte difficulty)) return null;
                for (int side = 0; side < teams.Length; side++)
                {
                    var ticket = teams[side];
                    uint firstKey = ticket.Party.Members.Max(member => Math.Max(
                        member.Actor.Session.Selected?.DungeonState?.NextObjectKey ?? 65536u, (uint)member.Actor.Character.Id + 1));
                    GameSession.PartyDungeonPreparation? shared = null;
                    foreach (var member in ticket.Party.Members.OrderBy(member => member.Slot == ticket.Party.Leader ? 0 : 1))
                    {
                        var admission = ticket.Admissions.Single(row => ReferenceEquals(row.Character, member.Actor.Session.Selected));
                        var plan = member.Actor.Session.PreparePartyDungeon(5360, difficulty, dungeons, rooms, drops, items, pieces,
                            prepareDrops, instances[side].Id, firstKey, shared, memberCount: checked((byte)ticket.Party.Members.Length), strongwayAdmission: admission);
                        if (plan is null) return null;
                        shared ??= plan;
                        prepared.Add((side, member, plan));
                    }
                }
                var owners = prepared.Select(row => (row.Member.Actor.Owner, row.Member.Actor.Character.Id)).ToArray();
                return AccountOwners.Lease.ExecuteRaidGroup(owners, 8, () => matching.CommitReserved<Strongway2019MatchingEntry?>(pair, () =>
                {
                    if (teams.Any(ticket => !parties.IsCurrent(ticket.Party))) return null;
                    var reservations = prepared.Select(row => row.Member.Actor.Session.ReservePartyDungeon(row.Plan)).ToArray();
                    if (reservations.Any(row => row is null)) return null;
                    var required = new InventoryEventPrecondition(pair.First.Admissions[0].Schedule, time);
                    var rows = reservations.Select(row => row! with { Charge = row!.Charge with { RequiredEvent = required } }).ToArray();
                    var saved = store.CommitStrongway2019MatchingEntry(rows, required);
                    committed = true;
                    if (saved.Length != prepared.Count) throw new InvalidOperationException("首期八人入场提交回执数量不一致");
                    var notices = prepared.Select((row, index) => new PartyDungeonNotice(PartyDungeonNoticeKind.Entry,
                        row.Member, teams[row.Side].Party.Members,
                        row.Member.Actor.Session.InstallPartyDungeon(row.Plan, saved[index], rows[index], instances[row.Side]))).ToImmutableArray();
                    bool Held(SemaphoreSlim command) => teams.Any(ticket => ReferenceEquals(ticket.Party.Commands, command) ||
                        ticket.Party.Members.Any(member => ReferenceEquals(member.Actor.Session.CommandGate, command)));
                    var match = matching.Bind(pair, instances[0], instances[1], Held) ??
                        throw new InvalidOperationException("首期八人正式提交后原Pair或Run绑定发生变化");
                    return new Strongway2019MatchingEntry(match, notices);
                }, null));
            }
            finally
            {
                foreach (var row in prepared) row.Member.Actor.Session.DiscardPartyPreparation(row.Plan);
                if (!committed) matching.Release(pair);
            }
        }, null, holdsCommandGate);
}
