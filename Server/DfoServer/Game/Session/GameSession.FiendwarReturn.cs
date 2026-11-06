using DfoServer.Game.Raids;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    Location FiendwarReturnPosition(int area) => Selected is { Position.Town: 57 } character &&
        towns.Spawn(character.Position with { Area = checked((byte)area) }, completed) is { } position
            ? position : throw new InvalidDataException("漩涡系统退场缺少有效城镇落点");

    internal bool NeedsFiendwarFinalReturn() => owner.Execute(() =>
        Ready && raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Fiendwar } raid &&
        raid.Battle.View.Fiendwar is { Cleared: true } && Selected is { Position.Town: 57 } character &&
        (character.Position.Area != raid.Battle.FiendwarBaseArea ||
            raid.Battle.FiendwarPosition(character.Id) != raid.Battle.FiendwarBaseArea));

    internal TownTravelReceipt? ReturnFromFiendwarBattlefield() => owner.Execute(() =>
    {
        if (!NeedsFiendwarFinalReturn() || !CanChangeTownParty || Phase != SessionPhase.Town ||
            PartyDungeon is not null || raidCombat is not { } raid || Selected is not { } character) return null;
        using var movement = raid.Battle.ReserveFiendwarReturn(raid.Actor, null);
        if (movement is null) return null;
        var position = FiendwarReturnPosition(movement.Destination);
        if (character.Position != position) Selected = repository.Save(owner, character, position, character.Return);
        var change = movement.Install();
        npcTownVisit = null;
        InvalidateImageCommunication();
        InvalidateExpertJobStore();
        return new TownTravelReceipt(Selected!, owner.Generation, SelectionEpoch) { RaidBattleChange = change };
    });
}
