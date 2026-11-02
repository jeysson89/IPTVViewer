using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;

namespace IPTVViewer
{
    public class EpgProgram
    {
        public string Title { get; set; }
        public DateTime Start { get; set; }
        public DateTime Stop { get; set; }

        public string TimeRange
        {
            get { return Start.ToString("HH:mm") + "–" + Stop.ToString("HH:mm"); }
        }
    }

    public class EpgData
    {
        internal readonly Dictionary<string, List<EpgProgram>> ById =
            new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> NameToId =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, List<EpgProgram>> ByName =
            new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);

        public bool IsEmpty
        {
            get { return ById.Count == 0 && ByName.Count == 0; }
        }

        public EpgProgram GetCurrent(Channel channel, DateTime now)
        {
            var list = Resolve(channel);
            if (list == null) return null;
            foreach (var p in list)
            {
                if (p.Start <= now && now < p.Stop)
                    return p;
            }
            return null;
        }

        public EpgProgram GetNext(Channel channel, DateTime now)
        {
            var list = Resolve(channel);
            if (list == null) return null;
            foreach (var p in list)
            {
                if (p.Start > now)
                    return p;
            }
            return null;
        }

        /// <summary>Список передач канала (в пределах загруженного окна).</summary>
        public IList<EpgProgram> GetPrograms(Channel channel)
        {
            var list = Resolve(channel);
            if (list == null)
                return new List<EpgProgram>();
            return list;
        }

