using System.Globalization;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    static string Adventure(ICharacterRepository repository, AccountOwners owners, Func<string, GameSession?> find, DevelopmentRequest request)
    {
        var catalog = repository.AdventureCatalog ?? throw new InvalidOperationException("冒险团资源未装配，请更新配套服务端");
        var live = find(request.Account);
        var now = DateTimeOffset.UtcNow;
        AdventureState state;
        if (request.Args is ["status"])
            state = live is not null ? live.ReadAdventure(catalog, now).State : WithLease(owners, request.Account, owner => repository.ReadAdventure(owner, catalog, now).State);
        else
        {
            if (live is not null) throw new InvalidOperationException("修改冒险团前请退出整个账号，下次登录生效");
            var store = repository as IAdventureGmStore ?? throw new InvalidOperationException("当前服务端不支持冒险团GM修改");
            long revision = long.Parse(request.Args[2], CultureInfo.InvariantCulture);
            state = WithLease(owners, request.Account, owner => store.SetAdventure(owner, revision,
                request.Args[0] == "level" ? int.Parse(request.Args[1], CultureInfo.InvariantCulture) : null,
                request.Args[0] == "name" ? request.Args[1] : null, now));
        }
        return Ok(new { account = request.Account, scope = "account-adventure", name = state.Name, nameInitialized = state.NameInitialized,
            level = state.Level, maximumLevel = catalog.MaximumLevel, experience = state.Experience, revision = state.Revision,
            online = live is not null, text = request.Writes ? "冒险团设置已保存，下次登录生效。" : "冒险团状态已读取。" });
    }
}
