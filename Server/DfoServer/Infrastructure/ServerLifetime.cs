using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Auth;
using DfoServer.Persistence;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.Infrastructure;

internal static class ServerLifetime
{
    public static async Task RunAsync(ServerOptions options, PvfArchive pvf, CharacterRepository repository, TownCatalog towns, CharacterCatalog characters,
        SkillLearningCatalog learning, ItemResources itemResources, DfoServer.GameWorld.Quests.QuestCatalog quests, ServerLog log)
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, args) =>
        {
            args.Cancel = true;
            stop.Cancel();
        };
        EventHandler onExit = (_, _) => stop.Cancel();
        Console.CancelKeyPress += onCancel;
        AppDomain.CurrentDomain.ProcessExit += onExit;
        try
        {
            Directory.CreateDirectory(options.LogDirectory);
            using var watcher = new FileSystemWatcher(options.LogDirectory, "stop.request")
            {
                EnableRaisingEvents = true
            };
            watcher.Created += (_, _) => stop.Cancel();
            if (File.Exists(Path.Combine(options.LogDirectory, "stop.request")))
                throw new InvalidDataException("Remove prior stop.request before starting a new run");
            await ServerRuntime.Create(options, pvf, repository, towns, characters, learning, itemResources, log, quests).RunAsync(stop.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            AppDomain.CurrentDomain.ProcessExit -= onExit;
        }
    }
}
