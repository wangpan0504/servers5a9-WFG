using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public bool CanPublishNativeScene(FormalDungeonReceipt capture) => owner.Execute(() =>
        Ready && owner.Generation == capture.Generation && SelectionEpoch == capture.SelectionEpoch &&
        Selected?.Id == capture.Character.Id && Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement &&
        formalDungeon is { } dungeon && IsCurrentRaidDungeon(dungeon.Entry) && dungeon.Run.Snapshot.Identity == capture.Snapshot.Identity &&
        dungeon.Run.Snapshot.CurrentMap == capture.Snapshot.CurrentMap);

    public NpcFavorDungeonCapture? CaptureNpcFavorDungeon() => owner.Execute(() =>
        Ready && Selected is not null && Phase is SessionPhase.Dungeon or SessionPhase.Settlement && formalDungeon is { } dungeon &&
        IsCurrentRaidDungeon(dungeon.Entry) &&
        dungeon.Run.ClearFact is { } clear && clear.ClearMap == dungeon.Run.Snapshot.CurrentMap &&
        !dungeon.CharacterDead && !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement &&
        !HasActiveDungeonContinuation && !HasPendingQuestReplay && dungeon.PendingTutorialClear is null
            ? new NpcFavorDungeonCapture(Selected, owner.Generation, SelectionEpoch, dungeon.Entry, dungeon.Run.Snapshot, PartyDungeon, dungeon.PartySlot) : null);

    internal bool CanConfirmNpcFavorAppearance(NpcFavorDungeonCapture capture, NpcFavorAppearance appearance) => owner.Execute(() =>
        CanMutatePartyDungeon && CanPublishNpcFavorAppearance(capture) && ReferenceEquals(Selected, capture.Character) &&
        formalDungeon is { } dungeon && (dungeon.FavorNpc is null || dungeon.FavorNpc == appearance));

    internal bool ConfirmNpcFavorAppearance(NpcFavorDungeonCapture capture, NpcFavorAppearance appearance) => owner.Execute(() =>
    {
        if (!CanConfirmNpcFavorAppearance(capture, appearance)) return false;
        formalDungeon!.FavorNpc = appearance;
        return true;
    });

    public bool CanPublishNpcFavorAppearance(NpcFavorDungeonCapture capture) => owner.Execute(() =>
        CaptureNpcFavorDungeon() is { } current && current.Generation == capture.Generation && current.Epoch == capture.Epoch &&
        current.Snapshot.Identity == capture.Snapshot.Identity && current.Snapshot.CurrentMap == capture.Snapshot.CurrentMap &&
        ReferenceEquals(current.Party, capture.Party));

    public bool HasNpcFavorAppearance(NpcFavorDungeonCapture capture, NpcFavorAppearance? appearance = null) => owner.Execute(() =>
        CanPublishNpcFavorAppearance(capture) && formalDungeon?.FavorNpc is { } saved && (appearance is null || appearance == saved));

    public NpcFavorAppearance? ReadNpcFavorAppearance(NpcFavorDungeonCapture capture) => owner.Execute(() =>
        CanPublishNpcFavorAppearance(capture) ? formalDungeon?.FavorNpc : null);
}
