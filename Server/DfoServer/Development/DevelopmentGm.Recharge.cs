using System.Globalization;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    static string Recharge(ICharacterRepository repository, AccountOwners owners, GameSession? live, DevelopmentRequest request)
    {
        // 账号钱包仍通过现有角色资产事务保存；只读名册，不加载无关角色的完整库存。
        string name = live?.Selected?.Name ?? (live?.Roster() ?? WithLease(owners, request.Account, repository.ListRoster)).FirstOrDefault()?.Name ??
            throw new InvalidDataException("该账号没有角色，请先创建角色");
        var target = request with { Name = name };
        if (request.Action == DevelopmentAction.RechargeStatus)
            return Summary(Target(live, owners, repository, target, false, (lease, _) => repository.LoadAccountWallet(lease)));
        Character character;
        if (request.Args[0] == "cera")
        {
            int amount = int.Parse(request.Args[1], CultureInfo.InvariantCulture);
            character = Commit(live, owners, repository, target, false, current =>
            {
                var before = current.Inventory;
                long balance = (long)before.AccountWallet.Get(CurrencyKind.Cera) + amount;
                return balance > int.MaxValue ? InventoryResult.Reject() :
                    new(true, 0, new InventoryPlan(before, CurrencyWallet.Set(before, CurrencyKind.Cera, balance), "gm_recharge"));
            }, "点券余额将超过上限，本次未充值");
        }
        else
        {
            long seconds = int.Parse(request.Args[1], CultureInfo.InvariantCulture) * 86400L;
            character = Target(live, owners, repository, target, false, (owner, current) =>
            {
                long expires;
                try
                {
                    expires = current.Inventory.AccountWallet.ActivateContract(CardMembership.BlackDiamondType,
                        seconds, DateTimeOffset.UtcNow).ContractExpiry(CardMembership.BlackDiamondType);
                }
                catch (OverflowException)
                {
                    throw new InvalidDataException("黑钻到期时间超过存储范围，本次未开通");
                }
                // 累加和保存都在原账号owner内；既有会员入口保留疲劳补发、待确认业务门禁及同事务提交。
                if (live is null)
                    repository.SetCardMembership(owner, name, CardMembershipKind.BlackDiamond, expires);
                else
                    live.SetCardMembership(name, CardMembershipKind.BlackDiamond, expires);
                return repository.LoadCharacter(owner, current.Slot);
            });
        }
        return Summary(character.Inventory.AccountWallet);

        string Summary(AccountWallet wallet) => Ok(new
        {
            account = request.Account, cera = wallet.Get(CurrencyKind.Cera),
            blackDiamondExpiresAt = wallet.ContractExpiry(CardMembership.BlackDiamondType), walletRevision = wallet.Revision
        });
    }
}
