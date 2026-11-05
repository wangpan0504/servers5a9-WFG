using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonDespairProgress(int NextLayer = 1, int LastClearDay = 0, int ClearCount = 0)
{
    public void Validate()
    {
        if (NextLayer is < 1 or > 100 || LastClearDay is < 0 or > 3652058 || ClearCount < 0)
            throw new InvalidDataException("绝望之塔持久进度无效");
    }
}

internal sealed record DungeonDespairAdmission(DungeonSpecialEntry Entry, DungeonRequiredItemRow? Ticket, int DungeonId);

// 首次免费、失败邀请函和当日成功后停层以本版4473说明、Neople更新公告为兼容依据。
// 不将ETC第三列、all layer required item或未绑定的推荐书解释成任意跳层授权。
internal static class DungeonDespairEntryPolicy
{
    public const string RuleId = "pvf-despair-floor-official-daily-retry-v1";

    public static DungeonDespairAdmission? Prepare(uint requested, Character character, int day,
        DungeonDespairProgress progress, DungeonSpecialEntryUsage usage, TowerOfDespairCatalog tower,
        DungeonDefinition floor, int adventureLevel, AdventureCatalog? adventure = null)
    {
        progress.Validate();
        if ((requested != tower.RootDungeonId && requested != floor.Id) || day is < 1 or > 3652058 || character.Level < tower.MinimumLevel ||
            progress.LastClearDay == day || usage.CharacterEntries < 0 || usage.AccountCharacters < 0 ||
            progress.NextLayer > tower.Floors.Length) return null;
        var target = tower.Floors[progress.NextLayer-1];
        if (target.Layer != progress.NextLayer || target.DungeonId != floor.Id || !floor.IsDespairTower ||
            !floor.DisableFatigueConsumption || !floor.EnterWithoutFatigue || floor.CoinLimit is null)
            throw new InvalidDataException("绝望准入没有绑定当前层的完整DGN合同");
        int towerBonus = tower.AdventureCounts
            .Where(row => adventureLevel >= row.MinimumLevel && adventureLevel <= row.MaximumLevel)
            .Select(row => row.AdditionalCount).DefaultIfEmpty().Single();
        int benefitBonus = AdventureBenefitPolicy.DespairTowerExtraEntries(adventureLevel, adventure);
        int maximum = checked(tower.AccountMaximum + Math.Max(towerBonus, benefitBonus));
        // 已入场角色的失败重试不占用第二个账号角色名额。
        if (usage.CharacterEntries == 0 && usage.AccountCharacters >= maximum) return null;
        var ticket = floor.OrdinaryEntryTicket;
        if (ticket is not { ItemId:4473, Count:1 }) throw new NotSupportedException("绝望重试票不是本版已核对的4473");
        var special = new DungeonSpecialEntry(day,DungeonSpecialEntryKind.Despair,progress.NextLayer,0,usage)
            { DespairProgress = progress };
        return new(special,usage.CharacterEntries == 0 ? null : ticket,floor.Id);
    }
}
