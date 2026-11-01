using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace IPTVViewer
{
    public static class PlaylistLoader
    {
        private static readonly HttpClient _client = new HttpClient();

        static PlaylistLoader()
        {
            _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 6.1; Win64; x64) AppleWebKit/537.36");
            _client.Timeout = TimeSpan.FromSeconds(30);
        }

        public static List<Channel> Load(string url)
        {
            var text = _client.GetStringAsync(url).GetAwaiter().GetResult();
            return Parse(text);
        }

        public static Task<List<Channel>> LoadAsync(string url)
        {
            return _client.GetStringAsync(url).ContinueWith(t => Parse(t.Result));
        }

        private static List<Channel> Parse(string text)
        {
            var channels = new List<Channel>();
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            string currentName = null;
            string currentGroup = null;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (line.StartsWith("#", StringComparison.OrdinalIgnoreCase))
                {
                    if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split(new[] { ',' }, 2);
                        currentName = parts.Length > 1 ? parts[1].Trim() : "Без названия";
                        var g = Regex.Match(line, @"group-title=""([^""]*)""");
                        currentGroup = g.Success ? g.Groups[1].Value : null;
                    }
                    continue;
                }

                if (line.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("udp", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("ftp", StringComparison.OrdinalIgnoreCase))
                {
                    channels.Add(new Channel
                    {
                        Name = currentName ?? "Канал",
                        Url = line,
                        Group = currentGroup
                    });
                    currentName = null;
                    currentGroup = null;
                }
            }

            return channels;
        }
    }
}
