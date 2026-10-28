namespace DfoServer.Game.Guild;

internal sealed record GuildHongbaoPurchase(uint GuildId, int Cera, int MinimumMembers, int DailyLimit, long DayStartUnix);

internal sealed class GuildHongbaoPurchaseRejectedException(string message) : Exception(message);
