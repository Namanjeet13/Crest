// Crest uninstaller. Copied into the install folder by Setup and listed in Settings > Apps.
// Removes the app, its shortcuts, the sign-in entry and the Apps entry. Your settings
// (%APPDATA%\Crest) stay unless you choose to delete them too.

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Crest Uninstaller")]
[assembly: AssemblyProduct("Crest")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace CrestUninstall
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool quiet = Array.Exists(args, a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) || a.Equals("/s", StringComparison.OrdinalIgnoreCase));
            Application.EnableVisualStyles();

            string dir = Path.GetDirectoryName(Path.GetFullPath(Assembly.GetExecutingAssembly().Location));
            bool removeData = false;
            if (!quiet)
            {
                if (MessageBox.Show("Remove Crest from this computer?", "Uninstall Crest", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return 1;
                removeData = MessageBox.Show("Also delete your Crest settings and clipboard history?\n\nChoose No to keep them in case you install Crest again.",
                    "Uninstall Crest", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }

            foreach (var p in Process.GetProcessesByName("Crest"))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
                finally { p.Dispose(); }
            }

            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Crest.lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Crest.lnk"));
            try
            {
                using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    if (run != null) run.DeleteValue("Crest", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Crest", false);
            }
            catch { }

            if (removeData)
            {
                try { Directory.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crest"), true); } catch { }
            }

            // Everything but this running file goes now; a hidden helper removes the folder once we have exited.
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                if (!f.Equals(Assembly.GetExecutingAssembly().Location, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
            if (Path.GetFileName(dir).Equals("Crest", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + dir + "\"")
                    {
                        CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetTempPath(),
                    });
                }
                catch { }
            }

            if (!quiet) MessageBox.Show("Crest was removed.", "Uninstall Crest", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
