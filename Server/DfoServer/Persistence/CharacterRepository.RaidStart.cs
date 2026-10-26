using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IRaidStartStore
{
    public ImmutableArray<Character> CommitRaidStart(RaidKind kind, RaidDefinition definition, Guid instance, IReadOnlyList<RaidStartReservation> reservations) =>
        RaidOwnerGroup.Execute(kind, reservations.Select(r => (r.Actor.Owner, r.Expected.Id)).ToArray(), () =>
        {
            if (instance == Guid.Empty || definition.Type != (byte)kind ||
                definition.Resources?.Script.WaitingRoom is not { } room ||
                reservations.Count < definition.StartMinimum || reservations.Count > definition.MemberMax ||
                reservations.Any(r => !r.Actor.Session.MatchesRaidStart(r) ||
                    RaidTypes.FromChannelType(r.Actor.Session.ChannelEntry?.Type ?? -1) != (byte)kind ||
                    r.Actor.Session.ChannelEntry?.Identity != r.Actor.Channel ||
                    r.Position.Town != room.Town || r.Position.Area != room.Area))
                throw new InvalidOperationException("攻坚开战成员快照已变化");
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitRaidStart");
            using var tx = write.Transaction;
            var committed = ImmutableArray.CreateBuilder<Character>(reservations.Count);
            foreach (var r in reservations)
                committed.Add(SaveInTransaction(db, tx, r.Actor.Owner, r.Expected, r.Position, r.Expected.Return,
                    new(r.Inventory.Before, r.Inventory.After, (kind == RaidKind.Anton ? "anton_raid_start:" : "luke_raid_start:") + instance.ToString("N"))));
            tx.Commit();
            return committed.ToImmutable();
        });
}
