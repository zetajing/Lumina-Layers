using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LuminaLauncher
{
    // Explicit integration test: starts and stops the configured local installation.
    internal static class ControllerSmokeTests
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length != 2) { Console.Error.WriteLine("Usage: LauncherSmokeTests.exe CONFIG.json REPORT.json"); return 2; }
            try { RunAsync(args[0], args[1]).GetAwaiter().GetResult(); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        private static async Task<int> MaterialCountAsync(HttpClient http)
        {
            string body = await http.GetStringAsync("http://127.0.0.1:8000/api/extractor/gradient-card-materials");
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
            return ((System.Collections.ICollection)data["materials"]).Count;
        }

        private static async Task RunAsync(string configFile, string reportFile)
        {
            var settings = LauncherSettings.Load(configFile);
            settings.Validate();
            var results = new List<string>();
            using (var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) })
            using (var controller = new LauncherController(settings, Path.Combine(Path.GetDirectoryName(reportFile), "launcher-test-logs")))
            {
                controller.LogReceived += Console.WriteLine;
                await controller.StartAsync();
                int before = await MaterialCountAsync(http);
                Process foreign = null;
                try
                {
                    await controller.StopAsync();
                    Require(!(await controller.ProbeAsync()).Any, "Existing services did not stop.");
                    results.Add("Stopped the existing script-launched services.");

                    foreign = Process.Start(new ProcessStartInfo(settings.PythonExecutable, "-u -m http.server 4174 --bind 127.0.0.1") {
                        UseShellExecute = false, CreateNoWindow = true
                    });
                    bool foreignReady = false;
                    using (var probe = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) })
                        for (int i = 0; i < 15 && !foreignReady; i++)
                        {
                            try { using (var response = await probe.GetAsync("http://127.0.0.1:4174/")) foreignReady = response.IsSuccessStatusCode; }
                            catch (HttpRequestException) { }
                            catch (TaskCanceledException) { }
                            if (!foreignReady) await Task.Delay(100);
                        }
                    Require(foreignReady, "The unrelated test HTTP server did not start.");
                    bool rejected = false;
                    try { await controller.StartAsync(); }
                    catch (InvalidOperationException) { rejected = true; }
                    Require(rejected, "An occupied frontend port was not rejected.");
                    await controller.StopAsync();
                    Require(!foreign.HasExited, "An unrelated HTTP server was terminated.");
                    results.Add("Rejected an occupied port and preserved an unrelated Python HTTP server.");
                    KillTestProcess(foreign);
                    foreign = null;

                    await controller.StartAsync();
                    Require((await controller.ProbeAsync()).Ready, "The services did not become healthy.");
                    int after = await MaterialCountAsync(http);
                    Require(before == after, "The material library count changed.");
                    results.Add("Started healthy frontend and backend; material count preserved: " + after);
                    await controller.StartAsync();
                    Require((await controller.ProbeAsync()).Ready, "Repeated start disrupted the services.");
                    results.Add("Repeated start is safe.");
                    await controller.StopAsync();
                    Require(!(await controller.ProbeAsync()).Any, "Application-managed services did not stop.");
                    results.Add("Stopped the application-managed services.");
                    File.WriteAllText(reportFile, new JavaScriptSerializer().Serialize(new { passed = true, material_count = after, checks = results }), Encoding.UTF8);
                    Console.WriteLine("PASS: " + results.Count + " checks; " + after + " materials preserved.");
                }
                finally
                {
                    if (foreign != null) KillTestProcess(foreign);
                    controller.StopAsync().GetAwaiter().GetResult();
                }
            }
        }

        private static void KillTestProcess(Process process)
        {
            if (process.HasExited) return;
            using (var kill = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + process.Id + " /T /F") { UseShellExecute = false, CreateNoWindow = true })) kill.WaitForExit();
            process.Dispose();
        }

    }
}
