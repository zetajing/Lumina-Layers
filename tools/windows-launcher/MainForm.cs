using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LuminaLauncher
{
    public sealed class MainForm : Form
    {
        private readonly string configFile;
        private LauncherSettings settings;
        private LauncherController controller;
        private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
        private readonly Label status = new Label();
        private readonly Label backend = new Label();
        private readonly Label frontend = new Label();
        private readonly Label detail = new Label();
        private readonly Button start = MakeButton("启动服务", Color.FromArgb(37, 99, 235), Color.White);
        private readonly Button stop = MakeButton("停止服务", Color.FromArgb(254, 242, 242), Color.FromArgb(185, 28, 28));
        private readonly Button open = MakeButton("打开 Lumina 网页", Color.White, Color.FromArgb(30, 41, 59));
        private readonly Button configure = MakeButton("设置", Color.White, Color.FromArgb(71, 85, 105));
        private readonly Button toggleLog = MakeButton("查看日志", Color.White, Color.FromArgb(71, 85, 105));
        private readonly TextBox log = new TextBox();
        private readonly TableLayoutPanel layout = new TableLayoutPanel();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly Timer timer = new Timer();
        private readonly ToolStripMenuItem exitMenu = new ToolStripMenuItem("退出并停止服务");
        private bool busy;
        private bool refreshing;
        private bool exiting;
        private bool logVisible;
        private bool trayHintShown;

        public MainForm(string configFile)
        {
            this.configFile = configFile;
            Text = "Lumina 启动器";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 367);
            BackColor = Color.FromArgb(245, 247, 251);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(24, 16, 24, 14);
            layout.ColumnCount = 1;
            layout.RowCount = 7;
            foreach (int height in new[] { 72, 96, 66, 42, 29, 0 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            var header = new Panel { Dock = DockStyle.Fill };
            header.Controls.Add(new Label { Text = "Lumina 启动器", Font = new Font(Font.FontFamily, 20F, FontStyle.Bold), AutoSize = true, Location = new Point(0, 0), ForeColor = Color.FromArgb(15, 23, 42) });
            header.Controls.Add(new Label { Text = "本机服务 · 一键启动与停止", AutoSize = true, Location = new Point(2, 43), ForeColor = Color.FromArgb(100, 116, 139) });
            status.Text = "检测中";
            status.AutoSize = false;
            status.TextAlign = ContentAlignment.MiddleRight;
            status.Dock = DockStyle.Right;
            status.Width = 130;
            status.Font = new Font(Font, FontStyle.Bold);
            header.Controls.Add(status);
            layout.Controls.Add(header, 0, 0);

            var services = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18, 8, 18, 8), ColumnCount = 2, RowCount = 2 };
            services.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            services.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            services.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            services.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            backend.Dock = frontend.Dock = DockStyle.Fill;
            backend.TextAlign = frontend.TextAlign = ContentAlignment.MiddleLeft;
            services.Controls.Add(backend, 0, 0);
            services.Controls.Add(frontend, 0, 1);
            services.Controls.Add(ServiceAddress("127.0.0.1:8000"), 1, 0);
            services.Controls.Add(ServiceAddress("localhost:4174"), 1, 1);
            layout.Controls.Add(services, 0, 1);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 16, 0, 4), ColumnCount = 3, RowCount = 1 };
            for (int i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            actions.Controls.Add(start, 0, 0); actions.Controls.Add(stop, 1, 0); actions.Controls.Add(open, 2, 0);
            layout.Controls.Add(actions, 0, 2);
            start.Click += async (s, e) => await RunOperationAsync(true);
            stop.Click += async (s, e) => await RunOperationAsync(false);
            open.Click += (s, e) => OpenPage();

            var tools = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            tools.Controls.Add(toggleLog, 0, 0);
            tools.Controls.Add(configure, 2, 0);
            layout.Controls.Add(tools, 0, 3);
            configure.Click += (s, e) => ShowSettings();
            toggleLog.Click += (s, e) => {
                logVisible = !logVisible;
                log.Visible = logVisible;
                toggleLog.Text = logVisible ? "收起日志" : "查看日志";
                ClientSize = new Size(ClientSize.Width, ClientSize.Height + (logVisible ? 220 : -220));
            };
            detail.Text = "正在检测本机服务…";
            detail.Dock = DockStyle.Fill;
            detail.AutoEllipsis = true;
            detail.ForeColor = Color.FromArgb(71, 85, 105);
            detail.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(detail, 0, 4);
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.WordWrap = false;
            log.Dock = DockStyle.Fill;
            log.BackColor = Color.FromArgb(15, 23, 42);
            log.ForeColor = Color.FromArgb(226, 232, 240);
            log.Font = new Font("Consolas", 9F);
            log.Visible = false;
            layout.Controls.Add(log, 0, 6);
            layout.Controls.Add(new Label { Text = "关闭窗口后留在托盘；托盘菜单可退出并停止服务。", ForeColor = Color.FromArgb(100, 116, 139), Dock = DockStyle.Bottom, AutoSize = true }, 0, 5);
            layout.RowStyles[5].Height = 23;

            var menu = new ContextMenuStrip();
            menu.Items.Add("显示启动器", null, (s, e) => RestoreWindow());
            menu.Items.Add("打开 Lumina 网页", null, (s, e) => OpenPage());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitMenu);
            exitMenu.Click += async (s, e) => {
                if (busy) return;
                if (await RunOperationAsync(false)) { exiting = true; Close(); }
            };
            tray.Icon = Icon;
            tray.Text = "Lumina 启动器";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += (s, e) => RestoreWindow();
            FormClosing += (s, e) => {
                if (!exiting && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    if (!trayHintShown) { tray.ShowBalloonTip(2500, "Lumina 启动器", "已收起到托盘，服务保持运行。双击托盘图标可重新打开。", ToolTipIcon.Info); trayHintShown = true; }
                }
            };
            FormClosed += (s, e) => { timer.Stop(); tray.Dispose(); controller.Dispose(); };
            LoadController();
            timer.Interval = 2000;
            timer.Tick += async (s, e) => await RefreshStatusAsync();
            Shown += async (s, e) => { timer.Start(); await RefreshStatusAsync(); };
        }

        private static Button MakeButton(string text, Color background, Color foreground)
        {
            var button = new Button { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = background, ForeColor = foreground, Cursor = Cursors.Hand, Margin = new Padding(3), UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }

        private Label ServiceAddress(string text)
        { return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(100, 116, 139), AutoEllipsis = true }; }

        private void LoadController()
        {
            settings = LauncherSettings.Load(configFile);
            controller = new LauncherController(settings, Path.Combine(Path.GetDirectoryName(configFile), "logs"));
            controller.LogReceived += messages.Enqueue;
        }

        private async Task RefreshStatusAsync()
        {
            FlushLog();
            if (busy || refreshing) return;
            refreshing = true;
            try
            {
                ServiceState state = await controller.ProbeAsync();
                if (IsDisposed || busy) return;
                status.Text = state.Ready ? "● 运行中" : state.Any ? "● 部分运行" : "● 已停止";
                status.ForeColor = state.Ready ? Color.FromArgb(5, 150, 105) : state.Any ? Color.FromArgb(217, 119, 6) : Color.FromArgb(100, 116, 139);
                backend.Text = state.Backend ? "● 后端服务　正常" : "○ 后端服务　未就绪";
                frontend.Text = state.Frontend ? "● 网页服务　正常" : "○ 网页服务　未就绪";
                backend.ForeColor = state.Backend ? Color.FromArgb(5, 150, 105) : Color.FromArgb(100, 116, 139);
                frontend.ForeColor = state.Frontend ? Color.FromArgb(5, 150, 105) : Color.FromArgb(100, 116, 139);
                start.Enabled = !state.Ready;
                open.Enabled = state.Frontend;
                configure.Enabled = !state.Any;
                if (detail.Text == "正在检测本机服务…")
                    detail.Text = state.Ready ? "已连接本机 Lumina，点击“打开 Lumina 网页”即可使用。" : "点击“启动服务”，就绪后即可打开网页。";
            }
            finally { refreshing = false; }
        }

        private async Task<bool> RunOperationAsync(bool launch)
        {
            if (busy) return false;
            busy = true;
            start.Enabled = stop.Enabled = configure.Enabled = exitMenu.Enabled = false;
            status.Text = launch ? "● 启动中" : "● 停止中";
            status.ForeColor = Color.FromArgb(37, 99, 235);
            detail.Text = launch ? "正在启动前后端，请稍候…" : "正在停止当前项目的服务…";
            bool success = false;
            try
            {
                if (launch) await controller.StartAsync(); else await controller.StopAsync();
                detail.Text = launch ? "服务已就绪，点击“打开 Lumina 网页”即可使用。" : "服务已停止，耗材库和项目文件保留在本机。";
                success = true;
            }
            catch (Exception ex)
            {
                detail.Text = ex.Message;
                controller.Log("操作失败 · " + ex.Message);
                MessageBox.Show(this, ex.Message, "Lumina 启动器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                busy = false;
                stop.Enabled = exitMenu.Enabled = true;
                FlushLog();
            }
            await RefreshStatusAsync();
            return success;
        }

        private void FlushLog()
        {
            string line;
            while (messages.TryDequeue(out line)) log.AppendText(line + Environment.NewLine);
            if (log.TextLength > 100000) log.Text = log.Text.Substring(log.TextLength - 60000);
        }

        private void OpenPage()
        {
            try { Process.Start(new ProcessStartInfo(LauncherController.PageUrl) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开浏览器"); }
        }

        private void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        private void ShowSettings()
        {
            using (var dialog = new SettingsForm(settings))
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    dialog.Settings.Save(configFile);
                    controller.Dispose();
                    LoadController();
                    detail.Text = "设置已保存，点击启动服务即可。";
                }
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly TextBox project = new TextBox();
        private readonly TextBox python = new TextBox();
        private readonly TextBox node = new TextBox();
        public LauncherSettings Settings { get; private set; }

        public SettingsForm(LauncherSettings current)
        {
            Text = "启动器设置";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(580, 270);
            project.Text = current.ProjectDirectory;
            python.Text = current.PythonExecutable;
            node.Text = current.NodeExecutable;
            AddField("Lumina 项目目录", project, 16, true);
            AddField("Python 运行环境", python, 83, false);
            AddField("Node.js", node, 150, false);
            var save = new Button { Text = "保存", Location = new Point(474, 228), Size = new Size(85, 30) };
            save.Click += (s, e) => {
                try {
                    Settings = new LauncherSettings { ProjectDirectory = Path.GetFullPath(project.Text.Trim()), PythonExecutable = Path.GetFullPath(python.Text.Trim()), NodeExecutable = Path.GetFullPath(node.Text.Trim()) };
                    Settings.Validate();
                    DialogResult = DialogResult.OK;
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "请检查设置"); }
            };
            Controls.Add(save);
            AcceptButton = save;
        }

        private void AddField(string caption, TextBox field, int top, bool directory)
        {
            Controls.Add(new Label { Text = caption, Location = new Point(20, top), AutoSize = true });
            field.Location = new Point(20, top + 25); field.Size = new Size(445, 25); Controls.Add(field);
            var browse = new Button { Text = "浏览…", Location = new Point(474, top + 23), Size = new Size(85, 29) };
            browse.Click += (s, e) => {
                if (directory)
                {
                    using (var picker = new FolderBrowserDialog { Description = "选择 Lumina-Layers 项目目录", SelectedPath = field.Text })
                        if (picker.ShowDialog(this) == DialogResult.OK) field.Text = picker.SelectedPath;
                }
                else
                {
                    using (var picker = new OpenFileDialog { Filter = "可执行文件 (*.exe)|*.exe", FileName = field.Text })
                        if (picker.ShowDialog(this) == DialogResult.OK) field.Text = picker.FileName;
                }
            };
            Controls.Add(browse);
        }
    }
}
