using DfoServer.Game.Accounts;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using DfoServer.Infrastructure;
using DfoServer.Game.Mailbox;
using PvfLib;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Development;

internal static class DevelopmentCommands
{
    public static int RunGm(string[] args, ServerOptions options, PvfArchive pvf)
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
            bool reset = request.Action is DevelopmentAction.Level or DevelopmentAction.Grow or DevelopmentAction.GrowOptions;
            var resources = request.Action == DevelopmentAction.Item || reset ? new ItemResources(pvf) : null;
            var mailbox = request.Action == DevelopmentAction.Item ? new MailboxService(repository, resources!.Items, resources.GoldLimits, resources.EpicPieces) : null;
            var learning = reset ? SkillLearningCatalog.Load(pvf) : null;
            var progress = reset ? new CharacterProgressReset(CharacterCatalog.Load(pvf, resources!.Items), learning!, CharacterGrowthCatalog.Load(pvf, learning)) : null;
            Console.WriteLine(DevelopmentGm.Execute(repository, new AccountOwners(), _ => null, request, mailbox, resources?.Items, progress));
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
