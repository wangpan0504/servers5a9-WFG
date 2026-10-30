using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Currency;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    RechargeTicket? rechargeTicket;

    public RechargeTicket? IssueRechargeTicket(DateTimeOffset now) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase == SessionPhase.Loading)
            return null;
        return rechargeTicket = RechargeTicket.Create(owner.Account, owner.Generation, SelectionEpoch, Selected.Id, now);
    });

    public bool CanPublishRechargeTicket(RechargeTicket ticket) => owner.Execute(() =>
        ReferenceEquals(rechargeTicket, ticket) && ticket.Generation == owner.Generation &&
        Selected?.Id == ticket.CharacterId && SelectionEpoch == ticket.SelectionEpoch && Ready);

    public T WithRechargeTicket<T>(string token, uint key, DateTimeOffset now, Func<T> action) => owner.Execute(() =>
    {
        var ticket = rechargeTicket;
        if (ticket is null || now >= ticket.ExpiresAt || ticket.Generation != owner.Generation ||
            ticket.SelectionEpoch != SelectionEpoch || ticket.CharacterId != Selected?.Id || !Ready || key != ticket.Key ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ticket.Token), Encoding.ASCII.GetBytes(token)))
            throw new InvalidDataException("充值票据无效或已到期，请从当前游戏角色重新打开充值页");
        // 验票和实际加值保持在同一个owner内，不能在换角/接管后借用新的会话提交旧票据。
        return action();
    });
}
