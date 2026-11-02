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
            DepsLoader.Register();

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

            if (args != null && Array.IndexOf(args, "--epg-test") >= 0)
            {
                RunEpgTest();
                return;
            }

            CleanupOldExecutable();

            try
            {
                var startupSettings = SettingsManager.Load();
                Theme.SetMode(startupSettings.LightTheme);

                // Распаковка VLC из единого exe — заранее, пока показывается заставка.
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { VlcLocator.Ensure(SettingsManager.DataDir); } catch { }
                });

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
                    "IPTV Newline", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// После самообновления работающий exe переименовывается в *.old —
        /// удаляем файл-остаток, когда старый процесс с ним закончит.
        /// </summary>
        private static void CleanupOldExecutable()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                // старый процесс может завершаться дольше минуты — пробуем до 5 минут
                for (int i = 0; i < 150; i++)
                {
                    try
                    {
                        var old = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName + ".old";
                        if (!File.Exists(old)) return;
                        File.Delete(old);
                        return;
                    }
                    catch
                    {
                        System.Threading.Thread.Sleep(2000);
                    }
                }
            });
        }

        private static void RunEpgTest()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var settings = SettingsManager.Load();
                var url = settings.SourceUrls != null && settings.SourceUrls.Count > settings.SelectedSourceIndex
                    ? settings.SourceUrls[settings.SelectedSourceIndex]
                    : null;

                sb.AppendLine("source=" + url);

                var playlist = PlaylistLoader.Load(url);
                sb.AppendLine("channels=" + playlist.Channels.Count);
                sb.AppendLine("epgUrl=" + (playlist.EpgUrl ?? ""));

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var epg = EpgLoader.Load(playlist.EpgUrl, playlist.Channels);
                watch.Stop();

                sb.AppendLine("elapsedMs=" + watch.ElapsedMilliseconds);
                sb.AppendLine("empty=" + epg.IsEmpty);

                int total = 0, withCurrent = 0;
                foreach (var ch in playlist.Channels)
                {
                    var programs = epg.GetPrograms(ch);
                    total += programs.Count;
                    var current = epg.GetCurrent(ch, DateTime.Now);
                    if (current != null)
                    {
                        withCurrent++;
                        if (withCurrent <= 10)
                            sb.AppendLine("now: " + ch.Name + " -> " + current.Title + " " + current.TimeRange);
                    }
                }

                sb.AppendLine("programs=" + total);
                sb.AppendLine("channelsWithCurrent=" + withCurrent);
            }
            catch (Exception ex)
            {
                sb.AppendLine("error=" + ex);
            }

            Directory.CreateDirectory(SettingsManager.DataDir);
            File.WriteAllText(Path.Combine(SettingsManager.DataDir, "epgtest.log"), sb.ToString());
            Console.WriteLine(sb.ToString());
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
