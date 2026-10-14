using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    CubeContractTick? cubeContractTick;

    public CubeContractTick? PrepareCubeContractTick(CubeContractService service, long now, long timestamp, long frequency) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || Phase is not (SessionPhase.Loading or SessionPhase.Dungeon) ||
            formalDungeon is not { CharacterDead: false } dungeon || service.Consumption(character.Inventory, now) is not { } option)
            return cubeContractTick = null;
        Guid instance = dungeon.Run.Snapshot.Identity.InstanceId;
        sbyte type = character.Inventory.CubeContract.CubeType;
        if (cubeContractTick is { } previous && previous.InstanceId == instance && previous.CharacterId == character.Id &&
            previous.SelectionEpoch == SelectionEpoch && previous.CubeType == type)
            return previous;
        if (Phase != SessionPhase.Dungeon) return cubeContractTick = null;
        return cubeContractTick = new(Guid.NewGuid(), instance, character.Id, SelectionEpoch, type, option.ItemId, option.ConsumeCount,
            option.IntervalMilliseconds, checked(timestamp + option.IntervalMilliseconds * frequency / 1000));
    });

    public InventoryResult ConsumeCubeContractTick(CubeContractTick expected, CubeContractService service,
        long now, long timestamp, long frequency)
    {
        bool Current() => ReferenceEquals(cubeContractTick, expected) && SelectionEpoch == expected.SelectionEpoch &&
            Selected?.Id == expected.CharacterId && formalDungeon is { CharacterDead: false } dungeon &&
            dungeon.Run.Snapshot.Identity.InstanceId == expected.InstanceId && timestamp >= expected.DueTimestamp;
        var result = PlanAndCommitInventory(character => service.Consume(character, expected.CubeType, now), 1,
            requireTown: false, allowSettlement: false, requiredPhase: SessionPhase.Dungeon, additionalGuard: Current);
        if (result.Accepted) owner.Execute(() =>
        {
            if (Current()) cubeContractTick = expected with
            { DueTimestamp = checked(timestamp + expected.IntervalMilliseconds * frequency / 1000) };
            return true;
        });
        return result;
    }
}
