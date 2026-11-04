namespace DfoServer.Network.Builders;

// 完整活动目录扩展，按匹配客户端的既有线序消费。
internal sealed record EventInfoDesignPage(ushort EventId, uint MetadataValue,
    string MetadataText1, string MetadataText2, byte Record04, byte Record08,
    string Title, string ShortName, string Link, uint Record4C, uint Record50,
    string Route, string Content, bool Visible)
{
    // 活动日历底部的道具栏；数量由客户端从真实持有容器读取。
    public IReadOnlyList<int> ShowItemIds { get; init; } = [];
}
