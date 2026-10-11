using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace ServerLauncher;

internal sealed class LauncherForm : Form
{
    readonly string root;
    readonly TextBox serverIp = new() { Text = "127.0.0.1", Dock = DockStyle.Fill, AccessibleName = "服务地址" };
    readonly NumericUpDown channel = Port(23100, "频道端口"), game = Port(23101, "游戏端口");
    readonly CheckBox enableGm = new() { Text = "启用 GM 工具（仅本机）", AutoSize = true, AccessibleName = "启用 GM 工具" };
    readonly NumericUpDown gmPort = Port(23102, "GM 命令端口"), gmWebPort = Port(23103, "GM 网页端口");
    readonly TextBox pvf = new() { Dock = DockStyle.Fill, AccessibleName = "PVF 文件" };
    readonly TextBox data = new() { Dock = DockStyle.Fill, AccessibleName = "存档目录" };
    readonly Button choosePvf = Button("选择文件…"), chooseData = Button("选择目录…");
    readonly Button start = Button("启动服务"), stop = Button("停止服务"), copy = Button("复制地址"), logs = Button("打开日志目录");
    readonly Button openGm = Button("打开 GM 工具");
    readonly Label status = new() { Text = "未启动", AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Right };
    readonly GroupBox settings = new() { Text = "启动设置", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(16) };
    readonly TextBox addresses = new() { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
        Dock = DockStyle.Fill, BackColor = Color.White, ScrollBars = ScrollBars.Vertical,
        Text = "启动后显示实际连接地址。", AccessibleName = "连接地址" };
    readonly RichTextBox output = new() { ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(245, 247, 250), DetectUrls = false, AccessibleName = "运行日志" };
    readonly ConcurrentQueue<string> pendingLogs = new();
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 100 };
    ServerProcess? server;
    GmWebHost? gmWeb;
    CancellationTokenSource? starting;
    Task? transition;
    State state;
    bool closeRequested, allowClose;

    public LauncherForm(string root)
    {
        this.root = root;
        start.FlatStyle = FlatStyle.Flat;
        start.FlatAppearance.BorderSize = 0;
        start.BackColor = Color.FromArgb(36, 95, 185);
        start.ForeColor = Color.White;
        start.UseVisualStyleBackColor = false;
        Text = "S5A9 服务启动器";
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.White;
        int availableHeight = Screen.PrimaryScreen?.WorkingArea.Height ?? 768;
        ClientSize = new Size(960, Math.Max(660, Math.Min(760, availableHeight - 64)));
        MinimumSize = new Size(900, 700);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (float height in new[] { 56f, 292f, 48f, 124f, 24f })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(new Label { Text = "S5A9 服务启动器", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), AutoSize = true });
        heading.Controls.Add(status, 1, 0);
        layout.Controls.Add(heading, 0, 0);

        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 7 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int row = 0; row < 6; row++)
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        fields.Controls.Add(FieldLabel("服务地址"), 0, 0);
        fields.Controls.Add(serverIp, 1, 0);
        fields.SetColumnSpan(serverIp, 3);
        fields.Controls.Add(FieldLabel("频道端口"), 0, 1);
        fields.Controls.Add(channel, 1, 1);
        fields.Controls.Add(FieldLabel("游戏端口"), 2, 1);
        fields.Controls.Add(game, 3, 1);
        fields.Controls.Add(FieldLabel("PVF 文件"), 0, 2);
        fields.Controls.Add(PathField(pvf, choosePvf), 1, 2);
        fields.SetColumnSpan(fields.GetControlFromPosition(1, 2)!, 3);
        fields.Controls.Add(FieldLabel("存档目录"), 0, 3);
        fields.Controls.Add(PathField(data, chooseData), 1, 3);
        fields.SetColumnSpan(fields.GetControlFromPosition(1, 3)!, 3);
        fields.Controls.Add(enableGm, 0, 4);
        fields.SetColumnSpan(enableGm, 4);
        fields.Controls.Add(FieldLabel("GM 端口"), 0, 5);
        fields.Controls.Add(gmPort, 1, 5);
        fields.Controls.Add(FieldLabel("网页端口"), 2, 5);
        fields.Controls.Add(gmWebPort, 3, 5);
        var note = new Label { Text = "空存档目录会新建 game.db。GM 仅供本机管理；勾选后启动服务，再点击“打开 GM 工具”，无需安装 Python。",
            Dock = DockStyle.Fill, ForeColor = Color.DimGray, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        fields.Controls.Add(note, 0, 6);
        fields.SetColumnSpan(note, 4);
        settings.Controls.Add(fields);
        layout.Controls.Add(settings, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        actions.Controls.AddRange([start, stop, openGm, copy, logs]);
        layout.Controls.Add(actions, 0, 2);
        var connections = new GroupBox { Text = "连接地址", Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 12) };
        connections.Controls.Add(addresses);
        layout.Controls.Add(connections, 0, 3);
        layout.Controls.Add(new Label { Text = "运行输出", AutoSize = true, Margin = new Padding(0, 7, 0, 0) }, 0, 4);
        layout.Controls.Add(output, 0, 5);
        layout.Controls.Add(new Label { Text = "关闭窗口会先停止本次启动的服务。存档使用所选目录；日志保存在程序目录的 Logs。",
            AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 6, 0, 0) }, 0, 6);
        Controls.Add(layout);
        pvf.Text = "Data/Pvf/Script.pvf";
        data.Text = "Data";
        LoadSettings();
        enableGm.CheckedChanged += (_, _) => gmPort.Enabled = gmWebPort.Enabled = enableGm.Checked;
        gmPort.Enabled = gmWebPort.Enabled = enableGm.Checked;
        choosePvf.Click += (_, _) => BeginInvoke((Action)(() =>
        {
            using var dialog = new OpenFileDialog { Title = "选择 PVF 资源", Filter = "PVF 文件 (*.pvf)|*.pvf|所有文件 (*.*)|*.*", CheckFileExists = true };
            string current = BrowsePath(pvf.Text);
            if (File.Exists(current))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(current);
                dialog.FileName = Path.GetFileName(current);
            }
            if (dialog.ShowDialog(this) == DialogResult.OK)
                pvf.Text = PortablePath(dialog.FileName);
        }));
        chooseData.Click += (_, _) => BeginInvoke((Action)(() =>
        {
            string current = BrowsePath(data.Text);
            using var dialog = new FolderBrowserDialog { Description = "选择存档目录（服务使用其中的 game.db）", UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(current) ? current : root };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                data.Text = PortablePath(dialog.SelectedPath);
        }));
        start.Click += (_, _) => transition = StartAsync();
        stop.Click += (_, _) =>
        {
            if (state == State.Starting)
            {
                stop.Enabled = false;
                status.Text = "正在取消启动…";
                starting?.Cancel();
            }
            else if (state == State.Running)
                transition = StopAsync();
        };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(addresses.Text); WriteLog("连接地址已复制。"); }
            catch (System.Runtime.InteropServices.ExternalException) { WriteLog("剪贴板正被占用，请稍后再试。"); }
        };
        openGm.Click += (_, _) =>
        {
            if (state != State.Running || gmWeb is null || closeRequested) return;
            try
            {
                Process.Start(new ProcessStartInfo(gmWeb.Url) { UseShellExecute = true });
                WriteLog("GM 工具：" + gmWeb.Url);
            }
            catch (System.ComponentModel.Win32Exception error) { WriteLog("无法打开浏览器，请使用连接地址中的 GM 网页地址：" + error.Message); }
        };
        logs.Click += (_, _) =>
        {
            string directory = Path.Combine(root, "Logs");
            if (!Directory.Exists(directory))
                WriteLog("尚未生成日志；启动服务后可打开日志目录。");
            else
            {
                try { Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception error) { WriteLog("无法打开日志目录：" + error.Message); }
            }
        };
        refresh.Tick += (_, _) =>
        {
            FlushLogs();
            if (state == State.Running && server?.HasExited == true)
                transition = StopAsync("游戏服务已退出。");
        };
        refresh.Start();
        FormClosing += ClosingAsync;
        FormClosed += (_, _) => { refresh.Stop(); refresh.Dispose(); };
        SetState(State.Idle);
    }

    async Task StartAsync()
    {
        if (state != State.Idle || closeRequested)
            return;
        var options = new LaunchSettings(serverIp.Text.Trim(), (int)channel.Value, (int)game.Value, pvf.Text.Trim(), data.Text.Trim())
        { GmEnabled = enableGm.Checked, GmPort = (int)gmPort.Value, GmWebPort = (int)gmWebPort.Value };
        SetState(State.Starting);
        addresses.Text = "正在启动，等待服务确认就绪…";
        starting = new CancellationTokenSource();
        server = new ServerProcess(root, WriteLog);
        try
        {
            var ownedServer = server;
            var token = starting.Token;
            // 文件与端口预检查也放在线程池执行，网络盘延迟不能阻塞窗口消息。
            var ready = await Task.Run(() => ownedServer.StartAsync(options, token));
            if (options.GmEnabled)
            {
                gmWeb = new GmWebHost(ready.GmPort, options.GmWebPort, WriteLog, () => !ownedServer.HasExited,
                    ownedServer.OwnsGmConnection);
                WriteLog("GM 工具已就绪：" + gmWeb.Url);
            }
            string ip = ready.AdvertisedAddress ?? options.ServerIp;
            addresses.Text = $"频道：{ip}:{ready.ChannelPort}\r\n游戏：{ip}:{ready.GamePort}\r\n存档：{ready.DataDirectory ?? Path.GetFullPath(options.DataDirectory, root)}";
            if (gmWeb is not null) addresses.Text += $"\r\nGM 工具：{gmWeb.Url}";
            try
            {
                var remembered = options with { PvfPath = PortablePath(options.PvfPath), DataDirectory = PortablePath(options.DataDirectory) };
                await File.WriteAllTextAsync(Path.Combine(root, "launcher.local.json"), JsonSerializer.Serialize(remembered, new JsonSerializerOptions { WriteIndented = true }), token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                WriteLog("服务已启动，但本次设置无法保存：" + error.Message);
            }
            starting.Token.ThrowIfCancellationRequested();
            WriteLog("游戏服务已就绪。");
            SetState(State.Running);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
        {
            string message = error is OperationCanceledException
                ? starting.IsCancellationRequested ? "已取消启动。" : "启动等待超过 45 秒，请检查资源和日志。"
                : "启动失败：" + error.Message;
            WriteLog(message);
            await ReleaseServerAsync();
            addresses.Text = message;
            SetState(State.Idle, error is OperationCanceledException ? "未启动" : "启动失败");
        }
        finally
        {
            starting.Dispose();
            starting = null;
        }
    }

    async Task StopAsync(string message = "服务已停止。")
    {
        SetState(State.Stopping);
        WriteLog("正在停止服务，等待连接和存档收尾…");
        await ReleaseServerAsync();
        addresses.Text = "服务已停止；可修改设置后再次启动。";
        WriteLog(message);
        SetState(State.Idle);
    }

    async Task ReleaseServerAsync()
    {
        if (gmWeb is not null)
        {
            var ownedWeb = gmWeb;
            gmWeb = null;
            try { await ownedWeb.DisposeAsync(); }
            catch (Exception error) when (error is IOException or SocketException)
            {
                WriteLog("GM 网页关闭时发生错误：" + error.Message);
            }
        }
        if (server is null)
            return;
        try { await server.DisposeAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            WriteLog("停止服务发生错误：" + error.Message);
        }
        server = null;
    }

    async void ClosingAsync(object? sender, FormClosingEventArgs args)
    {
        if (allowClose)
            return;
        args.Cancel = true;
        if (closeRequested)
            return;
        closeRequested = true;
        start.Enabled = stop.Enabled = openGm.Enabled = false;
        starting?.Cancel();
        if (transition is not null)
            await transition;
        if (server is not null)
            await StopAsync();
        allowClose = true;
        Close();
    }

    void SetState(State value, string? text = null)
    {
        state = value;
        settings.Enabled = value == State.Idle && !closeRequested;
        start.Enabled = value == State.Idle && !closeRequested;
        stop.Enabled = value is State.Starting or State.Running && !closeRequested;
        stop.Text = value == State.Starting ? "取消启动" : "停止服务";
        copy.Enabled = value == State.Running;
        openGm.Enabled = value == State.Running && gmWeb is not null && !closeRequested;
        status.Text = text ?? value switch { State.Idle => "未启动", State.Starting => "正在启动…", State.Running => "运行中", _ => "正在停止…" };
        status.ForeColor = text == "启动失败" ? Color.Firebrick : value switch
        {
            State.Running => Color.FromArgb(20, 115, 80), State.Starting or State.Stopping => Color.FromArgb(170, 100, 20), _ => Color.DimGray
        };
    }

    void WriteLog(string message)
    {
        while (pendingLogs.Count >= 500)
            pendingLogs.TryDequeue(out _);
        pendingLogs.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    void LoadSettings()
    {
        string path = Path.Combine(root, "launcher.local.json");
        if (!File.Exists(path))
            return;
        try
        {
            var saved = JsonSerializer.Deserialize<LaunchSettings>(File.ReadAllText(path));
            if (saved is null)
                throw new InvalidDataException("设置为空");
            saved.Validate();
            serverIp.Text = saved.ServerIp;
            channel.Value = saved.ChannelPort;
            game.Value = saved.GamePort;
            enableGm.Checked = saved.GmEnabled;
            gmPort.Value = saved.GmPort;
            gmWebPort.Value = saved.GmWebPort;
            pvf.Text = saved.PvfPath;
            data.Text = saved.DataDirectory;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            WriteLog("已恢复默认设置；上次设置无法读取：" + error.Message);
        }
    }

    string PortablePath(string path)
    {
        if (!Path.IsPathRooted(path))
            return path;
        string absolute = Path.GetFullPath(path, root);
        string relative = Path.GetRelativePath(root, absolute);
        return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? absolute : relative;
    }

    string BrowsePath(string path)
    {
        try { return Path.GetFullPath(path, root); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            WriteLog("当前路径无效，请重新选择文件或目录。");
            return root;
        }
    }

    void FlushLogs()
    {
        var batch = new System.Text.StringBuilder();
        for (int count = 0; count < 30 && pendingLogs.TryDequeue(out string? line); count++)
            batch.Append(line);
        if (batch.Length == 0)
            return;
        bool follow = output.SelectionStart + output.SelectionLength == output.TextLength;
        int selectedStart = output.SelectionStart, selectedLength = output.SelectionLength;
        var position = new ScrollPosition();
        if (!follow)
            SendMessage(output.Handle, 0x04DD, 0, ref position); // EM_GETSCROLLPOS
        output.AppendText(batch.ToString());
        int removed = 0;
        if (output.TextLength > 64000)
        {
            removed = output.TextLength - 48000;
            output.Select(0, removed);
            output.SelectedText = "";
        }
        if (follow)
        {
            output.Select(output.TextLength, 0);
            output.ScrollToCaret();
        }
        else
        {
            int selection = Math.Clamp(selectedStart - removed, 0, output.TextLength);
            output.Select(selection, Math.Min(selectedLength, output.TextLength - selection));
            SendMessage(output.Handle, 0x04DE, 0, ref position); // EM_SETSCROLLPOS
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ScrollPosition { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    static extern nint SendMessage(nint window, uint message, nint argument, ref ScrollPosition point);

    static NumericUpDown Port(int value, string name) => new() { Minimum = 1, Maximum = 65535, Value = value,
        Dock = DockStyle.Fill, AccessibleName = name };
    static Label FieldLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };
    static Button Button(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(112, 34), Margin = new Padding(0, 0, 12, 0) };
    static Panel PathField(TextBox field, Button browse)
    {
        var row = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0), Height = 30 };
        browse.AutoSize = false;
        browse.MinimumSize = new Size(100, 28);
        browse.Margin = new Padding(8, 0, 0, 0);
        browse.Size = new Size(105, 28);
        browse.Dock = DockStyle.Right;
        row.Controls.Add(field);
        row.Controls.Add(browse);
        return row;
    }
    enum State { Idle, Starting, Running, Stopping }
}
