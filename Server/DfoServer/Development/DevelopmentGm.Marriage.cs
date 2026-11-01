using System.Security.Cryptography;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Marriage;
using DfoServer.Game.Session;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    static string DirectMarriage(ICharacterRepository repository, AccountOwners owners,
        Func<string, GameSession?> find, DevelopmentRequest request, DevelopmentCatalog? catalog)
    {
        if (request.Args.Length != 2) throw new InvalidDataException("用法：direct-marriage <角色名> <对方账号> <对方角色名>");
        var partnerAccount = request.Args[0];
        var partnerName = request.Args[1];
        new DevelopmentRequest(DevelopmentAction.Show, partnerAccount, partnerName, 0, 0).Validate();
        if (find(request.Account) is not null || find(partnerAccount) is not null)
            throw new InvalidOperationException("结婚前请退出双方账号，再重新登录生效");
        var store = repository as IMarriageStore ?? throw new InvalidOperationException("当前服务端不支持直接结婚");
        var resources = catalog ?? throw new InvalidDataException("结婚职业资源未装配");

        string Commit(AccountOwners.Lease firstOwner, AccountOwners.Lease secondOwner)
        {
            // 装载、资源规划在账号锁外；提交时重新核对owner与角色版本，不接管在线账号。
            var first = LoadByName(repository, firstOwner, request.Name);
            var second = LoadByName(repository, secondOwner, partnerName);
            var maleFirst = resources.MarriageMale(first.Job);
            var maleSecond = resources.MarriageMale(second.Job);
            uint context;
            do { context = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(sizeof(uint))); } while (context == 0);
            var planned = MarriageDirectRules.Create(Guid.NewGuid(), context, first.Id, second.Id,
                maleFirst, maleSecond, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var committed = store.CommitDirectMarriage(firstOwner, secondOwner, first, second, planned);
            return Ok(new { state = (int)committed.State, married = true, marriageId = committed.Id.ToString("N"),
                characterId = first.Id, characterName = first.Name, partnerId = second.Id, partnerName = second.Name,
                text = first.Name + " 与 " + second.Name + " 已结婚；重新登录角色后生效。" });
        }

        if (StringComparer.Ordinal.Equals(request.Account, partnerAccount))
            return WithLease(owners, request.Account, owner => Commit(owner, owner));
        var ordered = new[] { request.Account, partnerAccount }.Order(StringComparer.Ordinal).ToArray();
        return WithLease(owners, ordered[0], lower => WithLease(owners, ordered[1], higher =>
            Commit(lower.Account == request.Account ? lower : higher, lower.Account == partnerAccount ? lower : higher)));
    }
}

