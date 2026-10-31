using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class SelectionContentsProjection(SelectionContentsRules rules)
{
    public async Task TowersAsync(GameConnectionContext context)
    {
        if (context.Session.CaptureSelectionContents(rules) is not { } receipt) return;
        // 95级悬停依赖721的五槽排列；首次选角不能等到入城才安装账号配置。
        await context.Notify(NotiPacketType.WEEKLY_DUNGEON_INFO_CONFIG, AdventurePackets.WeeklyConfig(receipt.WeeklySlots),
            "selection_weekly_config", () => context.Session.CanPublishSelectionContents(receipt));
        await context.Notify(NotiPacketType.ACCOUNT_TOWER_DUNGEON, SelectionContentsPackets.Towers(receipt.Towers),
            "selection_account_towers", () => context.Session.CanPublishSelectionContents(receipt));
    }

    public async Task CharacterAsync(GameConnectionContext context, byte index)
    {
        if (context.Session.CaptureSelectionContents(rules, index) is not { Contents: { } contents } receipt)
        {
            // 0233C450失败分支只清请求pending，不读取错误码或正文。
            await context.Reply(CmdPacketType.CONTENTS_PLAY_INFO, [0], "selection_contents_rejected");
            return;
        }
        bool Guard() => context.Session.CanPublishSelectionContents(receipt);
        // 704也可先于645到达，必须先安装排列再让ACK704生成角色提示行。
        await context.Notify(NotiPacketType.WEEKLY_DUNGEON_INFO_CONFIG, AdventurePackets.WeeklyConfig(receipt.WeeklySlots),
            "selection_weekly_config", Guard);
        await context.Notify(NotiPacketType.ACCOUNT_TOWER_DUNGEON, SelectionContentsPackets.Towers(receipt.Towers),
            "selection_account_towers", Guard);
        await context.Reply(CmdPacketType.CONTENTS_PLAY_INFO, SelectionContentsPackets.Character(index, contents),
            "selection_contents", Guard);
    }
}
