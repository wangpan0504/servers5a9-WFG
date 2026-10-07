using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Items;
using System.Globalization;
using System.Text.Json;
using DfoServer.Development;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using PvfLib;
using DfoServer.Testing;

namespace DfoServer.Application;

internal static class ServerApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        ServerCommand command;
        try
        {
            command = ServerCommand.Parse(args);
        }
        catch (InvalidDataException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }

        if (command.Kind == ServerCommandKind.Help)
        {
            Console.WriteLine(ServerCommand.Usage);
            return 0;
        }
        if (command.Kind is ServerCommandKind.PvfList or ServerCommandKind.PvfText)
        {
            using var archive = PvfArchive.Open(command.Arguments[0]);
            if (command.Kind == ServerCommandKind.PvfList)
            {
                foreach (var file in archive.Files.Where(file => file.Path.Contains(command.Arguments[1], StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine(file.Path);
            }
            else
                Console.WriteLine(archive.ReadText(command.Arguments[1]));
            return 0;
        }

        ServerOptions options;
        try
        {
            options = ServerOptions.Load(command.Options);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        if (command.PacketCapture)
            options = options with { PacketCaptureDirectory = command.Arguments.Length == 0 ? options.LogDirectory : Path.GetFullPath(command.Arguments[0]) };
        if (command.Kind == ServerCommandKind.Gm && (command.Arguments.Length == 0 || command.Arguments[0] is "help" or "-h" or "--help"))
        {
            Console.WriteLine(DevelopmentRequest.Usage);
            return 0;
        }

        if (command.Kind == ServerCommandKind.SelfTestCore)
        {
            await SelfTestRunner.RunCoreAsync();
            return 0;
        }

        // TCP 驱动按测试组共享只读资源；服务子进程自行解析 PVF，入口不再多加载一份。
        if (command.Kind == ServerCommandKind.TcpTest)
        {
            await TcpRegressionTests.RunAsync(options, command.Arguments.FirstOrDefault());
            return 0;
        }
        using var pvf = PvfArchive.Open(options.PvfPath);

        if (command.Kind == ServerCommandKind.InspectDungeon)
            return InspectDungeon(pvf, int.Parse(command.Arguments[0], CultureInfo.InvariantCulture));
        if (command.Kind == ServerCommandKind.ScanDungeons)
        {
            // 全量只读扫描在仓储、实例锁和监听初始化之前完成。
            // 完整扫描输出可达数百MiB，直接写流，避免再生成一份巨大的UTF-16 JSON字符串。
            using var output = Console.OpenStandardOutput();
            JsonSerializer.Serialize(output, DungeonResourceSurvey.Scan(pvf));
            output.WriteByte((byte)'\n');
            return 0;
        }
        if (command.Kind == ServerCommandKind.InspectPreparation)
        {
            Console.WriteLine(JsonSerializer.Serialize(CharacterPreparation.Load(pvf)));
            return 0;
        }

        var towns = TownCatalog.Load(pvf);
        if (command.Kind == ServerCommandKind.InspectTowns)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Room = towns.Room, Return = towns.InitialReturn, Areas = towns.Areas.Values }));
            return 0;
        }

        if (command.Kind == ServerCommandKind.Gm)
            return DevelopmentCommands.RunGm(command.Arguments, options);
        var itemResources = new ItemResources(pvf);
        var characters = CharacterCatalog.Load(pvf, itemResources.Items);
        if (command.Kind == ServerCommandKind.SelfTest)
        {
            await SelfTestRunner.RunAsync(towns, pvf, characters, itemResources);
            return 0;
        }

        Directory.CreateDirectory(options.DataDirectory);
        // 服务监听、导入及离线准备共用同一实例锁；自测在此之前转入隔离入口。
        using var databaseLock = new FileStream(Path.Combine(options.DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (command.Kind == ServerCommandKind.PrepareCharacter)
        {
            OfflineCharacterPreparation.Run(options, pvf, towns, command.Arguments[0], command.Arguments[1]);
            return 0;
        }

        var repository = new CharacterRepository(Path.Combine(options.DataDirectory, "game.db"));
        if (command.Kind == ServerCommandKind.ImportCharacters)
        {
            using var owner = new AccountOwners().Acquire(options.DevelopmentAccount, () =>
            {
            });
            Console.WriteLine("Imported: " + LegacyCharacterImport.Run(repository, owner, towns, command.Arguments[0]));
            return 0;
        }

        await ServerLifetime.RunAsync(options, pvf, repository, towns, characters, SkillLearningCatalog.Load(pvf), itemResources);
        return 0;
    }

    static int InspectDungeon(PvfArchive pvf, int id)
    {
        try
        {
            var catalog = DungeonCatalog.Load(pvf);
            Console.WriteLine(JsonSerializer.Serialize(new { catalog.DungeonCount, Dungeon = catalog.Get(id) }));
            return 0;
        }
        catch (Exception error) when (error is InvalidDataException or FileNotFoundException or NotSupportedException)
        {
            Console.Error.WriteLine("副本资源检查失败：" + error.Message);
            return 1;
        }
    }
}