        private List<EpgProgram> Resolve(Channel channel)
        {
            if (channel == null) return null;

            if (!string.IsNullOrWhiteSpace(channel.TvgId))
            {
                List<EpgProgram> list;
                if (ById.TryGetValue(channel.TvgId.Trim(), out list))
                    return list;
                var at = channel.TvgId.IndexOf('@');
                if (at > 0 && ById.TryGetValue(channel.TvgId.Substring(0, at), out list))
                    return list;
            }

            var key = EpgLoader.NormalizeName(channel.Name);
            if (key.Length == 0) return null;

            List<EpgProgram> byName;
            if (ByName.TryGetValue(key, out byName))
                return byName;

            string mappedId;
            if (NameToId.TryGetValue(key, out mappedId) && ById.TryGetValue(mappedId, out byName))
                return byName;

            return null;
        }
    }

    public static class EpgLoader
    {
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 6.1; Win64; x64) AppleWebKit/537.36";

        private const int GzipMaxAgeHours = 3;
        private const int CacheMaxAgeHours = 6;

        static EpgLoader()
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { }
        }

        /// <summary>
        /// Загружает XMLTV и оставляет только программы нужных каналов
        /// в окне [-2 ч; +24 ч]. Ошибки не выбрасываются — вернётся пустой EPG.
        /// Скачанный архив и разобранные данные кэшируются на диск.
        /// </summary>
        public static EpgData Load(string url, IList<Channel> channels)
        {
            var data = new EpgData();
            if (string.IsNullOrWhiteSpace(url) || channels == null || channels.Count == 0)
                return data;

            try
            {
                Directory.CreateDirectory(SettingsManager.DataDir);

                var key = Hash(url);
                var gzPath = Path.Combine(SettingsManager.DataDir, "epg-" + key + ".gz");
                var cachePath = Path.Combine(SettingsManager.DataDir, "epg-" + key + ".bin");
                var signature = ChannelSignature(channels);

                var cached = TryLoadCache(cachePath, signature);
                if (cached != null)
                    return cached;

                bool needDownload = true;
                if (File.Exists(gzPath) && new FileInfo(gzPath).Length > 1024 * 1024)
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(gzPath);
                    needDownload = age.TotalHours > GzipMaxAgeHours;
                }

                if (needDownload)
                    Download(url, gzPath);

                data = ParseFile(gzPath, channels);
                if (!data.IsEmpty)
                    SaveCache(cachePath, data, signature);
            }
            catch
            {
                // EPG не критична для просмотра — молча возвращаем пустые данные.
            }

            return data;
        }

        private static void Download(string url, string path)
        {
            var tmp = path + ".tmp";
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.UserAgent = UserAgent;
                request.Timeout = 60000;
                request.ReadWriteTimeout = 600000;
                request.AutomaticDecompression = DecompressionMethods.None;

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var network = response.GetResponseStream())
                using (var file = File.Create(tmp))
                {
                    network.CopyTo(file);
                }

                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        private static EpgData ParseFile(string path, IList<Channel> channels)
        {
            var data = new EpgData();

            using (var file = File.OpenRead(path))
            using (var buffered = new BufferedStream(file, 1 << 20))
            {
                var head = new byte[2];
                int read = buffered.Read(head, 0, 2);
                buffered.Position = 0;

                Stream stream = buffered;
                if (read == 2 && head[0] == 0x1f && head[1] == 0x8b)
                    stream = new GZipStream(buffered, CompressionMode.Decompress);

                if (ParseLines(stream, channels, data))
                    return data;
            }

            // Если XMLTV оказался многострочным — разбираем через XmlReader.
            data = new EpgData();
            using (var file = File.OpenRead(path))
            using (var buffered = new BufferedStream(file, 1 << 20))
            {
                var head = new byte[2];
                int read = buffered.Read(head, 0, 2);
                buffered.Position = 0;

                Stream stream = buffered;
                if (read == 2 && head[0] == 0x1f && head[1] == 0x8b)
                    stream = new GZipStream(buffered, CompressionMode.Decompress);

                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    IgnoreComments = true,
                    IgnoreWhitespace = true,
                    CloseInput = false
                }))
                {
                    ParseXml(reader, channels, data);
                }
            }

            return data;
        }

        /// <summary>
        /// Быстрый построчный разбор XMLTV. Возвращает false, если формат
        /// не построчный и нужен запасной разбор через XmlReader.
        /// </summary>
        private static bool ParseLines(Stream stream, IList<Channel> channels, EpgData data)
        {
            var wantedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in channels)
            {
                if (!string.IsNullOrWhiteSpace(ch.TvgId))
                    wantedIds.Add(ch.TvgId.Trim());
                else
                {
                    var key = NormalizeName(ch.Name);
                    if (key.Length > 0) missingNames.Add(key);
                }
            }

            var displayNameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var now = DateTime.Now;
            var from = now.AddHours(-2);
            var to = now.AddHours(24);
            bool sawProgramme = false;

            using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 1 << 20))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length < 10)
                        continue;

                    if (line[0] != '<')
                        continue;

                    if (line.StartsWith("<channel ", StringComparison.Ordinal))
                    {
                        var id = AttrValue(line, "id");
                        if (string.IsNullOrWhiteSpace(id))
                            continue;

                        var displayName = ElementText(line, "display-name");
                        if (string.IsNullOrWhiteSpace(displayName))
                            continue;

                        var key = NormalizeName(displayName);
                        if (key.Length == 0)
                            continue;

                        if (!displayNameToId.ContainsKey(key))
                            displayNameToId[key] = id;
                        if (missingNames.Contains(key))
                            wantedIds.Add(id);
                    }
                    else if (line.StartsWith("<programme ", StringComparison.Ordinal))
                    {
                        sawProgramme = true;

                        // Многострочный programme — построчный разбор не подходит.
                        if (line.IndexOf("</programme>", StringComparison.Ordinal) < 0)
                            return false;

                        var channelId = AttrValue(line, "channel");
                        if (string.IsNullOrWhiteSpace(channelId) || !wantedIds.Contains(channelId))
                            continue;

                        var start = ParseXmltvDateFast(AttrValue(line, "start"));
                        var stop = ParseXmltvDateFast(AttrValue(line, "stop"));
                        if (start == DateTime.MinValue || stop == DateTime.MinValue)
                            continue;
                        if (stop < from || start > to)
                            continue;

                        var title = ElementText(line, "title");
                        if (string.IsNullOrWhiteSpace(title))
                            continue;

                        List<EpgProgram> list;
                        if (!data.ById.TryGetValue(channelId, out list))
                        {
                            list = new List<EpgProgram>();
                            data.ById[channelId] = list;
                        }
                        list.Add(new EpgProgram { Title = title.Trim(), Start = start, Stop = stop });
                    }
                }
            }

            if (!sawProgramme && data.ById.Count == 0)
                return false;

            FinishParsing(channels, data, displayNameToId);
            return true;
        }

        private static void FinishParsing(IList<Channel> channels, EpgData data,
            Dictionary<string, string> displayNameToId)
        {
            foreach (var list in data.ById.Values)
                list.Sort((a, b) => a.Start.CompareTo(b.Start));

            foreach (var ch in channels)
            {
                var key = NormalizeName(ch.Name);
                if (key.Length == 0) continue;

                string id = null;
                if (!string.IsNullOrWhiteSpace(ch.TvgId))
                    id = ch.TvgId.Trim();
                else if (displayNameToId.ContainsKey(key))
                    id = displayNameToId[key];

                if (string.IsNullOrEmpty(id)) continue;

                List<EpgProgram> list;
                if (!data.ById.TryGetValue(id, out list)) continue;

                data.NameToId[key] = id;
                if (!data.ByName.ContainsKey(key))
                    data.ByName[key] = list;
            }
        }

        private static string AttrValue(string line, string name)
        {
            var marker = name + "=\"";
            int start = line.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (start < 0) return null;
            }
            start += marker.Length;
            int end = line.IndexOf('"', start);
            return end > start ? line.Substring(start, end - start) : null;
        }

        private static string ElementText(string line, string tag)
        {
            var open = "<" + tag;
            int start = line.IndexOf(open, StringComparison.Ordinal);
            if (start < 0) return null;

            start = line.IndexOf('>', start);
            if (start < 0) return null;
            start++;

            var close = "</" + tag + ">";
            int end = line.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0) return null;

            var text = line.Substring(start, end - start);
            return System.Net.WebUtility.HtmlDecode(text);
        }

        private static DateTime ParseXmltvDateFast(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 12)
                return DateTime.MinValue;

            int year, month, day, hour, minute, second = 0;
            if (!TryInt(text, 0, 4, out year) ||
                !TryInt(text, 4, 2, out month) ||
                !TryInt(text, 6, 2, out day) ||
                !TryInt(text, 8, 2, out hour) ||
                !TryInt(text, 10, 2, out minute))
                return DateTime.MinValue;

            if (text.Length >= 14)
                TryInt(text, 12, 2, out second);

            if (year < 1990 || month < 1 || month > 12 || day < 1 || day > 31 ||
                hour > 23 || minute > 59 || second > 59)
                return DateTime.MinValue;

            DateTime dt;
            try
            {
                dt = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
            }
            catch
            {
                return DateTime.MinValue;
            }

            int offsetIndex = text.IndexOf(' ', 14);
            if (offsetIndex > 0 && offsetIndex + 5 < text.Length &&
                (text[offsetIndex + 1] == '+' || text[offsetIndex + 1] == '-'))
            {
                int oh, om;
                if (TryInt(text, offsetIndex + 2, 2, out oh) && TryInt(text, offsetIndex + 4, 2, out om))
                {
                    int sign = text[offsetIndex + 1] == '-' ? -1 : 1;
                    var utc = DateTime.SpecifyKind(dt, DateTimeKind.Utc).AddMinutes(-sign * (oh * 60 + om));
                    return utc.ToLocalTime();
                }
            }

            return dt;
        }

        private static bool TryInt(string text, int index, int length, out int value)
        {
            value = 0;
            if (text.Length < index + length) return false;
            for (int i = index; i < index + length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            return true;
        }

        private static string Hash(string text)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                foreach (var c in text)
                {
                    h ^= c;
                    h *= 1099511628211UL;
                }
                return h.ToString("x16");
            }
        }

        private static string ChannelSignature(IList<Channel> channels)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ch in channels)
                sb.Append(ch.TvgId).Append('|').Append(ch.Name).Append('\n');
            return Hash(sb.ToString());
        }

        private static void SaveCache(string path, EpgData data, string signature)
        {
            var tmp = path + ".tmp";
            try
            {
                using (var file = File.Create(tmp))
                using (var w = new BinaryWriter(file))
                {
                    w.Write("IPTVEPG1");
                    w.Write(signature);
                    w.Write(DateTime.UtcNow.Ticks);

                    w.Write(data.ById.Count);
                    foreach (var kv in data.ById)
                    {
                        w.Write(kv.Key);
                        w.Write(kv.Value.Count);
                        foreach (var p in kv.Value)
                        {
                            w.Write(p.Title ?? "");
                            w.Write(p.Start.Ticks);
                            w.Write(p.Stop.Ticks);
                        }
                    }

                    w.Write(data.NameToId.Count);
                    foreach (var kv in data.NameToId)
                    {
                        w.Write(kv.Key);
                        w.Write(kv.Value);
                    }
                }

                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private static EpgData TryLoadCache(string path, string signature)
        {
            try
            {
                if (!File.Exists(path)) return null;

                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                if (age.TotalHours > CacheMaxAgeHours) return null;

                using (var file = File.OpenRead(path))
                using (var r = new BinaryReader(file))
                {
                    if (r.ReadString() != "IPTVEPG1") return null;
                    if (r.ReadString() != signature) return null;

                    var created = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
                    if ((DateTime.UtcNow - created).TotalHours > CacheMaxAgeHours) return null;

                    var data = new EpgData();

                    int idCount = r.ReadInt32();
                    for (int i = 0; i < idCount; i++)
                    {
                        var id = r.ReadString();
                        int count = r.ReadInt32();
                        var list = new List<EpgProgram>(count);
                        for (int j = 0; j < count; j++)
                        {
                            list.Add(new EpgProgram
                            {
                                Title = r.ReadString(),
                                Start = new DateTime(r.ReadInt64()),
                                Stop = new DateTime(r.ReadInt64())
                            });
                        }
                        data.ById[id] = list;
                    }

                    int nameCount = r.ReadInt32();
                    for (int i = 0; i < nameCount; i++)
                    {
                        var nameKey = r.ReadString();
                        var id = r.ReadString();
                        data.NameToId[nameKey] = id;
                        List<EpgProgram> list;
                        if (data.ById.TryGetValue(id, out list))
                            data.ByName[nameKey] = list;
                    }

                    return data;
                }
            }
            catch
            {
                return null;
            }
        }

        private static void ParseXml(XmlReader reader, IList<Channel> channels, EpgData data)
        {
            var wantedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in channels)
            {
                if (!string.IsNullOrWhiteSpace(ch.TvgId))
                    wantedIds.Add(ch.TvgId.Trim());
                else
                {
                    var key = NormalizeName(ch.Name);
                    if (key.Length > 0) missingNames.Add(key);
                }
            }

            var displayNameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var now = DateTime.Now;
            var from = now.AddHours(-2);
            var to = now.AddHours(24);

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                if (reader.Name == "channel")
                {
                    var id = reader.GetAttribute("id");
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    string displayName = null;
                    if (!reader.IsEmptyElement)
                    {
                        using (var sub = reader.ReadSubtree())
                        {
                            while (sub.Read())
                            {
                                if (sub.NodeType == XmlNodeType.Element && sub.Name == "display-name")
                                {
                                    displayName = sub.ReadElementContentAsString();
                                    break;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(displayName))
                        continue;

                    var key = NormalizeName(displayName);
                    if (key.Length == 0)
                        continue;

                    if (!displayNameToId.ContainsKey(key))
                        displayNameToId[key] = id;
                    if (missingNames.Contains(key))
                        wantedIds.Add(id);
                }
                else if (reader.Name == "programme")
                {
                    var channelId = reader.GetAttribute("channel");
                    if (string.IsNullOrWhiteSpace(channelId) || !wantedIds.Contains(channelId))
                    {
                        reader.Skip();
                        continue;
                    }

                    var start = ParseXmltvDate(reader.GetAttribute("start"));
                    var stop = ParseXmltvDate(reader.GetAttribute("stop"));

                    string title = null;
                    if (!reader.IsEmptyElement)
                    {
                        using (var sub = reader.ReadSubtree())
                        {
                            while (sub.Read())
                            {
                                if (sub.NodeType == XmlNodeType.Element && sub.Name == "title")
                                {
                                    title = sub.ReadElementContentAsString();
                                    break;
                                }
                            }
                        }
                    }

                    if (title == null || start == DateTime.MinValue || stop == DateTime.MinValue)
                        continue;
                    if (stop < from || start > to)
                        continue;

                    List<EpgProgram> list;
                    if (!data.ById.TryGetValue(channelId, out list))
                    {
                        list = new List<EpgProgram>();
                        data.ById[channelId] = list;
                    }
                    list.Add(new EpgProgram { Title = title.Trim(), Start = start, Stop = stop });
                }
            }

            FinishParsing(channels, data, displayNameToId);
        }

        private static DateTime ParseXmltvDate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return DateTime.MinValue;

            text = text.Trim();
            var formats = new[]
            {
                "yyyyMMddHHmmss zzz",
                "yyyyMMddHHmm zzz",
                "yyyyMMddHHmmss",
                "yyyyMMddHHmm"
            };

            DateTimeOffset dto;
            if (DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out dto))
                return dto.ToLocalTime().DateTime;

            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out dto))
                return dto.ToLocalTime().DateTime;

            return DateTime.MinValue;
        }

        public static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "";

            var s = name.ToLowerInvariant();
            s = Regex.Replace(s, @"\b(hd|fhd|uhd|4k|sd|hevc|h\.?264|h\.?265)\b", " ");
            s = Regex.Replace(s, @"[^0-9a-zа-яё]+", "");
            return s;
        }
    }
}
