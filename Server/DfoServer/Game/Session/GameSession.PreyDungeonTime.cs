using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal uint? PreyDungeonSeconds(FormalDungeonReceipt scene) => owner.Execute<uint?>(() =>
        Ready && Phase == SessionPhase.Dungeon && owner.Generation == scene.Generation && SelectionEpoch == scene.SelectionEpoch &&
        Selected?.Id == scene.Character.Id && formalDungeon is { Entry.RaidEntry: { Kind: RaidKind.Prey, PreyGuide: false } entry } dungeon &&
        dungeon.Run.Snapshot.Identity == scene.Snapshot.Identity &&
        raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid && entry.InstanceId == raid.Battle.Instance
            ? raid.Battle.PreyDungeonSeconds(raid.Actor, scene.Snapshot.Identity) : null);
}
