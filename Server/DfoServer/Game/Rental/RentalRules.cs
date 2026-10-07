namespace DfoServer.Game.Rental;

internal readonly record struct RentalChargeValidation(bool Accepted, int PointsAfter, long GoldAfter)
{
    public static RentalChargeValidation Reject(int points, long gold) => new(false, points, gold);
}

internal static class RentalRules
{
    public static RentalChargeValidation ValidateCharge(int pointLimit, long gold, int points, int goldPerPoint, int count)
    {
        if (pointLimit <= 0 || gold < 0 || points < 0 || goldPerPoint <= 0 || count <= 0 ||
            count > pointLimit - points)
            return RentalChargeValidation.Reject(points, gold);
        long cost = checked((long)count * goldPerPoint);
        return gold < cost ? RentalChargeValidation.Reject(points, gold) : new(true, checked(points + count), gold - cost);
    }
}
