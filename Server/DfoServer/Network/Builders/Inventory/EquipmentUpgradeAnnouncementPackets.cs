using DfoServer.Game.Chat;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Builders.Inventory;

internal static class EquipmentUpgradeAnnouncementPackets
{
    public static byte[] Body(string characterName, string itemName, EquipmentUpgradeAnnouncement notice)
    {
        string outcome = notice.Succeeded ? "成功" : "失败";
        string text = notice.Kind switch
        {
            EquipmentUpgradeKind.Reinforcement => $"玩家[{characterName}]强化 +{notice.AttemptedLevel} {itemName}{outcome}",
            EquipmentUpgradeKind.Amplification => $"玩家[{characterName}]增幅 +{notice.AttemptedLevel} {itemName}{outcome}",
            EquipmentUpgradeKind.Refinement => $"玩家[{characterName}]锻造 " +
                (notice.Item.Upgrade > 0 ? $"+{notice.Item.Upgrade} " : "") + $"{itemName} 第{notice.AttemptedLevel}阶段{outcome}",
            _ => throw new ArgumentOutOfRangeException(nameof(notice))
        };
        // 原生86按接收端玩家缓存查名字，跨频道可能找不到；系统聊天正文直接携带本次真实名字。
        return ServerNoticePackets.Chat(ChatText.Truncate(text, ServerNoticePackets.InformationMaxBytes, out _));
    }
}
