using System.Collections.Immutable;
using System.Text.Json.Serialization;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Marriage;

internal sealed record MarriageMemberState
{
    public int LovePoints { get; init; }
    public int LoveDay { get; init; }
    public int CapsuleGauge { get; init; }
    public int CapsulesOwned { get; init; }
    public int CapsulesTaken { get; init; }
    public int CapsulesPending { get; init; }
    public int CapsuleDay { get; init; }
}

internal sealed record MarriageInvitation
{
    public ushort CharacterId { get; init; }
    public string Name { get; init; } = "";
    public byte State { get; init; }
    public long SentAt { get; init; }
    public ushort InviterId { get; init; }
}

internal sealed record MarriageGuestGift
{
    public Guid Id { get; init; }
    public ushort CharacterId { get; init; }
    public string Name { get; init; } = "";
    public string Text { get; init; } = "";
    public uint Gold { get; init; }
    public long SentAt { get; init; }
    public bool Settled { get; init; }
}

// 场景身份由已提交的婚礼入场安装到当前角色快照；重登回普通地区后重新按请柬入场。
internal sealed record MarriageHallContext(Guid RelationshipId, Guid CeremonyId, byte HallArea, byte ServerIndex);

internal sealed record MarriageWeddingState
{
    public Guid Id { get; init; }
    public int Type { get; init; }
    public ushort PayerId { get; init; }
    public uint PaidGold { get; init; }
    public int PaidTicketId { get; init; }
    public int PaidTicketCount { get; init; }
    public long ReservedAt { get; init; }
    public long StartsAt { get; init; }
    public long EndsAt { get; init; }
    public byte State { get; init; }
    public int SelectionSeconds { get; init; }
    public byte HallArea { get; init; }
    public byte ServerIndex { get; init; }
    public byte ChannelNumber { get; init; }
    public ImmutableDictionary<ushort, int> InviteRemaining { get; init; } = ImmutableDictionary<ushort, int>.Empty;
    public ImmutableArray<MarriageInvitation> Invitations { get; init; } = [];
    public ImmutableArray<MarriageGuestGift> Gifts { get; init; } = [];
}

internal sealed record MarriageTreeState
{
    public int Seed { get; init; }
    public long Expiry { get; init; }
    public int Growth { get; init; }
    public int WaterDay { get; init; }
    public int Waters { get; init; }
}

internal readonly record struct MarriageFurnitureStack(int ItemId, int Count);
internal sealed record MarriageComment(uint Id, ushort AuthorId, string Name, long Timestamp, string Text);

// 聚合版本独立于角色资产版本；离线配偶的角色快照不因共享关系更新而被改写。
internal sealed record MarriageRecord(Guid Id, uint ContextId, long Revision, ushort FirstId, ushort SecondId, byte State)
{
    public byte RingLevel { get; init; }
    public long MarriedAt { get; init; }
    public ImmutableDictionary<ushort, MarriageMemberState> Members { get; init; } = ImmutableDictionary<ushort, MarriageMemberState>.Empty;
    public MarriageWeddingState? Wedding { get; init; }
    public MarriageTreeState? Tree { get; init; }
    public ImmutableDictionary<ushort, MarriageFurnitureStack> Furniture { get; init; } = ImmutableDictionary<ushort, MarriageFurnitureStack>.Empty;
    public ImmutableDictionary<ushort, MarriageFurnitureStack> Storage { get; init; } = ImmutableDictionary<ushort, MarriageFurnitureStack>.Empty;
    public ImmutableArray<MarriageComment> Comments { get; init; } = [];
    public ImmutableHashSet<Guid> ClearEvents { get; init; } = ImmutableHashSet<Guid>.Empty;
    public int PrepareClears { get; init; }
    public long CreatedAt { get; init; }
    public long EndedAt { get; init; }
    public long LastWeddingAt { get; init; }
    [JsonIgnore] public bool Active => State != 0;
    public bool IsMember(ushort id) => id == FirstId || id == SecondId;
    public ushort Other(ushort id) => id == FirstId ? SecondId : id == SecondId ? FirstId :
        throw new ArgumentOutOfRangeException(nameof(id), "角色不属于婚姻关系");
}

internal sealed record MarriageCommit(MarriageRecord Record, Character First, Character? Second = null);
