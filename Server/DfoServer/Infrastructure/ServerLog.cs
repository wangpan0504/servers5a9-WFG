using System.Text.Json;

namespace DfoServer.Infrastructure;

internal sealed class ServerLog(string directory) : IDisposable
{
    readonly BufferedLogFile file = new(Path.Combine(directory, "server.jsonl"), error => Console.Error.WriteLine("Server log failed: " + error.Message));
    public void Write(string kind, object detail)
    {
        string line = JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Kind = kind, Detail = detail }) + "\n";
        file.Append(line);
    }
    public void Flush() => file.Flush();
    public void Dispose() => file.Dispose();
}
