namespace DfoServer.Game.Skills;

internal static class SkillGuidePolicy
{
    // 本服从15级起结束技能使用引导；5是匹配客户端停止提示的原生计数。
    public const byte EndLevel = 15;
    public static byte DisplayCount(byte level) => level >= EndLevel ? (byte)5 : (byte)0;
}
