namespace DfoServer.Game.Dungeons;

// 单个准备计划内禁止回绕；由新实例的准备入口选择是否从头重新规划一次。
internal sealed class DungeonObjectKeyOverflowException(string message) : OverflowException(message);
