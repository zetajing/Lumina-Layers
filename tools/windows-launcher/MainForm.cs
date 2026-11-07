using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace LuminaLauncher
{
    public sealed class MainForm : Form
    {
        private readonly string configFile;
        private LauncherSettings settings;
        private LauncherController controller;
        private UpdateController updater;
        private UpdateInfo latest;
        private readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> updateStages = new ConcurrentQueue<string>();
        private readonly Label status = new Label();
        private readonly Label backend = new Label();
        private readonly Label frontend = new Label();
        private readonly Label detail = new Label();
        private readonly Button start = MakeButton("启动服务", Color.FromArgb(37, 99, 235), Color.White);
        private readonly Button stop = MakeButton("停止服务", Color.FromArgb(254, 242, 242), Color.FromArgb(185, 28, 28));
        private readonly Button open = MakeButton("打开 Lumina 网页", Color.White, Color.FromArgb(30, 41, 59));
        private readonly Button configure = MakeButton("设置", Color.White, Color.FromArgb(71, 85, 105));
        private readonly Button toggleLog = MakeButton("查看日志", Color.White, Color.FromArgb(71, 85, 105));
        private readonly Button checkUpdates = MakeButton("检查更新", Color.FromArgb(239, 246, 255), Color.FromArgb(37, 99, 235));
        private readonly Button updateNow = MakeButton("一键更新", Color.FromArgb(37, 99, 235), Color.White);
        private readonly Label version = new Label();
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

        public MainForm(string configFile, bool resumeServices)
        {
            this.configFile = configFile;
            Text = "Lumina 启动器";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 475);
            BackColor = Color.FromArgb(245, 247, 251);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(24, 16, 24, 14);
            layout.ColumnCount = 1;
            layout.RowCount = 8;
            foreach (int height in new[] { 72, 96, 66, 108, 42, 29, 23 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
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

            var updates = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(12, 6, 12, 6), ColumnCount = 2, RowCount = 3 };
            updates.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            updates.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            updates.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            updates.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            updates.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            version.Text = "版本更新 · 尚未检查";
            version.Dock = DockStyle.Fill;
            version.TextAlign = ContentAlignment.MiddleLeft;
            version.AutoEllipsis = true;
            updates.Controls.Add(version, 0, 0); updates.SetColumnSpan(version, 2);
            updates.Controls.Add(checkUpdates, 0, 1); updates.Controls.Add(updateNow, 1, 1);
            var updateHint = new Label { Text = "请先保存网页工程；一键更新会自动备份并重启服务。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Color.FromArgb(100, 116, 139) };
            updates.Controls.Add(updateHint, 0, 2); updates.SetColumnSpan(updateHint, 2);
            layout.Controls.Add(updates, 0, 3);
            checkUpdates.Click += async (s, e) => await RunUpdateAsync(false);
            updateNow.Click += async (s, e) => await RunUpdateAsync(true);

            var tools = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            tools.Controls.Add(toggleLog, 0, 0);
            tools.Controls.Add(configure, 2, 0);
            layout.Controls.Add(tools, 0, 4);
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
            layout.Controls.Add(detail, 0, 5);
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Both;
            log.WordWrap = false;
            log.Dock = DockStyle.Fill;
            log.BackColor = Color.FromArgb(15, 23, 42);
            log.ForeColor = Color.FromArgb(226, 232, 240);
            log.Font = new Font("Consolas", 9F);
            log.Visible = false;
            layout.Controls.Add(log, 0, 7);
            layout.Controls.Add(new Label { Text = "关闭窗口后留在托盘；托盘菜单可退出并停止服务。", ForeColor = Color.FromArgb(100, 116, 139), Dock = DockStyle.Bottom, AutoSize = true }, 0, 6);

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
            Shown += async (s, e) => {
                timer.Start();
                await RefreshStatusAsync();
                ShowUpdateReceipt();
                if (resumeServices) await RunOperationAsync(true);
            };
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
            updater = new UpdateController(settings, controller, configFile);
            updater.Progress += updateStages.Enqueue;
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
            start.Enabled = stop.Enabled = configure.Enabled = exitMenu.Enabled = checkUpdates.Enabled = updateNow.Enabled = false;
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
                checkUpdates.Enabled = true;
                updateNow.Enabled = latest == null || (latest.Available && !latest.HasLocalChanges);
                FlushLog();
            }
            await RefreshStatusAsync();
            return success;
        }

        private void FlushLog()
        {
            string line;
            while (messages.TryDequeue(out line)) log.AppendText(line + Environment.NewLine);
            while (updateStages.TryDequeue(out line)) if (busy) detail.Text = line;
            if (log.TextLength > 100000) log.Text = log.Text.Substring(log.TextLength - 60000);
        }

        private void OpenPage()
        {
            try { Process.Start(new ProcessStartInfo(LauncherController.PageUrl) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开浏览器"); }
        }

        private void RestoreWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

        private void ShowUpdateReceipt()
        {
            string receipt = Path.Combine(Path.GetDirectoryName(configFile), "launcher-update-result.json");
            if (!File.Exists(receipt)) return;
            try
            {
                var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(receipt));
                if (result != null && result.ContainsKey("success") && !Convert.ToBoolean(result["success"]))
                    MessageBox.Show(this, Convert.ToString(result["message"]), "启动器替换未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                File.Delete(receipt);
            }
            catch (Exception ex) { controller.Log("读取启动器更新结果失败 · " + ex.Message); }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == Program.ShowMessage) RestoreWindow();
            base.WndProc(ref message);
        }

        private void ShowVersion(UpdateInfo info)
        {
            latest = info;
            version.Text = "当前 " + info.CurrentCommit.Substring(0, 7) + " · " +
                (info.Available ? "可更新 " + info.PendingCommits + " 个提交" : "已是最新版本") +
                (info.HasLocalChanges ? " · 有本地改动" : "");
        }

        private async Task RunUpdateAsync(bool apply)
        {
            if (busy) return;
            busy = true;
            start.Enabled = stop.Enabled = open.Enabled = configure.Enabled = exitMenu.Enabled = checkUpdates.Enabled = updateNow.Enabled = false;
            status.Text = apply ? "● 更新中" : "● 检查中";
            status.ForeColor = Color.FromArgb(37, 99, 235);
            UpdateResult result = null;
            try
            {
                if (!apply)
                {
                    var info = await updater.CheckAsync();
                    FlushLog();
                    ShowVersion(info);
                    detail.Text = info.Available ? "点击“一键更新”安装；服务目前保持运行。" : "检查完成，当前没有可用更新。";
                }
                else
                {
                    result = await updater.UpdateAsync();
                    FlushLog();
                    ShowVersion(result.UpdatedCommit == null ? result.Version : new UpdateInfo { CurrentCommit = result.UpdatedCommit });
                    detail.Text = result.BackupDirectory == null ? "当前已是最新版本，无需更新。" : "更新完成，数据备份已保存。";
                    if (result.SyncWarning != null) controller.Log(result.SyncWarning);
                }
            }
            catch (Exception ex)
            {
                FlushLog();
                detail.Text = "更新操作未完成，请查看日志。";
                controller.Log(ex.Message);
                MessageBox.Show(this, ex.Message, "Lumina 更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                result = null;
            }
            finally
            {
                busy = false;
                stop.Enabled = exitMenu.Enabled = checkUpdates.Enabled = true;
                updateNow.Enabled = latest == null || (latest.Available && !latest.HasLocalChanges);
            }
            await RefreshStatusAsync();
            if (result != null && result.StagedLauncherDirectory != null)
            {
                Exception restartFailure = null;
                try
                {
                    string helper = Path.Combine(Path.GetDirectoryName(configFile), "ApplyLauncherUpdate.ps1");
                    var args = new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", helper, "-ParentId", Process.GetCurrentProcess().Id.ToString(), "-StagedDirectory", result.StagedLauncherDirectory, "-TargetDirectory", Path.GetDirectoryName(configFile) };
                    string arguments = string.Join(" ", Array.ConvertAll(args, UpdateController.QuoteArgument));
                    if (result.ResumeServices) arguments += " -ResumeServices";
                    Process.Start(new ProcessStartInfo("powershell.exe", arguments) { UseShellExecute = false, CreateNoWindow = true });
                    exiting = true;
                    Close();
                }
                catch (Exception ex) { restartFailure = ex; }
                if (restartFailure != null)
                {
                    controller.Log("重启启动器失败 · " + restartFailure.Message);
                    if (result.ResumeServices) await RunOperationAsync(true);
                    MessageBox.Show(this, "代码已更新，请手动重启启动器。\n" + restartFailure.Message, "Lumina 更新");
                }
            }
        }

        private void ShowSettings()
        {
            using (var dialog = new SettingsForm(settings))
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    dialog.Settings.Save(configFile);
                    controller.Dispose();
                    LoadController();
                    latest = null;
                    version.Text = "版本更新 · 尚未检查";
                    updateNow.Enabled = true;
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
