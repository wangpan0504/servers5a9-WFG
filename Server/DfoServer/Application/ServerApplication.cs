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
        // 非 Serve 命令没有 ServerLog，内部兜底只覆盖起服路径；这里再兜一层，把未处理异常
        // 转成退出码，避免 CLR 交给 Windows 错误报告弹出“应用程序错误”对话框。
        try
        {
            return await RunCommandAsync(args);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Server failed: {error}");
            return 1;
        }
    }

    static async Task<int> RunCommandAsync(string[] args)
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
        if (command.Kind == ServerCommandKind.MapGeometryGolden)
        {
            MapGeometryTests.RunDungeonGolden(command.Arguments[0], command.Arguments[1], command.Arguments[2]);
            return 0;
        }
        if (command.Kind == ServerCommandKind.MapGeometryInterop)
        {
            await MapGeometryHttpTests.RunInteropAsync(command.Arguments[0], command.Arguments[1], command.Arguments[2], command.Arguments[3]);
            return 0;
        }
        if (command.Kind == ServerCommandKind.HostedEligibilityFakeHost)
            return DfoServer.Game.Dungeons.HostedDungeonEligibilityFakeHost.Run(command.Arguments[0]);
        // 注册清单不经过ServerOptions、PVF、SQL或监听装配，不表示执行通过。
        if (command.Kind == ServerCommandKind.TcpTestList)
        {
            Console.WriteLine(TcpRegressionTests.RegistryJson());
            return 0;
        }
        // 无主机参数时只打印用法，不解析监听端口、不打开数据库、不连接。
        if (command.Kind == ServerCommandKind.AntonProbe && AntonTcpProbe.IsUsageOnly(command.Arguments))
        {
            Console.WriteLine(AntonTcpProbe.Usage);
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

        // 自测与正式服一样复用连接池：短开连接免去重复建 WAL 映射与解析结构；测试删除临时库前经 TestFiles 释放池句柄，
        // 进程退出前整体释放。离线工具保持非池化。
        if (command.Kind is ServerCommandKind.SelfTest or ServerCommandKind.SelfTestCore or ServerCommandKind.TcpTest)
            SqliteDatabaseBootstrap.EnableRuntimePooling();
        if (command.Kind == ServerCommandKind.SelfTestCore)
        {
            try { await SelfTestRunner.RunCoreAsync(); }
            finally { SqliteDatabaseBootstrap.ReleaseRuntimePool(); }
            return 0;
        }
        // TCP 驱动按测试组共享只读资源；服务子进程自行解析 PVF，入口不再多加载一份。
        if (command.Kind == ServerCommandKind.TcpTest)
        {
            try { await TcpRegressionTests.RunAsync(options, command.Arguments.FirstOrDefault()); }
            finally { SqliteDatabaseBootstrap.ReleaseRuntimePool(); }
            return 0;
        }
        if (command.Kind == ServerCommandKind.AntonProbe)
            return AntonTcpProbe.Run(options, command.Arguments);
        using var log = command.Kind == ServerCommandKind.Serve ? new ServerLog(options.LogDirectory, Console.Out) : null;
        // 数据库慢操作（>1000ms）诊断出口：由装配入口把统一日志交给 Persistence，仓储不持有 ServerLog 实例。
        // 只在本入口生命周期内有效，日志释放后不保留全局引用。
        SqliteDbDiagnostics.Sink = log is null ? null : log.Write;
        try
        {
            return await RunWithResourcesAsync(command, options, log);
        }
        finally
        {
            SqliteDbDiagnostics.Sink = null;
        }
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
                var scanQuests = DfoServer.GameWorld.Quests.QuestCatalog.Load(pvf).WithCreatures(scanItems.Creatures).WithItems(scanItems.Items);
                var scanDungeons = DungeonCatalog.Load(pvf);
                var scanRooms = DungeonRoomCatalog.Load(pvf);
                var scanHope = OperationHopeCatalog.Load(pvf,scanDungeons,scanQuests,scanRooms);
                JsonSerializer.Serialize(output, DfoServer.Game.Quests.QuestCoverage.Scan(scanQuests,
                    scanItems.Items, scanItems.EpicPieces, DfoServer.GameWorld.Quests.QuestRewardParameterCatalog.Load(pvf), scanDungeons,
                    DfoServer.GameWorld.Quests.QuestConnectionCatalog.Load(pvf),scanHope));
                output.WriteByte((byte)'\n');
                return 0;
            }
            if (command.Kind == ServerCommandKind.InspectPreparation)
            {
                Console.WriteLine(JsonSerializer.Serialize(CharacterPreparation.Load(pvf)));
                return 0;
            }

            var geometry = Load("Common map geometry", () => DfoServer.GameWorld.Maps.MapGeometryConfig.ReadFile(Path.Combine(options.DataDirectory, "DisplayCompat.world.json")));
            var towns = Load("Town resources", () => TownCatalog.Load(pvf, geometry));
            if (command.Kind == ServerCommandKind.InspectTowns)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { Room = towns.Room, Return = towns.InitialReturn, Areas = towns.Areas.Values }));
                return 0;
            }

            if (command.Kind == ServerCommandKind.Gm)
                return DevelopmentCommands.RunGm(command.Arguments, options, pvf);
            var itemResources = Load("Item resources", () => new ItemResources(pvf, StartupStage.DropDiagnostics(log)));
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
                try { await SelfTestRunner.RunAsync(towns, pvf, characters, itemResources); }
                finally { SqliteDatabaseBootstrap.ReleaseRuntimePool(); }
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

            var quests = Load("Quest resources", () => DfoServer.GameWorld.Quests.QuestCatalog.Load(pvf).WithCreatures(itemResources.Creatures).WithItems(itemResources.Items));
            // 冒险团经验账本与角色成长共用同一份等级经验表，必须在仓储建立前装配。
            var learning = Load("Skill resources", () => SkillLearningCatalog.Load(pvf, itemResources.Contracts.ExpertSkillLevels));
            var adventureCatalog = Load("Adventure resources", () => new DfoServer.GameWorld.Adventure.AdventureCatalog(pvf));
            var growthCatalog = Load("Character growth resources", () => DfoServer.GameWorld.Characters.CharacterGrowthCatalog.Load(pvf, learning));
            var promotionCatalog = Load("Cash shop promotions", () => ServerRuntime.LoadCashShopPromotions(itemResources));
            DfoServer.GameWorld.Items.CashShopPromotionCatalog CurrentPromotions() => promotionCatalog;
            if (command.Kind == ServerCommandKind.Serve) SqliteDatabaseBootstrap.EnableRuntimePooling();
            var repository = Load("Database initialization", () => new CharacterRepository(Path.Combine(options.DataDirectory, "game.db"), quests,
                adventureCatalog, growthCatalog, guildDungeonMileage: itemResources.GuildContents.DungeonMileage, promotions: CurrentPromotions));
            log?.Write("startup", new { Stage = "Database schema", State = "complete", Version = SqliteMigrations.CurrentVersion,
                Commit = ServerBuildInfo.ShortCommit });
            if (command.Kind == ServerCommandKind.Serve)
            {
                var recovery = Load("Fiendwar interruption recovery", () => repository.RecoverInterruptedFiendwar(DateTimeOffset.UtcNow));
                log?.Write("startup", new { Stage = "Fiendwar interruption recovery", State = "complete",
                    recovery.Refunded, recovery.Abandoned, recovery.MissingPayment });
            }
            if (command.Kind == ServerCommandKind.PrepareBookCharacter)
            {
                OfflineBookPreparation.Run(options, characters, towns, repository, learning, growthCatalog, command.Arguments);
                return 0;
            }
            if (command.Kind == ServerCommandKind.ImportCharacters)
            {
                using var owner = new AccountOwners().Acquire(options.DevelopmentAccount, () =>
                {
                });
                Console.WriteLine("Imported: " + LegacyCharacterImport.Run(repository, owner, towns, command.Arguments[0]));
                return 0;
            }
            // 国服95模型开启账本：现存角色经验一次性折算进账号总量；标记已存在时不再补发。
            var opening = Load("Adventure ledger opening", () => repository.OpenAdventureLedger(DateTimeOffset.UtcNow));
            log?.Write("adventure_ledger", new { Stage = "Opening", opening.Ran, opening.Accounts, opening.Characters,
                Opening = opening.Opening.ToString(CultureInfo.InvariantCulture), Honor = opening.Honor.ToString(CultureInfo.InvariantCulture),
                Roster = opening.Roster.ToString(CultureInfo.InvariantCulture) });
            stage = "Runtime and listeners";
            try
            {
                await ServerLifetime.RunAsync(options, pvf, repository, towns, characters, learning, itemResources, quests, log!,
                    adventureCatalog, growthCatalog, promotions: CurrentPromotions);
            }
            finally
            {
                SqliteDatabaseBootstrap.ReleaseRuntimePool();
            }
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
