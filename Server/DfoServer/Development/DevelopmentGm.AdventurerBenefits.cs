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
        if (request.Action == DevelopmentAction.LuckyProfession && request.Args is not ([] or ["on"] or ["off"] or ["status"]))
            throw new InvalidDataException("幸运开关只支持on/off/status");
        bool? requested = request.Args is ["off"] ? false : request.Args is ["status"] || request.Name.Length == 0 && request.Args.IsEmpty ? null : true;
        if (request.Action == DevelopmentAction.LuckyProfession && request.Name.Length == 0)
        {
            if (requested is { } enabled) service.SetAllProfessions(enabled);
            return Ok(new { scope = "all-professions", enabled = service.Configuration.AllProfessions && service.Configuration.LuckyEnabled == true,
                text = "全职业幸运状态已确认。" });
        }
        return Target(find(request.Account), owners, repository, request, false, (lease, character) =>
        {
            long until = 0;
            if (request.Action == DevelopmentAction.LuckyProfession && requested is { } enabled) service.SetAllProfessions(enabled);
            else if (request.Action == DevelopmentAction.ReturningAdventurer) until = service.SetReturning(lease.Account, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return Ok(new { account = lease.Account, characterId = character.Id, name = character.Name, job = character.Job,
                scope = request.Action == DevelopmentAction.LuckyProfession ? "all-professions" : "account", returningUntil = until,
                enabled = service.Configuration.AllProfessions && service.Configuration.LuckyEnabled == true,
                text = request.Action == DevelopmentAction.LuckyProfession
                    ? (service.Configuration.AllProfessions ? "已开启全职业幸运，所有职业及其转职、觉醒均享受原生四叶草及技能Buff。" : "已关闭全职业幸运，在线角色自动清除幸运资格。")
                    : "已设置本账号为回归冒险家，期限按原生资源；重新进图后按原生组队条件获得Buff。" });
        });
    }
}
