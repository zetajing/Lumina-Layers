using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LuminaLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string config = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LuminaLauncher.json");
            string key;
            using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(config.ToUpperInvariant()))).Replace("-", "");
            bool first;
            using (var mutex = new Mutex(true, "Local\\LuminaLauncher-" + key, out first))
            {
                if (!first) { MessageBox.Show("启动器已经打开，可在任务栏托盘中找到 Lumina 图标。", "Lumina 启动器"); return; }
                try { Application.Run(new MainForm(config)); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Lumina 启动器", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
