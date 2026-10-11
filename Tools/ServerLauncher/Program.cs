namespace ServerLauncher;

internal static class Program
{
#if BUNDLED_SERVER
    public static bool BundledServer => true;
#else
    public static bool BundledServer => false;
#endif

    [STAThread]
    static int Main(string[] args)
    {
#if BUNDLED_SERVER
        // 同一 EXE 的后台模式不创建启动器窗口或 launcher.lock。
        if (args is ["--server", ..])
        {
            try
            {
                Console.OutputEncoding = new System.Text.UTF8Encoding(false);
                return DfoServer.Application.ServerApplication.RunAsync(args[1..]).GetAwaiter().GetResult();
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
#endif
        if (args.Length != 0)
        {
            if (args is ["--selftest-gm"])
            {
                try { GmWebTests.RunAsync().GetAwaiter().GetResult(); return 0; }
                catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            }
            if (args is ["--help"])
            {
                Console.WriteLine(BundledServer
                    ? "S5A9Server：单 EXE 游戏服务。双击打开启动界面；--server [运行参数] [命令] 使用内置服务端；--selftest-gm 检查内置GM网页。"
                    : "DfoLauncher：Windows 游戏服务启动器，管理同目录的 DfoServer.exe；--selftest-gm 检查内置GM网页。");
                return 0;
            }
            Console.Error.WriteLine(BundledServer
                ? "请通过 --server 将运行参数或命令交给内置服务端。"
                : "启动器不接受运行参数；命令行参数请交给 DfoServer.exe。");
            return 2;
        }
        ApplicationConfiguration.Initialize();
        try
        {
            string root = AppContext.BaseDirectory;
            Directory.CreateDirectory(Path.Combine(root, "Data"));
            // 整个窗口生命周期独占，构建脚本和第二个启动器都不能覆盖本实例。
            using var instance = new FileStream(Path.Combine(root, "Data", "launcher.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Application.Run(new LauncherForm(root));
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("无法打开启动器。请确认目录可写，并关闭该目录已有的启动器。\n\n" + error.Message,
                "S5A9 服务启动器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
