using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LuminaLauncher
{
    public sealed class UpdateInfo
    {
        public string CurrentCommit;
        public string Branch;
        public string TrackingRemote;
        public string TrackingBranch;
        public bool HasLocalChanges;
        public int PendingCommits;
        public string RetryCommit;
        public readonly List<string> Targets = new List<string>();
        public bool Available { get { return Targets.Count > 0 || RetryCommit != null; } }
    }

    public sealed class UpdateResult
    {
        public UpdateInfo Version;
        public string BackupDirectory;
        public string StagedLauncherDirectory;
        public bool ResumeServices;
        public string SyncWarning;
        public string UpdatedCommit;
    }

    public sealed class CommandResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
    }

    internal sealed class UpdateRetryState
    {
        public string Head { get; set; }
        public string Target { get; set; }
    }

    public sealed class UpdateController
    {
        private readonly LauncherSettings settings;
        private readonly ILauncherServices services;
        private readonly string configFile;
        private const string LocalLutPath = "lut-preset/";
        private static readonly HashSet<string> CustomLutExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".npy", ".json", ".npz", ".msgpack" };
        public event Action<string> Progress;

        public UpdateController(LauncherSettings settings, ILauncherServices services, string configFile)
        {
            this.settings = settings;
            this.services = services;
            this.configFile = Path.GetFullPath(configFile);
        }

        private void Stage(string text)
        {
            services.Log("更新 · " + text);
            var callback = Progress;
            if (callback != null) callback(text);
        }

        public async Task<UpdateInfo> CheckAsync()
        {
            if (string.IsNullOrWhiteSpace(settings.ProjectDirectory) || !Directory.Exists(settings.ProjectDirectory))
                throw new InvalidOperationException("请先在设置中选择 Lumina 项目目录。");
            string root = (await GitAsync("rev-parse", "--show-toplevel")).Output.Trim();
            if (!Path.GetFullPath(root).TrimEnd('\\', '/').Equals(settings.ProjectDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新目录必须是 Lumina Git 仓库根目录。");
            var info = new UpdateInfo {
                CurrentCommit = (await GitAsync("rev-parse", "HEAD")).Output.Trim(),
                Branch = (await GitAsync("symbolic-ref", "--quiet", "--short", "HEAD")).Output.Trim(),
                HasLocalChanges = await HasLocalChangesAsync()
            };
            if (File.Exists(Path.Combine(root, ".git", "MERGE_HEAD")))
                throw new InvalidOperationException("仓库有尚未完成的合并，请先处理后再更新。");
            Stage("检查官方 Lumina 更新…");
            await GitAsync("fetch", "--no-tags", "origin", "+refs/heads/main:refs/remotes/origin/main");
            await AddTargetAsync(info, "refs/remotes/origin/main");
            var tracking = await RunGitAsync("rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}");
            if (tracking.ExitCode == 0)
            {
                string reference = tracking.Output.Trim();
                int slash = reference.IndexOf('/');
                if (slash > 0)
                {
                    info.TrackingRemote = reference.Substring(0, slash);
                    info.TrackingBranch = reference.Substring(slash + 1);
                    if (info.TrackingRemote != "origin" || info.TrackingBranch != "main")
                    {
                        Stage("检查启动器分支更新…");
                        await GitAsync("fetch", "--no-tags", info.TrackingRemote,
                            "+refs/heads/" + info.TrackingBranch + ":refs/remotes/" + reference);
                        await AddTargetAsync(info, "refs/remotes/" + reference);
                    }
                }
            }
            if (info.Targets.Count > 0)
            {
                var revisions = new List<string> { "rev-list", "--count" };
                revisions.AddRange(info.Targets);
                revisions.Add("^" + info.CurrentCommit);
                info.PendingCommits = int.Parse((await GitAsync(revisions.ToArray())).Output.Trim());
            }
            string retryFile = Path.Combine(Path.GetDirectoryName(configFile), "launcher-update-retry.json");
            if (File.Exists(retryFile))
            {
                var retry = new JavaScriptSerializer().Deserialize<UpdateRetryState>(File.ReadAllText(retryFile, Encoding.UTF8));
                if (retry != null && retry.Head == info.CurrentCommit && retry.Target != null &&
                    retry.Target.Length == 40 && retry.Target.All(Uri.IsHexDigit))
                {
                    info.RetryCommit = retry.Target;
                    info.PendingCommits = Math.Max(1, info.PendingCommits);
                }
            }
            Stage(info.Available ? "发现 " + info.PendingCommits + " 个新提交。" : "当前已是最新版本。");
            return info;
        }

        private async Task AddTargetAsync(UpdateInfo info, string reference)
        {
            string target = (await GitAsync("rev-parse", reference)).Output.Trim();
            int behind = int.Parse((await GitAsync("rev-list", "--count", info.CurrentCommit + ".." + target)).Output.Trim());
            if (behind > 0 && !info.Targets.Contains(target)) info.Targets.Add(target);
        }

        public async Task<UpdateResult> UpdateAsync()
        {
            UpdateInfo info = await CheckAsync();
            var result = new UpdateResult { Version = info };
            if (!info.Available) return result;
            if (info.HasLocalChanges)
                throw new InvalidOperationException("检测到未提交的代码或文件。请先处理这些改动，再重新检查更新。");
            settings.Validate();
            Stage("预先检查合并冲突…");
            string planned = await PrepareMergeAsync(info);
            await RefuseDataOverwriteAsync(info.CurrentCommit, planned);
            string directory = Path.Combine(Path.GetDirectoryName(configFile), "backups", "update-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(directory);
            result.BackupDirectory = directory;
            Stage("备份当前代码、运行环境清单和启动器…");
            await GitAsync("archive", "--format=zip", "--output=" + Path.Combine(directory, "code-before.zip"), info.CurrentCommit);
            File.WriteAllText(Path.Combine(directory, "python-before.txt"), (await RunRequiredAsync(settings.PythonExecutable, 120, "-m", "pip", "freeze")).Output, new UTF8Encoding(false));
            foreach (string name in new[] { "LuminaLauncher.exe", "LuminaLauncher.exe.config", "LuminaLauncher.json", "ApplyLauncherUpdate.ps1" })
            {
                string source = Path.Combine(Path.GetDirectoryName(configFile), name);
                if (File.Exists(source)) File.Copy(source, Path.Combine(directory, name));
            }
            File.WriteAllText(Path.Combine(directory, "update.json"), new JavaScriptSerializer().Serialize(new {
                previous_commit = info.CurrentCommit, planned_commit = planned, branch = info.Branch, project_directory = settings.ProjectDirectory,
                materials_directory = MaterialsDirectory(), environment_file = EnvironmentFile(), created_at = DateTimeOffset.Now.ToString("o")
            }), Encoding.UTF8);
            bool wasRunning = (await services.ProbeAsync()).Any;
            bool stopped = false;
            bool applied = false;
            Exception failure = null;
            try
            {
                Stage("停止服务并备份耗材、本机设置和 LUT…");
                await services.StopAsync();
                stopped = true;
                await Task.Run(() => BackupLocalData(Path.Combine(directory, "local-data.zip")));
                await EnsureUnchangedAsync(info.CurrentCommit);
                await RefuseDataOverwriteAsync(info.CurrentCommit, planned);
                await GitAsync("update-ref", "refs/lumina-backups/" + Path.GetFileName(directory), info.CurrentCommit);
                Stage("应用已检查的更新…");
                await GitAsync("merge", "--ff-only", "--no-edit", "--no-overwrite-ignore", planned);
                applied = true;
                await InstallDependenciesAsync();
                string builder = Path.Combine(settings.ProjectDirectory, "tools", "windows-launcher", "build.ps1");
                if (File.Exists(builder))
                {
                    Stage("编译新启动器…");
                    result.StagedLauncherDirectory = Path.Combine(directory, "launcher-new");
                    await RunRequiredAsync("powershell.exe", 180, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", builder, "-OutputDirectory", result.StagedLauncherDirectory);
                }
                Stage("验证更新后的服务…");
                await services.StartAsync();
                if (!(await services.ProbeAsync()).Ready) throw new InvalidOperationException("更新后的服务健康检查未通过。");
                if (!wasRunning || result.StagedLauncherDirectory != null) await services.StopAsync();
                result.ResumeServices = wasRunning;
            }
            catch (Exception ex) { failure = ex; }
            if (failure != null)
            {
                string recovery = "";
                try
                {
                    if (applied)
                    {
                        Stage("更新失败，恢复上一版代码与依赖…");
                        await services.StopAsync();
                        await EnsureUnchangedAsync(planned);
                        string tree = (await GitAsync("rev-parse", info.CurrentCommit + "^{tree}")).Output.Trim();
                        string rollback = (await GitAsync("commit-tree", tree, "-p", planned, "-m", "fix: restore previous Lumina version after failed update")).Output.Trim();
                        await GitAsync("merge", "--ff-only", "--no-edit", "--no-overwrite-ignore", rollback);
                        File.WriteAllText(Path.Combine(Path.GetDirectoryName(configFile), "launcher-update-retry.json"),
                            new JavaScriptSerializer().Serialize(new UpdateRetryState { Head = rollback, Target = planned }), Encoding.UTF8);
                        await RunRequiredAsync(settings.PythonExecutable, 1800, "-m", "pip", "install", "-r", Path.Combine(directory, "python-before.txt"));
                        await InstallFrontendAsync(false);
                    }
                    if (stopped && wasRunning) await services.StartAsync();
                    recovery = applied ? "已恢复更新前的代码和服务状态。" : "已保留当前代码并恢复服务状态。";
                }
                catch (Exception ex) { recovery = "自动恢复未完成：" + ex.Message + "。请保留备份并查看日志。"; }
                throw new InvalidOperationException("更新失败：" + failure.Message + "\n" + recovery + "\n备份：" + directory, failure);
            }
            result.UpdatedCommit = (await GitAsync("rev-parse", "HEAD")).Output.Trim();
            // Publishing is best-effort after successful local validation; never push upstream main.
            if (info.TrackingRemote != null && info.TrackingRemote != "origin" && info.Branch != "main" && info.Branch != "dev-refactor")
            {
                Stage("同步更新后的个人分支…");
                try { await GitAsync("push", info.TrackingRemote, "HEAD:refs/heads/" + info.TrackingBranch); }
                catch (Exception ex) { result.SyncWarning = "本机更新成功，Git 推送未完成：" + ex.Message; services.Log(result.SyncWarning); }
            }
            Stage("更新完成，备份保存在 " + directory);
            return result;
        }

        private async Task<string> PrepareMergeAsync(UpdateInfo info)
        {
            string staged = info.CurrentCommit;
            if (info.RetryCommit != null)
            {
                string retryTree = (await GitAsync("rev-parse", info.RetryCommit + "^{tree}")).Output.Trim();
                staged = (await GitAsync("commit-tree", retryTree, "-p", staged, "-m", "chore: retry Lumina automatic update")).Output.Trim();
            }
            foreach (string target in info.Targets)
            {
                var alreadyIncluded = await RunGitAsync("merge-base", "--is-ancestor", target, staged);
                if (alreadyIncluded.ExitCode == 0) continue;
                var fastForward = await RunGitAsync("merge-base", "--is-ancestor", staged, target);
                if (fastForward.ExitCode == 0) { staged = target; continue; }
                var merge = await RunGitAsync("merge-tree", "--write-tree", staged, target);
                if (merge.ExitCode != 0)
                {
                    services.Log(merge.Output + merge.Error);
                    throw new InvalidOperationException("更新存在合并冲突，未修改当前代码。请先手动处理分支。");
                }
                string tree = merge.Output.Split('\n')[0].Trim();
                staged = (await GitAsync("commit-tree", tree, "-p", staged, "-p", target, "-m", "chore: merge Lumina update " + target.Substring(0, 12))).Output.Trim();
            }
            return staged;
        }

        private async Task EnsureUnchangedAsync(string expectedHead)
        {
            if ((await GitAsync("rev-parse", "HEAD")).Output.Trim() != expectedHead ||
                await HasLocalChangesAsync())
                throw new InvalidOperationException("更新期间仓库发生了其他改动，已停止自动操作以保留这些改动。");
        }

        private async Task<bool> HasLocalChangesAsync()
        {
            // Check the index and work tree separately, including when their changes cancel out.
            foreach (string[] args in new[] { new[] { "diff", "--quiet", "--cached", "HEAD", "--" }, new[] { "diff", "--quiet", "--" } })
            {
                var tracked = await RunGitAsync(args);
                if (tracked.ExitCode > 1) throw new InvalidOperationException("无法检查本地代码改动：" + tracked.Error);
                if (tracked.ExitCode == 1) return true;
            }
            string untracked = (await GitAsync("ls-files", "-z", "--others", "--exclude-standard")).Output;
            // Lumina writes imports under Custom in the source checkout. Do not treat them as code.
            return GitPaths(untracked).Any(path =>
                !path.StartsWith(LocalLutPath + "Custom/", StringComparison.OrdinalIgnoreCase) ||
                !CustomLutExtensions.Contains(Path.GetExtension(path)));
        }

        private async Task RefuseDataOverwriteAsync(string previous, string next)
        {
            var changes = GitPaths((await GitAsync("diff", "--name-only", "--no-renames", "-z", previous, next)).Output).ToArray();
            if (changes.Any(path => IsSameOrBelow(path, "materials") || path.Equals(".env.local", StringComparison.OrdinalIgnoreCase) || path.Equals("user_settings.json", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("更新试图更改本机数据目录或配置，请改为手动处理；本次未修改代码。");
            var localLuts = GitPaths((await GitAsync("ls-files", "-z", "--others", "--", LocalLutPath)).Output);
            string collision = localLuts.FirstOrDefault(local => changes.Any(path => IsSameOrBelow(local, path) || IsSameOrBelow(path, local)));
            if (collision != null)
                throw new InvalidOperationException("更新会覆盖本机 LUT：" + collision + "。请先备份并重命名该 LUT，再重试更新。");
        }

        private static IEnumerable<string> GitPaths(string output)
        {
            return output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool IsSameOrBelow(string path, string parent)
        {
            return path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
        }

        private string MaterialsDirectory()
        {
            string value = Environment.GetEnvironmentVariable("LUMINA_MATERIALS_DIR");
            string envFile = EnvironmentFile();
            if (value == null && File.Exists(envFile))
                foreach (string line in File.ReadAllLines(envFile, Encoding.UTF8))
                {
                    int equals = line.IndexOf('=');
                    if (equals >= 0 && line.Substring(0, equals).Trim() == "LUMINA_MATERIALS_DIR")
                    {
                        value = line.Substring(equals + 1).Trim();
                        if (value.Length >= 2 && value[0] == value[value.Length - 1] && (value[0] == '"' || value[0] == '\'')) value = value.Substring(1, value.Length - 2);
                        break;
                    }
                }
            if (string.IsNullOrWhiteSpace(value)) return Path.Combine(settings.ProjectDirectory, "materials");
            if (value.StartsWith("~\\") || value.StartsWith("~/")) value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value.Substring(2));
            return Path.GetFullPath(Path.Combine(settings.ProjectDirectory, value));
        }

        private string EnvironmentFile()
        {
            string envOverride = Environment.GetEnvironmentVariable("LUMINA_ENV_LOCAL_PATH");
            return string.IsNullOrWhiteSpace(envOverride) ? Path.Combine(settings.ProjectDirectory, ".env.local") : Path.GetFullPath(Path.Combine(settings.ProjectDirectory, envOverride));
        }

        private void BackupLocalData(string file)
        {
            using (var archive = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                AddDirectory(archive, MaterialsDirectory(), "materials");
                AddDirectory(archive, Path.Combine(settings.ProjectDirectory, "lut-preset"), "lut-preset");
                foreach (string name in new[] { ".env.local", "user_settings.json" })
                {
                    string source = Path.Combine(settings.ProjectDirectory, name);
                    if (File.Exists(source)) archive.CreateEntryFromFile(source, name, CompressionLevel.Optimal);
                }
                string activeEnv = EnvironmentFile();
                if (File.Exists(activeEnv) && !activeEnv.Equals(Path.Combine(settings.ProjectDirectory, ".env.local"), StringComparison.OrdinalIgnoreCase))
                    archive.CreateEntryFromFile(activeEnv, "active-env.local", CompressionLevel.Optimal);
            }
        }

        private static void AddDirectory(ZipArchive archive, string directory, string prefix)
        {
            if (!Directory.Exists(directory)) return;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("备份目录含符号链接，请先人工处理：" + directory);
            foreach (string file in Directory.GetFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("备份文件含符号链接：" + file);
                archive.CreateEntryFromFile(file, prefix + "/" + Path.GetFileName(file), CompressionLevel.Optimal);
            }
            foreach (string child in Directory.GetDirectories(directory)) AddDirectory(archive, child, prefix + "/" + Path.GetFileName(child));
        }

        private async Task InstallDependenciesAsync()
        {
            Stage("更新 Python 依赖…");
            await RunRequiredAsync(settings.PythonExecutable, 1800, "-m", "pip", "install", "-r", Path.Combine(settings.ProjectDirectory, "requirements.txt"));
            await InstallFrontendAsync(true);
        }

        private async Task InstallFrontendAsync(bool build)
        {
            string corepack = Path.Combine(Path.GetDirectoryName(settings.NodeExecutable), "node_modules", "corepack", "dist", "corepack.js");
            if (!File.Exists(corepack)) corepack = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "corepack", "dist", "corepack.js");
            if (!File.Exists(corepack)) throw new InvalidOperationException("未找到 Corepack。请先安装 Corepack，再重试更新。");
            string frontend = Path.Combine(settings.ProjectDirectory, "frontend");
            Stage("安装前端依赖…");
            await RunRequiredAsync(settings.NodeExecutable, 1800, corepack, "pnpm", "--dir", frontend, "install", "--frozen-lockfile");
            if (build)
            {
                Stage("检查前端构建…");
                await RunRequiredAsync(settings.NodeExecutable, 600, corepack, "pnpm", "--dir", frontend, "build");
            }
        }

        private Task<CommandResult> RunGitAsync(params string[] arguments)
        {
            return RunAsync("git.exe", 120, new[] { "-c", "core.quotepath=false" }.Concat(arguments).ToArray());
        }

        private async Task<CommandResult> GitAsync(params string[] arguments)
        {
            var result = await RunGitAsync(arguments);
            if (result.ExitCode != 0) throw new InvalidOperationException("Git " + arguments[0] + " 失败：" + result.Error.Trim() + result.Output.Trim());
            return result;
        }

        private async Task<CommandResult> RunRequiredAsync(string executable, int timeoutSeconds, params string[] arguments)
        {
            var result = await RunAsync(executable, timeoutSeconds, arguments);
            services.Log(result.Output + result.Error);
            if (result.ExitCode != 0) throw new InvalidOperationException(Path.GetFileName(executable) + " 执行失败，请查看日志。");
            return result;
        }

        private async Task<CommandResult> RunAsync(string executable, int timeoutSeconds, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable, string.Join(" ", arguments.Select(QuoteArgument))) {
                WorkingDirectory = settings.ProjectDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            start.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
            start.EnvironmentVariables["PYTHONUTF8"] = "1";
            start.EnvironmentVariables["COREPACK_ENABLE_DOWNLOAD_PROMPT"] = "0";
            start.EnvironmentVariables["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
            start.EnvironmentVariables["PIP_NO_INPUT"] = "1";
            using (var process = Process.Start(start))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                if (!await Task.Run(() => process.WaitForExit(timeoutSeconds * 1000)))
                {
                    using (var kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true })) kill.WaitForExit(10000);
                    throw new TimeoutException(Path.GetFileName(executable) + " 执行超时，请检查网络和日志。");
                }
                return new CommandResult { ExitCode = process.ExitCode, Output = await output, Error = await error };
            }
        }

        internal static string QuoteArgument(string argument)
        {
            var value = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                value.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                value.Append(c);
                slashes = 0;
            }
            value.Append('\\', slashes * 2);
            return value.Append('"').ToString();
        }
    }
}
