using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Network.Protocol;

namespace DfoServer.Development.Dungeons;

// 每个诊断连接独占；不进入正式实例、持久化或奖励链。
internal sealed class DungeonProbeState(GameSession session) : IGameConnectionActivity
{
    public long? SelectionEpoch { get; set; }
    public DungeonEntryPlan? Plan { get; set; }
    public DungeonMonsterPlan? Monsters { get; set; }
    public DungeonProbeRun? Run { get; set; }
    public int NextObjectKey { get; set; } = 0x100;
    public bool LoadingReplied { get; set; }
    public bool ReturnStarted { get; set; }
    public bool ReturnedToTown { get; set; }
    public bool BlocksTownActions => session.Selected is not null && SelectionEpoch == session.SelectionEpoch && !ReturnedToTown;

    public void Reset()
    {
        Run?.End();
        Run = null;
        Plan = null;
        Monsters = null;
        SelectionEpoch = null;
        LoadingReplied = false;
        ReturnStarted = false;
        ReturnedToTown = false;
        // 同一连接重选角色仍不能立即复用旧对象 key。
    }
}
