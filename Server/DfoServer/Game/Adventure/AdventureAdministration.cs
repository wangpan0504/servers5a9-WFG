using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Game.Adventure;

internal interface IAdventureGmStore
{
    AdventureState SetAdventure(AccountOwners.Lease owner, long revision, int? level, string? name, DateTimeOffset now);
}

internal static class AdventureAdministration
{
    public static AdventureState Prepare(AdventureState before, AdventureCatalog catalog, int? level, string? name)
    {
        if ((level is null) == (name is null)) throw new InvalidDataException("每次只修改冒险团等级或名字");
        if (name is not null)
        {
            if (!AdventureName.IsValid(name)) throw new InvalidDataException("冒险团名须为可显示的文字、数字或下划线，最多16字符/32字节");
            return before with { Name = name, NameInitialized = true };
        }
        if (level < 1 || level > catalog.MaximumLevel) throw new InvalidDataException($"冒险团等级须为1至{catalog.MaximumLevel}");
        var after = before with { Level = level!.Value, Experience = 0, Remainder = 0 };
        // 只调账本排除额，保留所有角色里程和开启事实；下一笔自然经验继续按差额累计。
        after = after with { ExcludedExperience = before.ExcludedExperience + AdventureCredit.ExactTotal(before, catalog) - AdventureCredit.ExactTotal(after, catalog) };
        after.Validate(catalog);
        return after;
    }
}
