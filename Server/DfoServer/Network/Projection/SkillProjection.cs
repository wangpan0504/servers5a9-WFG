using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class SkillProjection
{
    // 客户端处理完整NOTI19时会延迟重建技能界面并重置连招六列；重建晚于同批包，
    // 同批NOTI447会被重建出的默认排列覆盖，必须等重建结束后再回放一次服务端排列。
    static readonly TimeSpan ComboRebuildDelay = TimeSpan.FromMilliseconds(500);

    public static async Task PublishAsync(GameConnectionContext context, Character character, string skillAction,
        string comboAction, Func<bool>? canSend = null, Func<ushort, string>? defaultCommand = null)
    {
        bool combos = character.Job == 9 && character.Learning.Initialized;
        if (combos) context.Session.MarkComboRebuild();
        await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(character.Learning, defaultCommand), skillAction, canSend);
        await PublishCombosAsync(context, character, comboAction, canSend);
        if (combos) await PublishCombosAfterRebuildAsync(context, character, comboAction, canSend);
    }

    // 自动组合开始前的完整树回放：只发NOTI19，不标记重建窗口，也不补发447。
    // 客户端收到后重建技能面板，随后在510回执之后本地生成排列并显示。
    public static Task PublishSkillsAsync(GameConnectionContext context, Character character, string skillAction,
        Func<bool>? canSend = null, Func<ushort, string>? defaultCommand = null) =>
        context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(character.Learning, defaultCommand), skillAction, canSend);

    // 447首字节必须与登录初始化一致为0；实测写1会让客户端在完整技能树重放后
    // 按自动组合重建六列，把玩家自己的排列覆盖掉。
    public static Task PublishCombosAsync(GameConnectionContext context, Character character, string comboAction,
        Func<bool>? canSend = null) =>
        character.Job == 9 && character.Learning.Initialized
            ? context.Notify(NotiPacketType.COMBO_SKILL_INFO, SkillPackets.Combos(character.Learning), comboAction, canSend)
            : Task.CompletedTask;

    // 重建完成前客户端自动回传的508由GameSession窗口拦截；这里在窗口内补发服务端排列，
    // 客户端随后回传的才是正确内容，服务端存档不会被默认排列覆盖。
    public static async Task PublishCombosAfterRebuildAsync(GameConnectionContext context, Character character,
        string comboAction, Func<bool>? canSend = null)
    {
        await Task.Delay(ComboRebuildDelay, context.Stop);
        await PublishCombosAsync(context, character, comboAction, canSend);
    }
}
