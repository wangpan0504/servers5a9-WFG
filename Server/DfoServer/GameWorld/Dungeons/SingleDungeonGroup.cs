using System.Collections.Immutable;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record SingleDungeonGroup(int Id, ImmutableArray<int> Dungeons, int CharacterLimit, int ResetDay);
