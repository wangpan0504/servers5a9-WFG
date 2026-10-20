using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Items;
using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using DfoServer.Development;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using PvfLib;
using DfoServer.Testing;

namespace DfoServer.Application;

// Windows 单文件 host 只通过命令行入口启动服务，不公开领域或存储类型。
public static class ServerApplication
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
            if (command.Kind == ServerCommandKind.Serve)
                Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [ERROR] [server_failed] Configuration: {error.Message}");
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
        using var log = command.Kind == ServerCommandKind.Serve ? new ServerLog(options.LogDirectory, Console.Out) : null;
        return await RunWithResourcesAsync(command, options, log);
    }

    static async Task<int> RunWithResourcesAsync(ServerCommand command, ServerOptions options, ServerLog? log)
    {
        string stage = "Configuration";
        T Load<T>(string name, Func<T> load)
        {
            if (log is null)
                return load();
            stage = name;
            log?.Write("startup", new { Stage = name, State = "starting" });
            var timer = Stopwatch.StartNew();
            T result = load();
            log?.Write("startup", new { Stage = name, State = "complete", ElapsedMs = Math.Round(timer.Elapsed.TotalMilliseconds, 3) });
            return result;
        }
        try
        {
            log?.Write("startup", new { Stage = stage, State = "complete", Pid = Environment.ProcessId,
                options.ListenAddress, options.AdvertisedAddress, options.ChannelPort, options.GamePort, options.GmPort,
                options.PvfPath, Database = Path.Combine(options.DataDirectory, "game.db"), options.LogDirectory,
                PacketCapture = options.PacketCaptureDirectory ?? "disabled", options.DevelopmentDungeonEntryProbe, options.TeleportPotionGates });
            using var pvf = Load("PVF archive", () => PvfArchive.Open(options.PvfPath));

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
            if (command.Kind == ServerCommandKind.ScanQuests)
            {
                using var output = Console.OpenStandardOutput();
                var scanItems = new ItemResources(pvf);
                JsonSerializer.Serialize(output, DfoServer.Game.Quests.QuestCoverage.Scan(DfoServer.GameWorld.Quests.QuestCatalog.Load(pvf).WithCreatures(scanItems.Creatures),
                    scanItems.Items, scanItems.EpicPieces, DfoServer.GameWorld.Quests.QuestRewardParameterCatalog.Load(pvf)));
                output.WriteByte((byte)'\n');
                return 0;
            }
            if (command.Kind == ServerCommandKind.InspectPreparation)
            {
                Console.WriteLine(JsonSerializer.Serialize(CharacterPreparation.Load(pvf)));
                return 0;
            }

            var towns = Load("Town resources", () => TownCatalog.Load(pvf));
            if (command.Kind == ServerCommandKind.InspectTowns)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { Room = towns.Room, Return = towns.InitialReturn, Areas = towns.Areas.Values }));
                return 0;
            }

            if (command.Kind == ServerCommandKind.Gm)
                return DevelopmentCommands.RunGm(command.Arguments, options, pvf);
            var itemResources = Load("Item resources", () => new ItemResources(pvf));
            var particularHell = Load("Particular hell party table", () => ParticularHellPartyCatalog.Load(pvf, itemResources.Items.Contains));
            if (particularHell.ItemsByDungeon.Count == 0)
                throw new InvalidDataException("particularhellparty 表是空的");
            var crackDimension = Load("Crack of dimension rewards", () => CrackDimensionCatalog.Load(pvf, itemResources.Items.Contains));
            if (crackDimension.Bands.Length == 0)
                throw new InvalidDataException("次元裂缝奖励表是空的");
            foreach (var warning in itemResources.Contracts.ResourceWarnings)
                log?.Write("resource_warning", new { Stage = "Premium contract STK", warning.Type, warning.ConfiguredItems, warning.Reason });
            var characters = Load("Character resources", () => CharacterCatalog.Load(pvf, itemResources.Items));
            if (command.Kind == ServerCommandKind.SelfTest)
            {
                await SelfTestRunner.RunAsync(towns, pvf, characters, itemResources);
                return 0;
            }

            // 服务监听、导入及离线准备共用同一实例锁；自测在此之前转入隔离入口。
            using var databaseLock = Load("Database instance lock", () =>
            {
                Directory.CreateDirectory(options.DataDirectory);
                return new FileStream(Path.Combine(options.DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            });
            if (command.Kind == ServerCommandKind.PrepareCharacter)
            {
                OfflineCharacterPreparation.Run(options, pvf, towns, command.Arguments[0], command.Arguments[1]);
                return 0;
            }

            var quests = Load("Quest resources", () => DfoServer.GameWorld.Quests.QuestCatalog.Load(pvf).WithCreatures(itemResources.Creatures));
            var repository = Load("Database initialization", () => new CharacterRepository(Path.Combine(options.DataDirectory, "game.db"), quests));
            log?.Write("startup", new { Stage = "Database schema", State = "complete", Version = SqliteMigrations.CurrentVersion });
            if (command.Kind == ServerCommandKind.ImportCharacters)
            {
                using var owner = new AccountOwners().Acquire(options.DevelopmentAccount, () =>
                {
                });
                Console.WriteLine("Imported: " + LegacyCharacterImport.Run(repository, owner, towns, command.Arguments[0]));
                return 0;
            }

            var learning = Load("Skill resources", () => SkillLearningCatalog.Load(pvf, itemResources.Contracts.ExpertSkillLevels));
            stage = "Runtime and listeners";
            await ServerLifetime.RunAsync(options, pvf, repository, towns, characters, learning, itemResources, quests, log!);
            return 0;
        }
        catch (Exception error) when (log is not null)
        {
            Console.Error.WriteLine($"Server failed at {stage}: {error}");
            log.Write("server_failed", new { Stage = stage, Reason = error.GetType().Name, error.Message });
            return 1;
        }
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
