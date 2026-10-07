namespace DfoServer.Network.Protocol;

// 连接上的可选活动边界；领域角色状态仍由 GameSession 拥有。
internal interface IGameConnectionActivity
{
    bool BlocksTownActions { get; }
    // 返回选角或关闭连接时清理本活动，允许原 owner 已过期；幂等且不写库、发包或释放其他连接状态。
    void Reset();
}
