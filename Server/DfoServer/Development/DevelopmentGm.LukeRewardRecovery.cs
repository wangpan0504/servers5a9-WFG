using System.Globalization;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    static string ExecuteLukeRewardRecovery(ICharacterRepository repository, AccountOwners owners, GameSession? live,
        DevelopmentRequest request, DevelopmentCatalog? catalog)
    {
        if (request.Action == DevelopmentAction.LukeRewardList)
        {
            Guid? after = null;
            int limit = 16;
            if (request.Args.Length is < 1 or > 3 ||
                !ushort.TryParse(request.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out ushort id) ||
                request.Args.Length >= 2 && (!int.TryParse(request.Args[1], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 16) ||
                request.Args.Length == 3 && (!Guid.TryParseExact(request.Args[2], "N", out var cursor) || cursor == Guid.Empty))
                throw new InvalidDataException("用法：luke-reward-list <角色名> <角色ID> [limit:1..16] [after:N]");
            if (request.Args.Length == 3) after = Guid.ParseExact(request.Args[2], "N");
            var discovery = repository as ILukeRewardRecoveryStore ?? throw new InvalidOperationException("历史卢克奖励存储未装配");
            string List(AccountOwners.Lease owner, Character current)
            {
                if (current.Id != id) throw new InvalidDataException("目标角色ID不符");
                return Ok(discovery.ListLukeRewardRecovery(owner, current, after, limit));
            }
            // 沿用原管理恢复入口的未选角约束；只返回保存的备选，不领取、不装载资源。
            return live is not null ? live.WithUnselectedDevelopmentOwner(request.Name, List) :
                WithLease(owners, request.Account, owner => List(owner, LoadByName(repository, owner, request.Name)));
        }
        bool claim = request.Action == DevelopmentAction.LukeRewardClaim;
        if (request.Args.Length != (claim ? 7 : 5) ||
            !ushort.TryParse(request.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out ushort character) ||
            !Guid.TryParseExact(request.Args[1], "N", out Guid instance) || instance == Guid.Empty ||
            !int.TryParse(request.Args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int phase) || phase is not (0 or 1) ||
            !byte.TryParse(request.Args[3], NumberStyles.None, CultureInfo.InvariantCulture, out byte kind) ||
            !byte.TryParse(request.Args[4], NumberStyles.None, CultureInfo.InvariantCulture, out byte card))
            throw new InvalidDataException("用法：luke-reward-inspect/claim <角色> <角色ID> <instance:N> <phase:0|1> <kind> <card>；claim另需 <event:N> <anchor>");
        Guid expectedEvent = Guid.Empty; long anchor = 0;
        if (claim && (!Guid.TryParseExact(request.Args[5], "N", out expectedEvent) || expectedEvent == Guid.Empty ||
            !long.TryParse(request.Args[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out anchor)))
            throw new InvalidDataException("领取必须明确给出检查所得原阶段event和anchor");
        var store = repository as ILukeRewardRecoveryStore ?? throw new InvalidOperationException("历史卢克奖励存储未装配");
        var target = new LukeRewardRecoveryTarget(character, instance, phase, kind, card);
        string Execute(AccountOwners.Lease owner, Character current)
        {
            if (!claim) return Ok(store.ReadLukeRewardRecovery(owner, current, target));
            var result = LukeRewardRecovery.Claim(owner, current, target, expectedEvent, anchor, store, () =>
            {
                var resources = catalog?.Resources ?? throw new InvalidOperationException("奖励资源目录未装配");
                return (resources.Items, resources.GoldLimits, resources.EpicPieces);
            }, DateTimeOffset.UtcNow);
            return Ok(new { target, duplicate = result.Duplicate, result.MailIds, revision = result.Character.Revision });
        }
        // 不接管账号，不切角色，不构造旧Battle；管理命令只是明确原资格的领取入口。
        return live is not null ? live.WithUnselectedDevelopmentOwner(request.Name, Execute) :
            WithLease(owners, request.Account, owner => Execute(owner, LoadByName(repository, owner, request.Name)));
    }
}
