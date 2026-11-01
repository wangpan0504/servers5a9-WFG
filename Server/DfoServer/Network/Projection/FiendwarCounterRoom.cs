using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Projection;

// 仅保存本连接最近排出的MAI房间身份；正式实例及存活怪物仍由Session/Battle拥有。
internal sealed record FiendwarCounterRoom(uint Serial, FormalDungeonReceipt Receipt);
