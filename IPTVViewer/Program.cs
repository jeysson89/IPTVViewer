using System;
using System.IO;
using System.Windows.Forms;

namespace IPTVViewer
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError(e.ExceptionObject as Exception);
            Application.ThreadException += (s, e) => LogError(e.Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args != null && Array.IndexOf(args, "--check-updates") >= 0)
            {
                RunUpdateCheck(args);
                return;
            }

            try
            {
                using (var splash = new SplashForm())
                {
                    Application.Run(splash);
                }

                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                LogError(ex);
                MessageBox.Show("Критическая ошибка:\n" + ex.Message + "\n\nЛог: " + LogPath,
                    "IPTV Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void RunUpdateCheck(string[] args)
        {
            try
            {
                var settings = SettingsManager.Load();
                var repo = settings.UpdateRepo;
                var current = UpdateChecker.CurrentVersion;

                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "--repo") repo = args[i + 1];
                    else if (args[i] == "--current") current = args[i + 1];
                }

                var info = UpdateChecker.Check(repo, current);
                var asset = info.PreferredAsset;

                var text =
                    "repo=" + UpdateChecker.NormalizeRepo(repo) + "\r\n" +
                    "current=" + info.CurrentVersion + "\r\n" +
                    "latest=" + (info.LatestVersion ?? "") + "\r\n" +
                    "hasUpdate=" + info.HasUpdate + "\r\n" +
                    "tag=" + (info.TagName ?? "") + "\r\n" +
                    "releaseUrl=" + (info.ReleaseUrl ?? "") + "\r\n" +
                    "asset=" + (asset != null ? asset.Name : "") + "\r\n" +
                    "assetSize=" + (asset != null ? asset.Size.ToString() : "0") + "\r\n" +
                    "error=" + (info.Error ?? "");

                Directory.CreateDirectory(SettingsManager.DataDir);
                File.WriteAllText(Path.Combine(SettingsManager.DataDir, "updatecheck.log"), text);
                Console.WriteLine(text);
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        private static string LogPath
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPTVViewer");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "crash.log");
            }
        }

        private static void LogError(Exception ex)
        {
            try
            {
                File.AppendAllText(LogPath,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "]\r\n" +
                    (ex == null ? "Unknown error" : ex.ToString()) + "\r\n\r\n");
            }
            catch { }
        }
    }
}
