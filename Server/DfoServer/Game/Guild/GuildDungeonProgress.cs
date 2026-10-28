using System.Collections.Immutable;
using DfoServer.GameWorld.Guilds;

namespace DfoServer.Game.Guild;

// 会籍或净化在准备后变化属于正常业务拒绝，事务回滚后应恢复选图而非断开连接。
internal sealed class GuildDungeonAdmissionRejectedException(string message) : Exception(message);

internal sealed record GuildDungeonAdmission(uint DungeonId, GuildDungeonMembership Membership, GuildDungeonCatalog Catalog);
internal sealed record GuildDungeonClearCount(uint DungeonId, uint Clears, uint? EffectiveClears = null, long DecayAnchorUnix = 0)
{
    // 本服规则：独立有效次数每完整闲置周期相对衰减；真实通关总数永不扣减。
    public (uint Units, long Anchor) Effective(GuildDungeonCatalog catalog, long now)
    {
        var definition = catalog.Find(DungeonId);
        if (definition is null) return (0, DecayAnchorUnix);
        uint limit = checked((uint)(((long)definition.GaugeLimit + definition.GaugeOnClear - 1) / definition.GaugeOnClear));
        uint units = Math.Min(EffectiveClears ?? Clears, limit);
        long anchor = DecayAnchorUnix > 0 ? DecayAnchorUnix : now;
        long period = checked(catalog.InactiveDays * 86400L);
        long cycles = now > anchor ? (now - anchor) / period : 0;
        long remaining = cycles;
        while (remaining-- > 0 && units > 0) units = checked((uint)((ulong)units * (uint)(100 - catalog.InactiveReduction) / 100));
        return (units, checked(anchor + cycles * period));
    }
}
internal sealed record GuildDungeonProgress(uint GuildId, ImmutableArray<GuildDungeonClearCount> Counts, long LastClearUnix);
