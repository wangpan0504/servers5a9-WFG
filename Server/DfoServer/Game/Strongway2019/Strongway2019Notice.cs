using DfoServer.Game.Party;

namespace DfoServer.Game.Strongway2019;

internal enum Strongway2019NoticeKind { Waiting, Symbol, Terminal, Departure }

// 只携带原比赛与收件人身份；连接及协议编码仍由Network投影负责。
internal sealed record Strongway2019Notice(Strongway2019NoticeKind Kind, Strongway2019Match Match,
    PartyMember Recipient, uint Side = 0, uint SymbolKind = 0, uint Value = 0);
