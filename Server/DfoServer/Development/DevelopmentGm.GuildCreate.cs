using System.Globalization;
using System.Text;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Session;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    static string CreateGuild(ICharacterRepository repository, AccountOwners owners,
        Func<string, GameSession?> find, DevelopmentRequest request, DevelopmentCatalog? catalog)
    {
        // 资源解析在账号锁外；真实费用和普通建会使用同一个目录。
        var parameters = (catalog ?? throw new InvalidDataException("公会资源未装配")).ReadGuildParameters();
        if (request.Action == DevelopmentAction.GuildCreateInfo)
            return Ok(new { createCost = parameters.CreateCost });
        if (request.Args.Length != 2 || !int.TryParse(request.Args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int expectedCost) ||
            expectedCost != parameters.CreateCost)
            throw new InvalidDataException("建会费用已变化，请刷新费用后重试");
        string name;
        try { name = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(request.Args[0])); }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        { throw new InvalidDataException("公会名编码无效"); }
        if (GuildTextRules.Check(name) != GuildTextStatus.Available)
            throw new InvalidDataException("公会名须为可显示的文字，最多7个汉字或14个英文字母");
        if (find(request.Account) is not null) throw new InvalidOperationException("创建公会前请退出当前账号");
        var store = repository as IGuildDirectoryStore ?? throw new InvalidOperationException("当前服务端不支持创建公会");
        var directory = new GuildDirectory(store, parameters);
        return WithLease(owners, request.Account, owner =>
        {
            var before = LoadByName(repository, owner, request.Name);
            // 沿用原客户端地址缺省为公会名的规则，不伪造第二个成员或建会认可。
            var planned = directory.PlanCreate(before, name, name, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (planned.Plan is null) throw new InvalidOperationException(GuildCreateReason(planned.Error));
            var result = directory.Create(owner, before, planned.Plan);
            if (result.Error != GuildCreateError.None || result.Character is not { } character)
                throw new InvalidOperationException(GuildCreateReason(result.Error));
            return Ok(new { guildId = result.GuildId, guildName = name, characterId = character.Id,
                createCost = parameters.CreateCost, gold = character.Inventory.Gold,
                text = "公会已创建，当前角色为会长；重新登录后生效。" });
        });
    }

    static string GuildCreateReason(GuildCreateError error) => error switch
    {
        GuildCreateError.NotEnoughGold => "角色金币不足，无法支付建会费用",
        GuildCreateError.AlreadyInGuild => "角色已加入公会或仍在入会冷却中",
        GuildCreateError.NameTaken => "这个公会名已被使用",
        GuildCreateError.AddressTaken => "这个公会地址已被使用，请更换公会名",
        GuildCreateError.InvalidText => "公会名不符合要求",
        GuildCreateError.StalePlan => "角色存档已变化，请刷新后重试",
        _ => "未取得创建公会的成功结果"
    };
}
