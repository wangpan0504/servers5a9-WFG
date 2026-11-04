using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Projection;

// 仅保存本连接实际写出的场景与观测水位；对象许可及消费仍归原共享副本。
internal sealed record AntonNativeRoom(uint Serial, FormalDungeonReceipt Receipt)
{
    internal uint LastSequence { get; set; }
}
