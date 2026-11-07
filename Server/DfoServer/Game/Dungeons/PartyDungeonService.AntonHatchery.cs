using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed partial class PartyDungeonService
{
    internal PartyDungeonNotice[] TransferAntonHatchery(GameSession leader, uint target, DungeonRoomCatalog rooms,
        Action<string>? rejected = null)
    {
        var party = parties.Current(leader);
        var previous = leader.PartyDungeon;
        if (target is < 221 or > 224 || party is null || previous is null || !parties.IsCurrent(party) ||
            !party.Members.Any(member => member.Slot == party.Leader && ReferenceEquals(member.Actor.Session, leader)) ||
            previous.Members.Length != party.Members.Length || party.Members.Any(member => !previous.Members.Contains(member)))
            return Reject("anton-hatchery-party-mismatch");
        var instance = new PartyDungeonInstance(Guid.NewGuid(), party, store);
        uint firstKey = party.Members.Max(member => Math.Max(member.Actor.Session.Selected?.DungeonState?.NextObjectKey ?? 65536u,
            (uint)member.Actor.Character.Id + 1));
        var prepared = new List<(PartyMember Member, GameSession.PartyDungeonPreparation Plan)>();
        try
        {
            // 解析资源在共享实例及整队owner之外完成，提交前再复核完整来源。
            foreach (var member in party.Members.OrderBy(member => member.Slot == party.Leader ? -1 : member.Slot))
            {
                var plan = member.Actor.Session.PrepareAntonHatchery(target, rooms, instance.Id, firstKey,
                    prepared.FirstOrDefault().Plan);
                if (plan is null) return Reject(member.Actor.Session.LastFormalDungeonRejection ?? "anton-hatchery-prepare-failed");
                prepared.Add((member, plan));
            }
            return previous.Execute<PartyDungeonNotice[]>(current =>
            {
                if (!parties.IsCurrent(party) || !previous.CanTransferAntonHatchery(party) ||
                    current.Length != party.Members.Length || current.Any(member => !party.Members.Contains(member))) return Reject("anton-hatchery-party-not-ready");
                var reservations = prepared.Select(row => row.Member.Actor.Session.ReservePartyDungeon(row.Plan)).ToArray();
                if (reservations.Any(row => row is null)) return Reject("anton-hatchery-reservation-changed");
                using var raidRun = GameSession.ReserveRaidPartyRun(prepared.Select(row => row.Plan).ToArray(), out bool required);
                if (!required || raidRun is null) return Reject("anton-hatchery-raid-run-changed");
                var committed = store.CommitPartyDungeonEntry(reservations.Select(row => row!).ToArray());
                var notices = prepared.Select((row, index) => new PartyDungeonNotice(PartyDungeonNoticeKind.Entry,
                    row.Member, party.Members, row.Member.Actor.Session.InstallPartyDungeon(row.Plan,
                        committed[index], reservations[index]!, instance), SceneTransition: true)).ToArray();
                raidRun.Install();
                previous.ClearCards();
                return notices;
            });
        }
        finally
        {
            foreach (var row in prepared) row.Member.Actor.Session.DiscardPartyPreparation(row.Plan);
        }
        PartyDungeonNotice[] Reject(string reason) { rejected?.Invoke(reason); return []; }
    }
}
