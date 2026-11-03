using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace IPTVViewer
{
    public class ReleaseAsset
    {
        public string Name { get; set; }
        public string DownloadUrl { get; set; }
        public long Size { get; set; }
    }

    public class UpdateInfo
    {
        public bool HasUpdate { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public string TagName { get; set; }
        public string ReleaseName { get; set; }
        public string ReleaseUrl { get; set; }
        public string Notes { get; set; }
        public string Error { get; set; }
        public List<ReleaseAsset> Assets { get; set; }

        public UpdateInfo()
        {
            Assets = new List<ReleaseAsset>();
        }

        /// <summary>
        /// Однофайловый exe приложения — им и обновляем установленную версию.
        /// Если exe в релизе нет, берём первый попавшийся asset.
        /// </summary>
        public ReleaseAsset PreferredAsset
        {
            get
            {
                if (Assets == null || Assets.Count == 0) return null;
                foreach (var a in Assets)
                {
                    if (!string.IsNullOrEmpty(a.Name) &&
                        a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        return a;
                }
                return Assets[0];
            }
        }
    }

    public static class UpdateChecker
    {
        public const string DefaultRepo = "jeysson89/IPTVViewer";

        private const string UserAgent = "IPTVViewer-Updater";

        static UpdateChecker()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { }
        }

        public static string CurrentVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                if (v == null) return "0.0.0";
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        /// <summary>Приводит "owner/repo", URL или ссылку на GitHub к виду owner/repo.</summary>
        public static string NormalizeRepo(string repo)
        {
            if (string.IsNullOrWhiteSpace(repo)) return DefaultRepo;
            var s = repo.Trim();
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var uri = new Uri(s);
                    s = uri.AbsolutePath.Trim('/');
                }
                catch { }
            }
            s = s.Trim('/');
            if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4);
            var parts = s.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return parts[0] + "/" + parts[1];
            return DefaultRepo;
        }

        /// <summary>Запрашивает последний релиз на GitHub. Никогда не бросает исключений.</summary>
        public static UpdateInfo Check(string repo)
        {
            return Check(repo, CurrentVersion);
        }

        public static UpdateInfo Check(string repo, string currentVersion)
        {
            var result = new UpdateInfo
            {
                CurrentVersion = currentVersion,
                HasUpdate = false
            };

            var slug = NormalizeRepo(repo);
            var url = "https://api.github.com/repos/" + slug + "/releases/latest";

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = UserAgent;
                request.Accept = "application/vnd.github+json";
                request.Timeout = 15000;
                request.ReadWriteTimeout = 15000;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                string body;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }

                ParseRelease(result, body, currentVersion);
                return result;
            }
            catch (WebException wex)
            {
                result.Error = DescribeWebError(wex);
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "Не удалось проверить обновления: " + ex.Message;
                return result;
            }
        }

        private static string DescribeWebError(WebException wex)
        {
            var http = wex.Response as HttpWebResponse;
            if (http != null)
            {
                try
                {
                    string body;
                    using (var stream = http.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        body = reader.ReadToEnd();

                    string message = null;
                    try
                    {
                        using (var document = JsonDocument.Parse(body))
                        {
                            var root = document.RootElement;
                            if (root.ValueKind == JsonValueKind.Object &&
                                root.TryGetProperty("message", out var value) &&
                                value.ValueKind == JsonValueKind.String)
                                message = value.GetString();
                        }
                    }
                    catch { }

                    if (http.StatusCode == HttpStatusCode.NotFound)
                        return "Обновления временно недоступны. Попробуйте позже.";
                    if (http.StatusCode == HttpStatusCode.Forbidden || (int)http.StatusCode == 429)
                        return "Сервер обновлений временно перегружен. Попробуйте позже." +
                               (string.IsNullOrEmpty(message) ? "" : "\n" + message);                    return "Сервер обновлений вернул ошибку " + (int)http.StatusCode + " " + http.StatusDescription +
                           (string.IsNullOrEmpty(message) ? "" : "\n" + message);
                }
                catch { }
            }

            if (wex.Status == WebExceptionStatus.Timeout)
                return "Превышено время ожидания ответа сервера обновлений.";
            return "Ошибка сети: " + wex.Message;
        }

        private static void ParseRelease(UpdateInfo result, string jsonText, string currentVersion)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(jsonText);
            }
            catch
            {
                result.Error = "Сервер обновлений вернул некорректный ответ.";
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    result.Error = "Сервер обновлений вернул некорректный ответ.";
                    return;
                }

                result.TagName = GetString(root, "tag_name");
                result.ReleaseName = GetString(root, "name");
                result.ReleaseUrl = GetString(root, "html_url");
                result.Notes = GetString(root, "body");
                result.LatestVersion = CleanVersion(result.TagName);

                if (string.IsNullOrEmpty(result.ReleaseUrl) && !string.IsNullOrEmpty(result.TagName))
                {
                    // html_url всегда приходит, но подстрахуемся
                    result.ReleaseUrl = "https://github.com/" + NormalizeRepo(DefaultRepo) + "/releases/tag/" + result.TagName;
                }

                if (root.TryGetProperty("assets", out var assets) &&
                    assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in assets.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        result.Assets.Add(new ReleaseAsset
                        {
                            Name = GetString(item, "name"),
                            DownloadUrl = GetString(item, "browser_download_url"),
                            Size = GetLong(item, "size")
                        });
                    }
                }

                if (string.IsNullOrEmpty(result.LatestVersion))
                {
                    result.Error = "В последнем релизе не указана версия (tag_name).";
                    return;
                }

                result.HasUpdate = CompareVersions(result.LatestVersion, currentVersion) > 0;
            }
        }

        private static string GetString(JsonElement obj, string key)
        {
            try
            {
                if (obj.ValueKind == JsonValueKind.Object &&
                    obj.TryGetProperty(key, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }
            catch { }
            return null;
        }

        private static long GetLong(JsonElement obj, string key)
        {
            try
            {
                if (obj.ValueKind == JsonValueKind.Object &&
                    obj.TryGetProperty(key, out var value) &&
                    value.ValueKind == JsonValueKind.Number)
                    return value.GetInt64();
            }
            catch { }
            return 0;
        }

        /// <summary>Убирает префикс v и лишние символы: "v2.1.0" -> "2.1.0".</summary>
        public static string CleanVersion(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return "";
            var s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("release-", StringComparison.OrdinalIgnoreCase))
            {
                s = s.TrimStart('v', 'V');
                if (s.StartsWith("release-", StringComparison.OrdinalIgnoreCase))
                    s = s.Substring(8);
            }
            var end = 0;
            while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.'))
                end++;
            return s.Substring(0, end).Trim('.');
        }

        /// <summary>Сравнивает версии: >0 если a новее b.</summary>
        public static int CompareVersions(string a, string b)
        {
            var va = ToVersion(a);
            var vb = ToVersion(b);
            if (va == null && vb == null) return 0;
            if (va == null) return -1;
            if (vb == null) return 1;
            return va.CompareTo(vb);
        }

        private static Version ToVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var s = CleanVersion(text);
            if (s.Length == 0) return null;

            var parts = s.Split('.');
            var numbers = new List<int>();
            foreach (var p in parts)
            {
                int n;
                if (!int.TryParse(p, out n)) break;
                numbers.Add(n);
                if (numbers.Count == 4) break;
            }
            if (numbers.Count == 0) return null;
            while (numbers.Count < 4) numbers.Add(0);
            return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        }

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            if (bytes >= 1024L * 1024L * 1024L)
                return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.0") + " ГБ";
            if (bytes >= 1024L * 1024L)
                return (bytes / 1024.0 / 1024.0).ToString("0.0") + " МБ";
            if (bytes >= 1024L)
                return (bytes / 1024.0).ToString("0") + " КБ";
            return bytes + " Б";
        }
    }
}
