using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace IPTVViewer
{
    public class PlaylistResult
    {
        public List<Channel> Channels { get; set; }
        public string EpgUrl { get; set; }

        public PlaylistResult()
        {
            Channels = new List<Channel>();
        }
    }

    public static class PlaylistLoader
    {
        private static readonly HttpClient _client = new HttpClient();

        static PlaylistLoader()
        {
            _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 6.1; Win64; x64) AppleWebKit/537.36");
            _client.Timeout = TimeSpan.FromSeconds(30);
        }

        public static PlaylistResult Load(string url)
        {
            var text = _client.GetStringAsync(url).GetAwaiter().GetResult();
            return Parse(text, url);
        }

        public static Task<PlaylistResult> LoadAsync(string url)
        {
            return _client.GetStringAsync(url).ContinueWith(t => Parse(t.Result, url));
        }

        public static PlaylistResult Parse(string text, string playlistUrl)
        {
            var result = new PlaylistResult();
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            string currentName = null;
            string currentGroup = null;
            string currentTvgId = null;
            string currentTvgName = null;
            string currentLogo = null;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(result.EpgUrl))
                        result.EpgUrl = ExtractEpgUrl(line);
                    continue;
                }

                if (line.StartsWith("#", StringComparison.OrdinalIgnoreCase))
                {
                    if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(new[] { ',' }, 2);
                        currentName = parts.Length > 1 ? parts[1].Trim() : "Без названия";
                        currentGroup = Attr(line, "group-title");
                        currentTvgId = Attr(line, "tvg-id");
                        currentTvgName = Attr(line, "tvg-name");
                        currentLogo = Attr(line, "tvg-logo");
                    }
                    continue;
                }

                if (line.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("udp", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("ftp", StringComparison.OrdinalIgnoreCase))
                {
                    result.Channels.Add(new Channel
                    {
                        Name = currentName ?? "Канал",
                        Url = line,
                        Group = currentGroup,
                        TvgId = currentTvgId,
                        TvgName = currentTvgName,
                        Logo = currentLogo
                    });
                    currentName = null;
                    currentGroup = null;
                    currentTvgId = null;
                    currentTvgName = null;
                    currentLogo = null;
                }
            }

            result.EpgUrl = ResolveUrl(playlistUrl, result.EpgUrl);
            return result;
        }

        private static string ExtractEpgUrl(string line)
        {
            var m = Regex.Match(line, @"(?:url-tvg|x-tvg-url)\s*=\s*""([^""]*)""",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static string Attr(string line, string name)
        {
            var m = Regex.Match(line, name + @"\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        private static string ResolveUrl(string baseUrl, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return url;
            try
            {
                var baseUri = new Uri(baseUrl);
                return new Uri(baseUri, url).ToString();
            }
            catch
            {
                return url;
            }
        }
    }
}
