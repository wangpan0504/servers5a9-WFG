namespace DfoServer.Network.Builders;

// 完整活动目录扩展，按匹配客户端的既有线序消费。
internal sealed record EventInfoDesignPage(ushort EventId, uint MetadataValue,
    string MetadataText1, string MetadataText2, byte Record04, byte Record08,
    string Title, string ShortName, string Link, uint Record4C, uint Record50,
    string Route, string Content, bool Visible);
