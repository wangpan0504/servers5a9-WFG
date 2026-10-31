namespace DfoServer.Game.Marriage;

internal enum MarriageFailureReason
{
    NotMarried, InvalidRingLevel, MissingItem, LovePointLimit, InvalidWedding,
    AlreadyReserved, TasksIncomplete, WeddingDailyLimit, GiftClosed, AlreadyGifted,
    GiftTooSmall, GiftTooLarge, InvalidInvitationTarget, DuplicateInvitation, NoInvitations, HallUnavailable
}

internal sealed class MarriageRuleException(MarriageFailureReason reason, string message) : InvalidOperationException(message)
{
    public MarriageFailureReason Reason { get; } = reason;
}
