using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LuminaLauncher
{
    internal sealed class TestServices : ILauncherServices
    {
        public bool Running = true;
        public int Starts;
        public int Stops;
        public Task<ServiceState> ProbeAsync() { return Task.FromResult(new ServiceState { Backend = Running, Frontend = Running }); }
        public Task StartAsync() { Running = true; Starts++; return Task.FromResult(0); }
        public Task StopAsync() { Running = false; Stops++; return Task.FromResult(0); }
        public void Log(string message) { Console.WriteLine(message); }
    }

    internal sealed class UpdateFixture
    {
        public readonly string Root;
        public readonly string Upstream;
        public readonly string App;
        public readonly string Config;
        public readonly string Fork;
        public readonly LauncherSettings Settings;
        public readonly TestServices Services = new TestServices();
        public readonly UpdateController Updater;

        public UpdateFixture(string root, LauncherSettings executables)
        {
            Root = root;
            Directory.CreateDirectory(root);
            Upstream = Path.Combine(root, "upstream");
            App = Path.Combine(root, "app with spaces");
            Fork = Path.Combine(root, "fork.git");
            Config = Path.Combine(root, "launcher", "LuminaLauncher.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Config));
            string remote = Path.Combine(root, "origin.git");
            Git(root, "init", "--bare", remote);
            Git(root, "init", "--bare", Fork);
            Git(root, "init", Upstream);
            Identify(Upstream);
            Git(Upstream, "switch", "-c", "main");
            Write(Upstream, "api_server.py", "# fixture only\n");
            Write(Upstream, "requirements.txt", "# no additional dependencies\n");
            Write(Upstream, "shared.txt", "original\n");
            Write(Upstream, ".gitignore", "/materials/\n/frontend/node_modules/\n/frontend/dist/\nuser_settings.json\n.env.local\n");
            // Write the package through the serializer to preserve shell-script quoting.
            var package = new Dictionary<string, object> {
                { "name", "lumina-update-fixture" }, { "version", "1.0.0" }, { "private", true },
                { "packageManager", "pnpm@10.33.2" },
                { "scripts", new Dictionary<string, object> { { "build", "node -e \"console.log('fixture build ok')\"" } } }
            };
            Write(Upstream, "frontend/package.json", new JavaScriptSerializer().Serialize(package));
            Write(Upstream, "package.json", new JavaScriptSerializer().Serialize(package));
            string corepack = Path.Combine(Path.GetDirectoryName(executables.NodeExecutable), "node_modules", "corepack", "dist", "corepack.js");
            Command(Upstream, executables.NodeExecutable, corepack, "pnpm", "--dir", Path.Combine(Upstream, "frontend"), "install", "--lockfile-only");
            Git(Upstream, "add", ".");
            Git(Upstream, "commit", "-m", "fixture base");
            Git(Upstream, "remote", "add", "origin", remote);
            Git(Upstream, "push", "origin", "main");
            Git(root, "clone", "--branch", "main", remote, App);
            Identify(App);
            Git(App, "switch", "-c", "test/launcher");
            Write(App, "launcher-local.txt", "keep custom launcher\n");
            Git(App, "add", ".");
            Git(App, "commit", "-m", "custom launcher");
            Git(App, "remote", "add", "fork", Fork);
            Git(App, "push", "-u", "fork", "test/launcher");
            Write(App, "frontend/node_modules/vite/bin/vite.js", "// fixture dependency marker\n");
            Write(App, "materials/品牌/红色/material.json", "{\"sentinel\":\"保留耗材\"}");
            Write(App, "user_settings.json", "{\"keep\":true}");
            Settings = new LauncherSettings { ProjectDirectory = App, PythonExecutable = executables.PythonExecutable, NodeExecutable = executables.NodeExecutable };
            Settings.Save(Config);
            Updater = new UpdateController(Settings, Services, Config);
        }

        public void Publish(string path, string content)
        {
            Write(Upstream, path, content);
            Git(Upstream, "add", ".");
            Git(Upstream, "commit", "-m", "upstream update");
            Git(Upstream, "push", "origin", "main");
        }

        public static void Write(string root, string relative, string content)
        {
            string file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, content, new UTF8Encoding(false));
        }

        private static void Identify(string directory)
        { Git(directory, "config", "user.name", "Launcher integration test"); Git(directory, "config", "user.email", "launcher-test@example.invalid"); Git(directory, "config", "commit.gpgsign", "false"); }

        public static string Git(string directory, params string[] args) { return Command(directory, "git.exe", args); }

        public static string Command(string directory, string executable, params string[] args)
        {
            var start = new ProcessStartInfo(executable, string.Join(" ", args.Select(UpdateController.QuoteArgument))) {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            start.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
            using (var process = Process.Start(start))
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120000)) throw new TimeoutException(executable);
                if (process.ExitCode != 0) throw new InvalidOperationException(executable + " " + string.Join(" ", args) + "\n" + error.Result + output.Result);
                return output.Result.Trim();
            }
        }
    }

    internal static class UpdaterIntegrationTests
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length != 2) { Console.Error.WriteLine("Usage: UpdaterIntegrationTests.exe REAL_CONFIG.json EMPTY_TEST_DIRECTORY"); return 2; }
            try { RunAsync(LauncherSettings.Load(args[0]), Path.GetFullPath(args[1])).GetAwaiter().GetResult(); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        private static async Task<bool> FailsAsync(UpdateFixture fixture, string expected = null)
        {
            try { await fixture.Updater.UpdateAsync(); return false; }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine("Update failure: " + ex.Message);
                return expected == null || ex.Message.Contains(expected);
            }
        }

        private static async Task RunAsync(LauncherSettings executables, string root)
        {
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("Use an empty test directory.");
            Directory.CreateDirectory(root);
            var checks = new List<string>();

            var success = new UpdateFixture(Path.Combine(root, "success"), executables);
            UpdateFixture.Write(success.Root, "external-materials/品牌/红色/material.json", "{\"sentinel\":\"自定义耗材目录\"}");
            UpdateFixture.Write(success.App, ".env.local", "LUMINA_MATERIALS_DIR=\"../external-materials\"\n");
            string original = UpdateFixture.Git(success.App, "rev-parse", "HEAD");
            var initial = await success.Updater.CheckAsync();
            Require(!initial.Available && success.Services.Stops == 0, "Checking an up-to-date installation changed service state.");
            var noOp = await success.Updater.UpdateAsync();
            Require(noOp.BackupDirectory == null && success.Services.Stops == 0, "No-op update stopped services.");
            checks.Add("Latest-version check and no-op update preserve running services.");

            success.Publish("shared.txt", "official version 2\n");
            const string localLut = "lut-preset/Custom/色卡 (1)_2.json";
            const string lutContent = "{\"sentinel\":\"本机 LUT\"}";
            UpdateFixture.Write(success.App, localLut, lutContent);
            var available = await success.Updater.CheckAsync();
            Require(available.Available && available.PendingCommits == 1, "An upstream update was not found.");
            Require(!available.HasLocalChanges, "A custom LUT was treated as uncommitted code.");
            Require(UpdateFixture.Git(success.App, "rev-parse", "HEAD") == original && success.Services.Stops == 0, "Checking modified the checkout or services.");
            var applied = await success.Updater.UpdateAsync();
            Require(File.ReadAllText(Path.Combine(success.App, "shared.txt")).Trim() == "official version 2", "Upstream code was not installed.");
            Require(File.Exists(Path.Combine(success.App, "launcher-local.txt")), "The custom launcher was lost.");
            Require(File.ReadAllText(Path.Combine(success.App, localLut)) == lutContent, "The custom LUT was changed.");
            Require(UpdateFixture.Git(success.App, "ls-files", "--", localLut) == "", "The local LUT was committed.");
            Require(success.Services.Running && success.Services.Starts == 1 && success.Services.Stops == 1, "Running service state was not restored.");
            Require(UpdateFixture.Git(success.App, "rev-parse", "HEAD") == UpdateFixture.Git(success.Root, "--git-dir=" + success.Fork, "rev-parse", "refs/heads/test/launcher"), "The validated update was not pushed to the personal branch.");
            using (var archive = ZipFile.OpenRead(Path.Combine(applied.BackupDirectory, "local-data.zip")))
            {
                Require(archive.GetEntry("materials/品牌/红色/material.json") != null, "Materials were not backed up.");
                using (var reader = new StreamReader(archive.GetEntry("materials/品牌/红色/material.json").Open()))
                    Require(reader.ReadToEnd().Contains("自定义耗材目录"), "The configured external materials directory was not backed up.");
                Require(archive.GetEntry("user_settings.json") != null, "Local settings were not backed up.");
                using (var reader = new StreamReader(archive.GetEntry(localLut).Open()))
                    Require(reader.ReadToEnd() == lutContent, "The imported LUT was not backed up byte-for-byte.");
            }
            checks.Add("A real Git merge, pip install, pnpm install/build, backup, restart and personal-branch push succeeded.");
            checks.Add("An untracked custom LUT with a non-ASCII name is backed up and preserved without committing it.");

            string contributor = Path.Combine(success.Root, "fork-author");
            UpdateFixture.Git(success.Root, "clone", "--branch", "test/launcher", success.Fork, contributor);
            UpdateFixture.Git(contributor, "config", "user.name", "Launcher integration test");
            UpdateFixture.Git(contributor, "config", "user.email", "launcher-test@example.invalid");
            UpdateFixture.Write(contributor, "launcher-local.txt", "new launcher feature\n");
            UpdateFixture.Git(contributor, "add", ".");
            UpdateFixture.Git(contributor, "commit", "-m", "launcher branch update");
            UpdateFixture.Git(contributor, "push", "origin", "test/launcher");
            Require((await success.Updater.CheckAsync()).Available, "A launcher-only update was not detected.");
            success.Services.Running = false;
            await success.Updater.UpdateAsync();
            Require(!success.Services.Running && File.ReadAllText(Path.Combine(success.App, "launcher-local.txt")).Contains("new launcher feature"), "A launcher-only update failed or changed the previously stopped service state.");
            checks.Add("Launcher-branch updates are detected separately and preserve a stopped service state.");

            var dirty = new UpdateFixture(Path.Combine(root, "dirty"), executables);
            dirty.Publish("shared.txt", "upstream\n");
            UpdateFixture.Write(dirty.App, "personal-change.txt", "do not overwrite");
            string dirtyHead = UpdateFixture.Git(dirty.App, "rev-parse", "HEAD");
            Require((await dirty.Updater.CheckAsync()).HasLocalChanges, "Untracked work was not detected.");
            Require(await FailsAsync(dirty), "An update with untracked work was allowed.");
            Require(dirty.Services.Stops == 0 && UpdateFixture.Git(dirty.App, "rev-parse", "HEAD") == dirtyHead, "Dirty-work protection changed the installation.");
            File.Move(Path.Combine(dirty.App, "personal-change.txt"), Path.Combine(dirty.Root, "personal-change.txt"));
            UpdateFixture.Write(dirty.App, localLut, lutContent);
            UpdateFixture.Git(dirty.App, "add", "--", localLut);
            Require((await dirty.Updater.CheckAsync()).HasLocalChanges && await FailsAsync(dirty, "未提交"), "A staged LUT edit was allowed.");
            checks.Add("Uncommitted and untracked work blocks the update before stopping services.");

            var lutCollision = new UpdateFixture(Path.Combine(root, "lut-collision"), executables);
            UpdateFixture.Write(lutCollision.App, localLut, lutContent);
            lutCollision.Publish("lut-preset/Custom/色卡 (1)_2.JSON", "upstream LUT\n");
            Require(await FailsAsync(lutCollision, "更新会覆盖本机 LUT：" + localLut), "A case-insensitive collision with an imported LUT was missed.");
            Require(lutCollision.Services.Stops == 0 && File.ReadAllText(Path.Combine(lutCollision.App, localLut)) == lutContent, "A colliding update stopped services or changed the LUT.");
            checks.Add("A case-insensitive collision with a custom LUT is blocked before stopping services.");

            var conflict = new UpdateFixture(Path.Combine(root, "conflict"), executables);
            UpdateFixture.Write(conflict.App, "shared.txt", "local version\n");
            UpdateFixture.Git(conflict.App, "add", "shared.txt");
            UpdateFixture.Git(conflict.App, "commit", "-m", "local edit");
            conflict.Publish("shared.txt", "incompatible official version\n");
            string conflictHead = UpdateFixture.Git(conflict.App, "rev-parse", "HEAD");
            Require(await FailsAsync(conflict), "A conflicting update was allowed.");
            Require(conflict.Services.Stops == 0 && UpdateFixture.Git(conflict.App, "rev-parse", "HEAD") == conflictHead && !File.Exists(Path.Combine(conflict.App, ".git", "MERGE_HEAD")), "Conflict preflight modified the working tree.");
            checks.Add("Merge conflicts are found without changing HEAD, files or running services.");

            var protectedData = new UpdateFixture(Path.Combine(root, "protected"), executables);
            UpdateFixture.Write(protectedData.Upstream, "materials/品牌/红色/material.json", "must not replace local data");
            UpdateFixture.Git(protectedData.Upstream, "add", "-f", "materials/品牌/红色/material.json");
            UpdateFixture.Git(protectedData.Upstream, "commit", "-m", "try to track local data");
            UpdateFixture.Git(protectedData.Upstream, "push", "origin", "main");
            Require(await FailsAsync(protectedData), "An upstream update overwrote the local materials directory.");
            Require(protectedData.Services.Stops == 0 && File.ReadAllText(Path.Combine(protectedData.App, "materials", "品牌", "红色", "material.json")).Contains("保留耗材"), "Protected local data was changed.");
            checks.Add("Upstream commits that would track and overwrite local materials are blocked.");

            var failure = new UpdateFixture(Path.Combine(root, "failure"), executables);
            string beforeTree = UpdateFixture.Git(failure.App, "rev-parse", "HEAD^{tree}");
            failure.Publish("requirements.txt", "--no-index\nlumina-updater-test-missing-package==0.0.0\n");
            Require(await FailsAsync(failure), "A dependency installation failure was not reported.");
            Require(UpdateFixture.Git(failure.App, "rev-parse", "HEAD^{tree}") == beforeTree && failure.Services.Running, "Failed update did not restore code and service state.");
            Require(File.ReadAllText(Path.Combine(failure.App, "materials", "品牌", "红色", "material.json")).Contains("保留耗材"), "A failed update changed material data.");
            Require((await failure.Updater.CheckAsync()).Available, "A rolled-back update cannot be retried.");
            checks.Add("Dependency failure restores the previous source tree, pinned environment and running state; retry remains available.");
            failure.Publish("requirements.txt", "# corrected requirements\n");
            var retried = await failure.Updater.UpdateAsync();
            Require(retried.UpdatedCommit != null && File.ReadAllText(Path.Combine(failure.App, "requirements.txt")).Contains("corrected") && failure.Services.Running, "A corrected update could not be applied after rollback.");
            checks.Add("Retry successfully combines the restored update with a later upstream fix.");

            var server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            try
            {
                UpdateFixture.Git(success.App, "config", "http.proxy", "");
                UpdateFixture.Git(success.App, "remote", "set-url", "origin", "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + "/git");
                var responses = Task.Run(() => {
                    foreach (string status in new[] { "503 Service Unavailable", "404 Not Found" })
                    {
                        using (var client = server.AcceptTcpClient())
                        using (var stream = client.GetStream())
                        using (var request = new StreamReader(stream, Encoding.ASCII))
                        {
                            stream.ReadTimeout = 5000;
                            string header;
                            while (!string.IsNullOrEmpty(header = request.ReadLine())) { }
                            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            stream.Write(response, 0, response.Length);
                        }
                    }
                });
                Require(await FailsAsync(success, "repository") && await Task.WhenAny(responses, Task.Delay(2000)) == responses,
                    "Fetch did not retry a 503 or did not stop at a permanent error.");
                await responses;
                Require(!server.Pending(), "Fetch retried after a permanent 404 error.");
            }
            finally { server.Stop(); }
            checks.Add("A real Git fetch retries a transient HTTP 503 and reports a permanent HTTP 404.");

            File.WriteAllText(Path.Combine(root, "report.json"), new JavaScriptSerializer().Serialize(new { passed = true, checks = checks }), Encoding.UTF8);
            Console.WriteLine("PASS: " + checks.Count + " updater integration checks.");
        }
    }
}
