namespace ServerLauncher;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 0)
        {
            if (args is ["--help"])
            {
                Console.WriteLine("DfoLauncher：Windows 游戏服务启动器，管理同目录的 DfoServer.exe。");
                return 0;
            }
            Console.Error.WriteLine("启动器不接受运行参数；命令行参数请交给 DfoServer.exe。");
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
