using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace IPTVViewer
{
    public class AppSettings
    {
        public int SelectedSourceIndex { get; set; } = 1;
        public List<string> SourceUrls { get; set; } = new List<string>
        {
            "https://newline.iptvportal.ru/",
            "http://tv.isp-nl.ru/nltv.m3u"
        };
        public bool AutoLoad { get; set; } = true;
        public bool UseExternalPlayer { get; set; } = false;
        public string UpdateRepo { get; set; } = UpdateChecker.DefaultRepo;
        public bool CheckUpdatesOnStartup { get; set; } = true;
        public bool LightTheme { get; set; } = false;
        public bool EpgEnabled { get; set; } = true;
        public string EpgUrl { get; set; } = "";
        public int WindowX { get; set; } = 100;
        public int WindowY { get; set; } = 100;
        public int WindowW { get; set; } = 1050;
        public int WindowH { get; set; } = 650;
    }

    public static class SettingsManager
    {
        private static readonly string _dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPTVViewer");
        private static readonly string _file = Path.Combine(_dir, "settings.json");
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(_file))
                    return new AppSettings();
                var txt = File.ReadAllText(_file);
                return _json.Deserialize<AppSettings>(txt) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public static void Save(AppSettings s)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(_file, _json.Serialize(s));
            }
            catch { }
        }

        public static string DataDir => _dir;
    }
}
