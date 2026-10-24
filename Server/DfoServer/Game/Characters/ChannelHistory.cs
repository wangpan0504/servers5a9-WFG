using System.Collections.Concurrent;

namespace DfoServer.Game.Characters;

// 账号上一次选角所在的频道类型（进程内，不落库）。频道切换后角色都在赛丽亚房间 38/1，
// 无法再按存档城镇反推旧频道类型；原生换频道时用这里记录的旧类型按 01EB5AF0 谓词判断客户端是否保留城镇快照。
// 服务端重启后没有记录，退回 ChannelAreaLanding.ClientKeepsTownSnapshot 的城镇推断。
internal sealed class ChannelHistory
{
    readonly ConcurrentDictionary<string, int> last = new(StringComparer.Ordinal);

    public int? Previous(string account) => last.TryGetValue(account, out int type) ? type : null;

    public void Record(string account, int type) => last[account] = type;
}
