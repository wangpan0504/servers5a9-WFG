using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;

namespace DfoServer.Development;

// 纸娃娃外观来源（只读）：职业、0–12 号装备位（0–11 装扮，12 武器）的物品与克隆外观编号，以及武器幻化选择。
// 在线时经原 owner 读取已提交角色，离线时取得租约（拒绝接管）；不改库，GM 网页据此合成 PNG。
internal static partial class DevelopmentGm
{
    static string DescribeAppearance(ICharacterRepository repository, AccountOwners owners, GameSession? live, DevelopmentRequest request)
    {
        var character = live is not null ? live.InspectCharacter(request.Name) :
            WithLease(owners, request.Account, lease => LoadByName(repository, lease, request.Name));
        return "OK " + JsonSerializer.Serialize(new
        {
            name = character.Name, job = character.Job, growType = character.GrowType, rev = character.Revision,
            weaponSkin = character.Inventory.Skins.Selected,
            equipment = character.Inventory.Items
                .Where(item => item.Position.List == InventoryListType.Equipment && item.Position.Index is >= 0 and <= 12)
                .OrderBy(item => item.Position.Index)
                .Select(item => new { slot = item.Position.Index, id = item.Core.ItemId, clear = item.Avatar?.ClearAvatarId ?? 0 })
        });
    }
}
