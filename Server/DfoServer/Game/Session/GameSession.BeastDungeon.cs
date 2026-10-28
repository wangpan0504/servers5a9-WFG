using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 选图候选列表与次数同源：复用上面同一套门禁，避免两处判断漂移。
    public int[] BeastSelectionCandidates() =>
        [.. BeastSelectionCounts().Select(entry => (int)entry.Dungeon)];

    public (uint Dungeon, byte Remaining)[] BeastSelectionCounts() => owner.Execute<(uint,byte)[]>(() =>
    {
        // 魔兽世界地图46（worldmap/beast_metrocenter.wdm）的气球编号就是 5335–5339，次数必须按这五个下发。
        // 区域不限定2：频道落点与玩家自行走动都会换区域，取城内的整座城镇51判断。
        if (!Ready || Phase != SessionPhase.MapSelect || Selected is not { Position: { Town:51 } } character ||
            ChannelEntry?.Type != ChannelAreaLanding.BeastType || !completed.Contains(5927)) return [];
        int day = BeastDungeonRotation.ServiceDay(IncountNow());
        return Enumerable.Range(BeastDungeonRotation.FirstGatewayDungeonId,5).Select(dungeon =>
        {
            int used = repository.LoadBeastDungeonUsed(owner,character.Id,day,dungeon);
            return ((uint)dungeon,(byte)(BeastDungeonRotation.Contains(day,dungeon)
                ? Math.Clamp(BeastDungeonRotation.DailyLimit-used,0,BeastDungeonRotation.DailyLimit) : 0));
        }).ToArray();
    });
}
