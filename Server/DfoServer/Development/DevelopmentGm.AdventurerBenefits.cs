using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Settings;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    static string SetAdventurerBenefit(ICharacterRepository repository, AccountOwners owners, Func<string, GameSession?> find,
        DevelopmentRequest request, GameDebugSettings? settings)
    {
        var service = settings?.AdventurerBenefits ?? throw new InvalidOperationException("冒险家活动资源未装配，请更新配套服务端");
        return Target(find(request.Account), owners, repository, request, false, (lease, character) =>
        {
            long until = 0;
            if (request.Action == DevelopmentAction.LuckyProfession) service.SetProfession(character.Job);
            else until = service.SetReturning(lease.Account, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return Ok(new { account = lease.Account, characterId = character.Id, name = character.Name, job = character.Job,
                scope = request.Action == DevelopmentAction.LuckyProfession ? "profession" : "account", returningUntil = until,
                text = request.Action == DevelopmentAction.LuckyProfession
                    ? "已将当前角色的基础职业设为活动职业，适用于该职业全部转职；重新进图后按原生组队条件获得Buff。"
                    : "已设置本账号为回归冒险家，期限按原生资源；重新进图后按原生组队条件获得Buff。" });
        });
    }
}
