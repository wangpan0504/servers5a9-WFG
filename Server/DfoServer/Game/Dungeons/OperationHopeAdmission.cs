using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 原选角中的一次入场选择；重试复用迷宫、任务和接取身份，不再次抽取。
internal sealed record OperationHopeAdmission(OperationHopeCatalog Catalog, int MazeOrdinal,
    ActiveMainQuest Acceptance, long SourceRevision);
