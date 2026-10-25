using System.Globalization;
using System.Web;

namespace DfoServer.Network.Management;

// 两个 HTTP 入口共用查询合同，不另建抓包缓存或在线状态。
internal static class PacketQueries
{
    public static int? ConnectionFilter(string query)
    {
        if (query.Length == 0) return null;
        var parameters = HttpUtility.ParseQueryString(query);
        if (parameters.Count != 1 || parameters.AllKeys[0] != "connectionId" ||
            parameters.GetValues("connectionId") is not { Length: 1 } values ||
            !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0)
            throw new InvalidDataException("Invalid connectionId");
        return id;
    }

    public static object Status(PacketCapture capture, ServerOptions options, DateTimeOffset startedAt)
    {
        var sessions = capture.Sessions();
        return new
        {
            StartedAt = startedAt,
            Pid = Environment.ProcessId,
            Channel = new { Address = options.ListenAddress, Port = options.ChannelPort },
            Game = new { Address = options.ListenAddress, Port = options.GamePort },
            options.GmPort,
            options.GmWebPort,
            options.PacketHttpPort,
            TotalConnections = capture.TotalConnections,
            ActiveSessions = sessions.Length,
            ActiveGameSessions = sessions.Count(session => session.Protocol == "GAME"),
            ReceivedPackets = capture.TotalReceived,
            SentPackets = capture.TotalSent
        };
    }
}
