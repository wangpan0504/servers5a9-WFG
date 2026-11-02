using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Strongway2019;

internal sealed class Strongway2019RouteService
{
    readonly Strongway2019Catalog activity;
    readonly DungeonCatalog dungeons;
    readonly DungeonRoomCatalog rooms;
    public Strongway2019RouteService(Strongway2019Catalog activity, DungeonCatalog dungeons, DungeonRoomCatalog rooms) =>
        (this.activity, this.dungeons, this.rooms) = (activity, dungeons, rooms);

    public PartyDungeonNotice[] Move(GameSession reporter, byte x, byte y, byte kind,
        Func<SemaphoreSlim, bool>? holdsCommandGate = null)
    {
        if (kind > 1 || reporter.CurrentStrongway2019Match is not { } match || reporter.PartyDungeon is not { } instance) return [];
        var ticket = ReferenceEquals(instance, match.First) ? match.Pair.First : ReferenceEquals(instance, match.Second) ? match.Pair.Second : null;
        if (ticket is null || instance.Members.Length != 4) return [];
        var members = ticket.Party.Members;
        var gates = new[] { ticket.Party.Commands }.Concat(members.OrderBy(member => member.Actor.Owner.Account, StringComparer.Ordinal)
            .Select(member => member.Actor.Session.CommandGate)).Distinct().ToArray();
        var acquired = new List<SemaphoreSlim>();
        try
        {
            foreach (var command in gates)
            {
                if (holdsCommandGate?.Invoke(command) == true) continue;
                if (!command.Wait(0)) return [];
                acquired.Add(command);
            }
            if (!members.Any(member => ReferenceEquals(member.Actor.Session, reporter)) || members.Any(member => !member.IsCurrent() ||
                !ReferenceEquals(member.Actor.Session.PartyDungeon, instance) || !ReferenceEquals(member.Actor.Session.CurrentStrongway2019Match, match))) return [];
            var snapshot = match.Competition.Snapshot;
            if (snapshot.Phase != Strongway2019CompetitionPhase.Running) return [];
            int side = ReferenceEquals(ticket, match.Pair.First) ? 0 : 1;
            var pending = snapshot.PendingRouteChanges.FirstOrDefault(change => change.TargetSide == side);
            if (pending is null) return instance.Move(reporter, x, y, kind);
            var target = new DungeonPoint(x, y);
            var runs = members.Select(member => member.Actor.Session.CaptureStrongway2019Run(instance)).ToArray();
            if (runs.Any(run => run is null)) return [];
            // 待换图只作用于尚未加载的普通地图，不能封死原终点、回访或原有跳转。
            if (kind != 0 || runs.All(run => !run!.IsStrongway2019ReplacementTarget(target)))
                return instance.Move(reporter, x, y, kind);
            if (runs.Any(run => !run!.IsStrongway2019ReplacementTarget(target)) ||
                !activity.RandomMapIds.Contains(pending.MapId) || dungeons.MapPath(pending.MapId) is not { } path) return [];
            // 资源读取在四个owner之外；坐标保持原迷宫目标，候选只提供真实MAP及生成行。
            var resource = rooms.Get(new DungeonMap("map", target, pending.MapId, path));
            uint first = members.Max(member => member.Actor.Session.Strongway2019NextObjectKey);
            if (first > ushort.MaxValue) return [];
            var prepared = members.Select(member => member.Actor.Session.PrepareStrongway2019NextRoom(pending,
                target, activity, resource, (ushort)first)).ToArray();
            if (prepared.Any(row => row is null)) return [];
            return instance.Execute(current =>
            {
                if (current.Length != 4 || current.Any(member => !members.Contains(member)) ||
                    match.Competition.Snapshot.PendingRouteChanges.All(change => !ReferenceEquals(change, pending)) ||
                    members.Select((member, index) => member.Actor.Session.CanApplyStrongway2019NextRoom(prepared[index]!)).Any(valid => !valid)) return [];
                foreach (var (member, index) in members.Select((member, index) => (member, index)))
                    if (member.Actor.Session.ApplyStrongway2019NextRoom(prepared[index]!) is null)
                        throw new InvalidOperationException("强者之路四人换图预验后原Run应用发生变化");
                return instance.Move(reporter, x, y, kind);
            });
        }
        finally { for (int index = acquired.Count - 1; index >= 0; index--) acquired[index].Release(); }
    }
}
