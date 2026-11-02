using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 只在原死亡发生时核验期限；排队等待writer不能撤销已确认的合法死亡。
    internal sealed class FiendwarGuideDeath
    {
        readonly SinglePlayerDungeonRun run;
        readonly DungeonEntryPlan entry;
        readonly PreparedDungeonDeath death;
        readonly RaidActor actor;
        readonly DungeonRunIdentity identity;
        internal DateTimeOffset CapturedAt { get; }

        private FiendwarGuideDeath(SinglePlayerDungeonRun run, PreparedDungeonDeath death,
            RaidActor actor, DateTimeOffset at)
        {
            this.run=run;entry=run.Entry;this.death=death;this.actor=actor;
            identity=death.Death.Identity;CapturedAt=at;
        }

        internal static FiendwarGuideDeath? Capture(AntonRaidBattle battle, RaidActor actor,
            SinglePlayerDungeonRun dungeon, PreparedDungeonDeath death)
        {
            lock (battle.gate)
            {
                var at=battle.lukeClock.GetUtcNow();
                if (battle.Kind != RaidKind.Fiendwar || battle.fiendwarDefinition is not { Guide: true } ||
                    dungeon.Entry.RaidEntry is not { Kind: RaidKind.Fiendwar } entry ||
                    entry.InstanceId != battle.Instance || entry.Phase != battle.current.Phase ||
                    !dungeon.IsPendingDeath(death) || death.Death.Identity != dungeon.Snapshot.Identity ||
                    !battle.dungeonRuns.TryGetValue(death.Death.Identity.InstanceId,out var run) ||
                    run.Actors.Length != 1 || run.Active.Count != 1 || !battle.CanRegisterFiendwarSpawn(actor,dungeon))
                    return null;
                return new(dungeon,death,actor,at);
            }
        }

        // Actor不可变引用携带原Owner/Session/selection身份；不重读可变战场或wall clock。
        internal bool Matches(SinglePlayerDungeonRun dungeon, PreparedDungeonDeath prepared) =>
            ReferenceEquals(run,dungeon) && ReferenceEquals(entry,dungeon.Entry) && ReferenceEquals(death,prepared) &&
            dungeon.IsPendingDeath(prepared) && identity==prepared.Death.Identity && identity==dungeon.Snapshot.Identity &&
            actor.IsCurrent && actor.Character==identity.CharacterId;
    }

    internal FiendwarGuideDeath? CaptureFiendwarGuideDeath(RaidActor actor, SinglePlayerDungeonRun dungeon,
        PreparedDungeonDeath death) => FiendwarGuideDeath.Capture(this,actor,dungeon,death);
}
