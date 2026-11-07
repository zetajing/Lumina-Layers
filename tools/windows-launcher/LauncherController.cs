using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LuminaLauncher
{
    public sealed class LauncherSettings
    {
        public string ProjectDirectory { get; set; }
        public string PythonExecutable { get; set; }
        public string NodeExecutable { get; set; }

        public LauncherSettings()
        {
            ProjectDirectory = "";
            PythonExecutable = "";
            NodeExecutable = "";
        }

        public static LauncherSettings Load(string file)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(file));
            var settings = File.Exists(file)
                ? new JavaScriptSerializer().Deserialize<LauncherSettings>(File.ReadAllText(file, Encoding.UTF8))
                : new LauncherSettings();
            if (settings == null) throw new InvalidDataException("启动器配置为空。");
            if (string.IsNullOrWhiteSpace(settings.ProjectDirectory))
            {
                string candidate = Path.Combine(folder, "Lumina-Layers");
                if (File.Exists(Path.Combine(candidate, "api_server.py"))) settings.ProjectDirectory = candidate;
                else
                {
                    var parent = new DirectoryInfo(folder);
                    for (int i = 0; parent != null && i < 5; i++, parent = parent.Parent)
                        if (File.Exists(Path.Combine(parent.FullName, "api_server.py")))
                        { settings.ProjectDirectory = parent.FullName; break; }
                }
            }
            settings.ProjectDirectory = ResolvePath(folder, settings.ProjectDirectory);
            settings.PythonExecutable = ResolvePath(folder, settings.PythonExecutable);
            if (string.IsNullOrEmpty(settings.PythonExecutable) && !string.IsNullOrEmpty(settings.ProjectDirectory))
                foreach (string env in new[] { ".venv312", ".venv", "venv" })
                {
                    string python = Path.Combine(settings.ProjectDirectory, env, "Scripts", "python.exe");
                    if (File.Exists(python)) { settings.PythonExecutable = python; break; }
                }
            settings.NodeExecutable = ResolvePath(folder, settings.NodeExecutable);
            if (string.IsNullOrEmpty(settings.NodeExecutable))
            {
                var folders = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs") };
                folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
                foreach (string path in folders.Where(p => !string.IsNullOrWhiteSpace(p)))
                {
                    string node = Path.Combine(path.Trim().Trim('"'), "node.exe");
                    if (File.Exists(node)) { settings.NodeExecutable = node; break; }
                }
            }
            return settings;
        }

        private static string ResolvePath(string folder, string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : Path.GetFullPath(Path.Combine(folder, value.Trim()));
        }

        public void Validate()
        {
            if (!File.Exists(Path.Combine(ProjectDirectory, "api_server.py")))
                throw new InvalidOperationException("请选择包含 api_server.py 的 Lumina 项目目录。");
            if (!File.Exists(PythonExecutable)) throw new InvalidOperationException("找不到 Python 运行环境，请在设置中选择 python.exe。");
            if (!File.Exists(NodeExecutable)) throw new InvalidOperationException("找不到 Node.js，请在设置中选择 node.exe。");
            if (!File.Exists(Path.Combine(ProjectDirectory, "frontend", "node_modules", "vite", "bin", "vite.js")))
                throw new InvalidOperationException("前端依赖尚未安装，请先在项目中安装 frontend 依赖。");
        }

        public void Save(string file)
        {
            File.WriteAllText(file, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
        }
    }

    public sealed class ServiceState
    {
        public bool Backend;
        public bool Frontend;
        public bool Ready { get { return Backend && Frontend; } }
        public bool Any { get { return Backend || Frontend; } }
    }

    public sealed class LauncherController : IDisposable
    {
        private readonly LauncherSettings settings;
        private readonly HttpClient http;
        private readonly object logLock = new object();
        private readonly string logFile;
        public event Action<string> LogReceived;
        public const string PageUrl = "http://localhost:4174/";

        public LauncherController(LauncherSettings settings, string logDirectory)
        {
            this.settings = settings;
            Directory.CreateDirectory(logDirectory);
            logFile = Path.Combine(logDirectory, "launcher-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            http = new HttpClient(new HttpClientHandler { UseProxy = false });
            http.Timeout = TimeSpan.FromSeconds(2);
        }

        public void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            lock (logLock)
            {
                try { File.AppendAllText(logFile, line + Environment.NewLine, Encoding.UTF8); }
                catch (IOException) { /* The UI log remains available when a file is locked. */ }
            }
            var handler = LogReceived;
            if (handler != null) handler(line);
        }

        public async Task<ServiceState> ProbeAsync()
        {
            Task<bool> backend = ProbeBackendAsync();
            Task<bool> frontend = ProbeFrontendAsync();
            await Task.WhenAll(backend, frontend);
            return new ServiceState { Backend = backend.Result, Frontend = frontend.Result };
        }

        private async Task<bool> ProbeBackendAsync()
        {
            try
            {
                using (var response = await http.GetAsync("http://127.0.0.1:8000/api/health"))
                {
                    if (!response.IsSuccessStatusCode) return false;
                    var body = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync());
                    if (body == null || !body.ContainsKey("status") || Convert.ToString(body["status"]) != "ok" ||
                        !body.ContainsKey("service") || Convert.ToString(body["service"]) != "lumina-studio") return false;
                    object worker;
                    if (!body.TryGetValue("worker_pool", out worker)) return false;
                    var pool = worker as Dictionary<string, object>;
                    return pool != null && pool.ContainsKey("healthy") && Convert.ToBoolean(pool["healthy"]);
                }
            }
            catch (HttpRequestException) { return false; }
            catch (TaskCanceledException) { return false; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private async Task<bool> ProbeFrontendAsync()
        {
            try
            {
                using (var response = await http.GetAsync("http://127.0.0.1:4174/"))
                    return response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("<title>Lumina Studio</title>");
            }
            catch (HttpRequestException) { return false; }
            catch (TaskCanceledException) { return false; }
        }

        private static async Task<bool> PortInUseAsync(int port)
        {
            using (var client = new TcpClient())
            {
                try
                {
                    Task connect = client.ConnectAsync("127.0.0.1", port);
                    if (await Task.WhenAny(connect, Task.Delay(600)) != connect) return false;
                    await connect;
                    return client.Connected;
                }
                catch (SocketException) { return false; }
            }
        }

        public async Task StartAsync()
        {
            settings.Validate();
            ServiceState state = await ProbeAsync();
            if (state.Ready) { Log("服务已经运行，无需重复启动。"); return; }
            if ((!state.Backend && await PortInUseAsync(8000)) || (!state.Frontend && await PortInUseAsync(4174)))
                throw new InvalidOperationException("8000 或 4174 端口被其他程序占用，请先释放端口后重试。");
            var started = new List<Process>();
            Exception failure = null;
            try
            {
                if (!state.Backend)
                    started.Add(StartProcess(settings.PythonExecutable, "-u " + Quote(Path.Combine(settings.ProjectDirectory, "api_server.py")), settings.ProjectDirectory, "后端"));
                if (!state.Frontend)
                    started.Add(StartProcess(settings.NodeExecutable, Quote(Path.Combine(settings.ProjectDirectory, "frontend", "node_modules", "vite", "bin", "vite.js")) + " --host 127.0.0.1 --port 4174 --strictPort", Path.Combine(settings.ProjectDirectory, "frontend"), "前端"));
                DateTime deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    foreach (Process process in started)
                        if (process.HasExited) throw new InvalidOperationException("服务启动后退出，请展开日志查看原因。");
                    if ((await ProbeAsync()).Ready) { Log("前后端已就绪，可以打开网页。"); return; }
                    await Task.Delay(700);
                }
                throw new TimeoutException("等待服务就绪超时，请查看日志后重试。");
            }
            catch (Exception ex) { failure = ex; }
            foreach (Process process in started)
                if (!process.HasExited) await Task.Run(() => KillTree(process.Id));
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private Process StartProcess(string executable, string arguments, string directory, string label)
        {
            var start = new ProcessStartInfo(executable, arguments) {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["PYTHONUTF8"] = "1";
            start.EnvironmentVariables["LUMINA_API_HOST"] = "127.0.0.1";
            start.EnvironmentVariables["LUMINA_API_PORT"] = "8000";
            start.EnvironmentVariables["VITE_API_PROXY_TARGET"] = "http://127.0.0.1:8000";
            start.EnvironmentVariables.Remove("PORT");
            start.EnvironmentVariables.Remove("VITE_DEV_PORT");
            var process = new Process { StartInfo = start };
            process.OutputDataReceived += (sender, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(label + " · " + e.Data); };
            process.ErrorDataReceived += (sender, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(label + " · " + e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Log(label + "已启动，PID " + process.Id);
            return process;
        }

        private static string Quote(string value) { return "\"" + value + "\""; }

        private static void KillTree(int id)
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + id + " /T /F") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                }))
                {
                    process.OutputDataReceived += (s, e) => { };
                    process.ErrorDataReceived += (s, e) => { };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(15000)) throw new TimeoutException("停止服务进程超时。");
                }
            }
            catch (System.ComponentModel.Win32Exception ex) { throw new InvalidOperationException("无法停止服务进程。", ex); }
        }

        public async Task StopAsync()
        {
            if (string.IsNullOrWhiteSpace(settings.ProjectDirectory) || !File.Exists(Path.Combine(settings.ProjectDirectory, "api_server.py")))
            {
                Log("尚未设置有效项目，没有停止任何进程。");
                return;
            }
            // Match the configured installation; never terminate a process just for owning a port.
            string backendScript = Path.Combine(settings.ProjectDirectory, "api_server.py");
            string launcherScript = Path.Combine(settings.ProjectDirectory, "start_all.py");
            string frontend = Path.Combine(settings.ProjectDirectory, "frontend");
            int[] ids = await Task.Run(() => {
                var matches = new List<int>();
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='python.exe' OR Name='node.exe' OR Name='esbuild.exe'"))
                using (ManagementObjectCollection rows = searcher.Get())
                    foreach (ManagementObject row in rows)
                    using (row)
                    {
                        string name = Convert.ToString(row["Name"]);
                        string command = Convert.ToString(row["CommandLine"]);
                        bool python = name.Equals("python.exe", StringComparison.OrdinalIgnoreCase) &&
                            (Contains(command, backendScript) || Contains(command, launcherScript));
                        bool node = !name.Equals("python.exe", StringComparison.OrdinalIgnoreCase) && Contains(command, frontend + Path.DirectorySeparatorChar);
                        if (python || node) matches.Add(Convert.ToInt32(row["ProcessId"]));
                    }
                return matches.ToArray();
            });
            foreach (int id in ids) await Task.Run(() => KillTree(id));
            for (int attempt = 0; attempt < 12; attempt++)
            {
                if (!(await ProbeAsync()).Any) { Log("当前项目的前后端服务已停止。"); return; }
                await Task.Delay(350);
            }
            throw new InvalidOperationException("当前项目进程已处理，但端口仍有服务响应。请查看状态，其他程序未被停止。");
        }

        private static bool Contains(string value, string part)
        { return !string.IsNullOrEmpty(part) && value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0; }

        public void Dispose() { http.Dispose(); }
    }
}
