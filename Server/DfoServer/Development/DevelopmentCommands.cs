using DfoServer.Game.Accounts;
using DfoServer.Network.Management;
using DfoServer.Persistence;

namespace DfoServer.Development;

internal static class DevelopmentCommands
{
    public static int RunGm(string[] args, ServerOptions options)
    {
        string line = string.Join(' ', args);
        DevelopmentRequest request;
        try
        {
            request = DevelopmentRequest.Parse(line, options.DevelopmentAccount);
        }
        catch (InvalidDataException error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }

        if (options.GmPort != 0)
        {
            try
            {
                if (DevelopmentGmClient.TryExecute(options.GmPort, line, out var remote))
                {
                    if (remote.StartsWith("ERR ", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine(remote[4..]);
                        return 1;
                    }

                    Console.WriteLine(remote);
                    return 0;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            using var databaseLock = new FileStream(Path.Combine(options.DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var repository = new CharacterRepository(Path.Combine(options.DataDirectory, "game.db"));
            Console.WriteLine(DevelopmentGm.Execute(repository, new AccountOwners(), _ => null, request));
            return 0;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("运行库正被服务占用。在线调整需要传入与运行实例一致的 --gm-port；否则先停止该实例。");
            return 1;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
