using DfoServer.Game.Characters;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public FatigueBatteryFillResult FillFatigueBatteries() => owner.Execute(() =>
    {
        if (Selected is not null && !CanAdvanceServiceDay())
            throw new InvalidOperationException("角色有待提交操作，请完成后再填满蓄电池");
        var result = repository.FillFatigueBatteries(owner, Selected);
        if (Selected is { } current)
        {
            var committed = result.Characters.Single(row => row.Id == current.Id);
            Selected = current with { Revision = committed.Revision, Fatigue = committed.Fatigue };
        }
        return result;
    });
}
