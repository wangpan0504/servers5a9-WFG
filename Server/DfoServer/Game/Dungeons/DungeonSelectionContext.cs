using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonSelectionContext(long Id, long SelectionEpoch, ushort CharacterId, Location OriginPosition, Location ReturnPosition);
internal sealed record DungeonSelectionReceipt(Character Character, long Generation, long SelectionEpoch, long SelectionId);
