using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LuminaLauncher
{
    internal static class Program
    {
        internal static int ShowMessage;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string config = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LuminaLauncher.json");
            string key;
            using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(config.ToUpperInvariant()))).Replace("-", "");
            ShowMessage = RegisterWindowMessage("LuminaLauncher.Show." + key);
            bool first;
            using (var mutex = new Mutex(true, "Local\\LuminaLauncher-" + key, out first))
            {
                if (!first) { PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero); return; }
                try { Application.Run(new MainForm(config, Array.IndexOf(args, "--resume-services") >= 0)); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Lumina 启动器", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
