using System.Collections.Immutable;

namespace DfoServer.GameWorld.Items;

internal sealed record AvatarEmblemTargetDescriptor(int ItemId, ItemKind Kind, ushort OwnFlags16);

internal sealed record AvatarEmblemMaterialDescriptor(int ItemId, ItemKind Kind, ushort SocketMask,
    bool HasMetadata35, ImmutableArray<int> PrimaryIndices, ImmutableArray<int> SecondaryIndices);
