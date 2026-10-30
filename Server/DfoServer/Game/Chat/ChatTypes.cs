namespace DfoServer.Game.Chat;

// 02E38E00选择输入上下文，02E2E470写入CMD17/424；TypeN表示只确认了线值，未确认业务含义。
internal enum ChatRequestType : byte
{
    Whisper = 1,
    Party = 2,
    Normal = 3,
    Context5 = 5,
    Guild = 6,
    Named7 = 7,
    Context8 = 8,
    Context9 = 9,
    ServerMegaphone = 11,
    Item898Megaphone = 12,
    ChannelMegaphone = 13,
    Official1120Megaphone = 14,
    Item2660655Megaphone = 15,
    OneToOne = 36,
    SelectedMegaphone37 = 37,
    NamedReply39 = 39,
    OneToOneReply = 40,
    WhisperReply = 41,
    // 原服务端角色ID私聊兼容入口；未发现本版生产者，不能当作原生名字私聊。
    LegacyCharacterWhisper = 46,
    NormalContext48 = 48,
    NormalContext49 = 49,
    SelectedMegaphone53 = 53,
    Item10000844Megaphone = 59,
    Item10151255Megaphone = 60,
    Named64 = 64,
    NamedReply66 = 66
}

// 下行显示类型与请求类型分开；消费入口023D0BA0、023B0210、023A3AE0、02E31740。
internal enum ChatMessageType : byte
{
    Whisper = 1,
    Party = 2,
    Normal = 3,
    Guild = 6,
    ServerMegaphone = 11,
    Item898Megaphone = 12,
    ChannelMegaphone = 13,
    Official1120Megaphone = 14,
    Item2660655Megaphone = 15,
    OneToOne = 36,
    WhisperReply = 41,
    NormalHyperlink47 = 47,
    NormalContext48 = 48,
    NormalContext49 = 49,
    SelectedMegaphone53 = 53,
    Item10000844Megaphone = 59,
    Item10151255Megaphone = 60,
    Named64 = 64
}
