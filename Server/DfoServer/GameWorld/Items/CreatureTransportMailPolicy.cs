namespace DfoServer.GameWorld.Items;

internal sealed record CreatureTransportMailExpiry(int SourceItemId, long PolicyRevision,
    int Days, long Created, long ExpiresAt);

// 仅决定已获跨界资格的源道具所用邮件期限，不授予跨界资格或修改宠物寿命。
internal sealed record CreatureTransportMailPolicy(long Revision)
{
    public CreatureTransportMailExpiry Create(int sourceItemId, long created)
    {
        if (Revision <= 0 || sourceItemId <= 0 || created < 0)
            throw new InvalidDataException("跨界邮件来源或策略版本无效");
        int days = sourceItemId == 490705421 ? 365 : 15;
        return new(sourceItemId, Revision, days, created, checked(created + days * 86400L));
    }
}
