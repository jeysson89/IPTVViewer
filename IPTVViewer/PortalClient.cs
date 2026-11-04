using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IPTVViewer
{
    /// <summary>
    /// Нативная поддержка веб-терминала newline.iptvportal.ru.
    /// Протокол воспроизведён из клиентского скрипта портала:
    ///  1) get_domain_config (открытый JSON-RPC);
    ///  2) login — тело шифруется AES-128-CBC, AES-ключ передаётся
    ///     в HTTP-заголовке Ares-Cipher (RSA-PKCS#1 v1.5, открытый ключ вшит);
    ///  3) authorize_terminal со session_id возвращает полный медиа-список;
    ///  4) get_epg per-канал на сегодня возвращает телепрограмму.
    /// Вход анонимный: портал сам регистрирует терминал из сети
    /// провайдера (allow_anonymous=true в конфиге домена).
    /// </summary>
    public static class PortalClient
    {
        public class PortalResult
        {
            public List<Channel> Channels { get; set; }
            public EpgData Epg { get; set; }
            public string Error { get; set; }

            public PortalResult()
            {
                Channels = new List<Channel>();
            }
        }

        private const string FirmwareVersion =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 IPTV Newline";

        private const string RsaModulusHex =
            "cf2cde3395737e94f0f0ca7474797d285170eb7acd41628a946f92a00138225a" +
            "23c2b7307da8c182536bfa325d7a111dc82eb0825c85614d807b46bdc0bea823" +
            "1dde941b2d435782dabed3381b48f30aa19bff261eec4086b56d5c45ea394b8f" +
            "386d1ad75b7e8c5d37d517e83e598719afb2b05b790e41f132858321f693c025";

        // IV "arescript      " (9 букв + 7 пробелов) — из скрипта портала
        private static readonly byte[] AresIv =
            { 97, 114, 101, 115, 99, 114, 121, 112, 116, 32, 32, 32, 32, 32, 32, 32 };

        private const string Base64Alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", FirmwareVersion);
            client.Timeout = TimeSpan.FromSeconds(25);
            return client;
        }

        private static int _requestId;

        // ===== Загрузка портала =====

        public static PortalResult Load(string portalUrl) { return Load(portalUrl, null); }

        /// <summary>Полный цикл: вход → медиа-список → EPG. Ошибки — в Error.</summary>
        public static PortalResult Load(string portalUrl, Action<string> step)
        {
            var result = new PortalResult();
            try
            {
                var baseUri = new Uri(portalUrl);
                if (step != null) step("get_domain_config");

                // 1) конфиг домена — проверяем доступность и анонимный вход
                using (var cfg = JsonRpc(baseUri, "get_domain_config",
                    new Dictionary<string, object> { { "terminal_type", "browser" } }, false))
                {
                    bool allowAnonymous = false;
                    var root = cfg.RootElement;
                    if (root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("result", out var r) &&
                        r.ValueKind == JsonValueKind.Object &&
                        r.TryGetProperty("allow_anonymous", out var aa) &&
                        aa.ValueKind == JsonValueKind.True)
                        allowAnonymous = true;
                    if (step != null) step("get_domain_config ok, allowAnonymous=" + allowAnonymous);
                    if (!allowAnonymous)
                        throw new InvalidOperationException(
                            "Портал больше не принимает вход без учётной записи.");
                }

                // 2) login (шифрованный) → session_id
                if (step != null) step("login...");
                var session = Login(baseUri);
                if (step != null) step("login ok, session=" + (session.SessionId ?? "").Substring(0, 8) + "…");
                if (string.IsNullOrEmpty(session.SessionId))
                    throw new InvalidOperationException("Портал не выдал сессию.");

                // 3) authorize_terminal → медиа-список
                if (step != null) step("authorize_terminal...");
                using (var auth = JsonRpc(baseUri, "authorize_terminal",
                    new Dictionary<string, object>
                    {
                        { "session_id", session.SessionId },
                        { "version", 2 },
                        { "require_media_tokens", true },
                        { "require_media_keys", true },
                        { "result_as", "csv" }
                    }, false))
                {
                    var root = auth.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var r))
                        throw new InvalidOperationException("Портал не отдал список каналов.");

                    if (r.ValueKind == JsonValueKind.Object &&
                        r.TryGetProperty("medialist", out var media) &&
                        media.ValueKind == JsonValueKind.Array)
                    {
                        result.Channels = ParseMediaList(media);
                    }
                    else if (r.ValueKind == JsonValueKind.Object &&
                             r.TryGetProperty("authorized", out var authorized) &&
                             authorized.ValueKind == JsonValueKind.False)
                    {
                        throw new InvalidOperationException("Портал не авторизовал терминал.");
                    }

                    if (result.Channels.Count == 0)
                        throw new InvalidOperationException("Список каналов пуст.");
                    if (step != null) step("authorize ok, channels=" + result.Channels.Count);
                }

                // 4) EPG на сегодня (некритично — при ошибке вернётся пустой)
                if (step != null) step("epg...");
                result.Epg = LoadEpg(baseUri, session.SessionId, result.Channels, step);
                if (step != null) step("epg ok, empty=" + (result.Epg == null || result.Epg.IsEmpty));
            }
            catch (HttpRequestException ex)
            {
                result.Error = "Портал недоступен: " + ex.Message;
            }
            catch (OperationCanceledException)
            {
                result.Error = "Портал не ответил вовремя.";
            }
            catch (Exception ex)
            {
                result.Error = "Не удалось загрузить портал: " + ex.Message;
            }
            return result;
        }

        // ===== JSON-RPC =====

        private static JsonDocument JsonRpc(
            Uri baseUri, string method, Dictionary<string, object> parameters, bool encrypt)
        {
            var id = Interlocked.Increment(ref _requestId);
            var plain = "{\"jsonrpc\":\"2.0\",\"id\":" + id +
                        ",\"method\":\"" + JsonQuote(method) + "\",\"params\":" +
                        JsonEncodeParams(parameters) + "}";

            string text;
            if (encrypt)
            {
                var key = RandomBytes(16);
                var cipherHeader = AresCipherHeader(key);
                var encrypted = AesEncrypt(plain, key);

                var posted = new HttpRequestMessage(HttpMethod.Post, Path(baseUri, method));
                posted.Headers.TryAddWithoutValidation("Ares-Cipher", cipherHeader);
                posted.Content = new StringContent(encrypted, Encoding.UTF8, "application/json");
                using (posted.Content)
                using (var response = _http.SendAsync(posted).GetAwaiter().GetResult())
                {
                    var scrambled = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    text = AesDecrypt(scrambled, key);
                }
            }
            else
            {
                var posted = new HttpRequestMessage(HttpMethod.Post, Path(baseUri, method));
                posted.Content = new StringContent(plain, Encoding.UTF8, "application/json");
                using (posted.Content)
                using (var response = _http.SendAsync(posted).GetAwaiter().GetResult())
                {
                    text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            }

            if (string.IsNullOrEmpty(text))
                throw new InvalidOperationException("Пустой ответ портала.");

            return JsonDocument.Parse(text);
        }

        private static Uri Path(Uri baseUri, string method)
        {
            return new Uri(baseUri, "/jsonrpc/" + method);
        }

        // ===== JSON helpers =====

        private static string JsonEncodeParams(Dictionary<string, object> parameters)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (var kv in parameters)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(JsonQuote(kv.Key)).Append("\":");
                AppendValue(sb, kv.Value);
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendValue(StringBuilder sb, object value)
        {
            if (value == null)
                sb.Append("null");
            else if (value is bool b)
                sb.Append(b ? "true" : "false");
            else if (value is int i)
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
            else if (value is long l)
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
            else if (value is string s)
                sb.Append('"').Append(JsonQuote(s)).Append('"');
            else
                sb.Append('"').Append(JsonQuote(value.ToString())).Append('"');
        }

        private static string JsonQuote(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 32)
                            sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else
                            sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        private static string GetString(JsonElement obj, string key)
        {
            try
            {
                if (obj.ValueKind == JsonValueKind.Object &&
                    obj.TryGetProperty(key, out var v) &&
                    v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            catch { }
            return null;
        }

        // ===== Шифрование (протокол портала) =====

        private static byte[] RandomBytes(int count)
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                var bytes = new byte[count];
                rng.GetBytes(bytes);
                return bytes;
            }
        }

        private static string RsaWrap(byte[] payload)
        {
            using (var rsa = new RSACryptoServiceProvider(1024))
            {
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = HexToBytes(RsaModulusHex),
                    Exponent = new byte[] { 0x01, 0x00, 0x01 }
                });
                var chunkSize = 116; // 128 (длина ключа) − 11..12, как в скрипте портала
                var chunks = new List<byte[]>();
                for (int offset = 0; offset < payload.Length; offset += chunkSize)
                {
                    var count = Math.Min(chunkSize, payload.Length - offset);
                    var chunk = new byte[count];
                    Array.Copy(payload, offset, chunk, 0, count);
                    chunks.Add(chunk); // каждый блок шифруем отдельно
                }
                var hex = new StringBuilder();
                foreach (var chunk in chunks)
                {
                    var encrypted = rsa.Encrypt(chunk, false);
                    hex.Append(BytesToHex(encrypted));
                }
                return HexToB64(hex.ToString());
            }
        }

        /// <summary>78-байтный заголовок: версия/алгоритм + таймстамп + AES-ключ + IV.</summary>
        private static string AresCipherHeader(byte[] aesKey)
        {
            var header = new byte[78];
            WriteUInt16(header, 0, 1);
            WriteString(header, 2, 16, "aes");
            WriteString(header, 18, 8, "cbc");
            WriteString(header, 26, 8, "base64");
            WriteUInt16(header, 34, 128);
            WriteUInt16(header, 36, 16);
            WriteUInt16(header, 38, 46);
            WriteUInt16(header, 40, 62);
            WriteUInt32(header, 42, UnixTimestamp());
            Array.Copy(aesKey, 0, header, 46, 16);
            Array.Copy(AresIv, 0, header, 62, 16);
            return RsaWrap(header);
        }

        private static uint UnixTimestamp()
        {
            return (uint)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond - 62135596800L);
        }

        private static string AesEncrypt(string plain, byte[] key)
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize = 128;
                aes.Key = key;
                aes.IV = AresIv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var transform = aes.CreateEncryptor())
                {
                    var plainBytes = Encoding.UTF8.GetBytes(plain);
                    var cipher = transform.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                    // как в портале: base64 с переносом строки каждые 64 символа
                    var b64 = Convert.ToBase64String(cipher);
                    return WrapLines(b64, 64);
                }
            }
        }

        private static string AesDecrypt(string encoded, byte[] key)
        {
            try
            {
                var compact = (encoded ?? "").Replace("\r", "").Replace("\n", "");
                var cipher = Convert.FromBase64String(compact);
                using (var aes = Aes.Create())
                {
                    aes.KeySize = 128;
                    aes.Key = key;
                    aes.IV = AresIv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    using (var transform = aes.CreateDecryptor())
                    {
                        var plain = transform.TransformFinalBlock(cipher, 0, cipher.Length);
                        return Encoding.UTF8.GetString(plain);
                    }
                }
            }
            catch
            {
                return encoded; // сервер прислал неотправленный JSON — отдаём как есть
            }
        }

        private static string WrapLines(string text, int width)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < text.Length; i += width)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(text, i, Math.Min(width, text.Length - i));
            }
            return sb.ToString();
        }

        private static byte[] HexToBytes(string hex)
        {
            var data = new byte[hex.Length / 2];
            for (int i = 0; i < data.Length; i++)
                data[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return data;
        }

        private static string BytesToHex(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 2);
            foreach (var b in data)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>Повторяет Qo из скрипта портала: hex → base64 группами по 3 hex-цифры.</summary>
        private static string HexToB64(string hex)
        {
            var sb = new StringBuilder();
            int c = 0;
            for (; c + 3 <= hex.Length; c += 3)
            {
                int d = Convert.ToInt32(hex.Substring(c, 3), 16);
                sb.Append(Base64Alphabet[d >> 6]).Append(Base64Alphabet[d & 63]);
            }
            if (c + 1 == hex.Length)
            {
                int d = Convert.ToInt32(hex.Substring(c, 1), 16);
                sb.Append(Base64Alphabet[d << 2]);
            }
            else if (c + 2 == hex.Length)
            {
                int d = Convert.ToInt32(hex.Substring(c, 2), 16);
                sb.Append(Base64Alphabet[d >> 2]).Append(Base64Alphabet[(d & 3) << 4]);
            }
            while (sb.Length % 4 != 0)
                sb.Append('=');
            return sb.ToString();
        }

        private static void WriteUInt16(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value & 0xFF);
            target[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteUInt32(byte[] target, int offset, uint value)
        {
            for (int i = 0; i < 4; i++)
                target[offset + i] = (byte)((value >> (8 * i)) & 0xFF);
        }

        private static void WriteString(byte[] target, int offset, int fieldSize, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            for (int i = 0; i < fieldSize; i++)
                target[offset + i] = i < bytes.Length ? bytes[i] : (byte)0;
        }

        // ===== login =====

        private class PortalSession
        {
            public string SessionId { get; set; }
            public string UserId { get; set; }
        }

        /// <summary>MAC-адрес терминала храним между запусками — стабильная учётка.</summary>
        private static string LoadMacaddr()
        {
            var path = System.IO.Path.Combine(SettingsManager.DataDir, "portal.mac");
            try
            {
                if (File.Exists(path))
                {
                    var txt = File.ReadAllText(path).Trim();
                    if (txt.Length == 32)
                        return txt;
                }
            }
            catch { }
            return null;
        }

        private static void SaveMacaddr(string mac)
        {
            if (string.IsNullOrEmpty(mac) || mac.Length != 32) return;
            try
            {
                Directory.CreateDirectory(SettingsManager.DataDir);
                File.WriteAllText(System.IO.Path.Combine(SettingsManager.DataDir, "portal.mac"), mac);
            }
            catch { }
        }

        private static PortalSession Login(Uri baseUri)
        {
            var mac = LoadMacaddr();
            var parameters = new Dictionary<string, object>
            {
                { "macaddr", mac },
                { "create_macaddr", mac == null },
                { "require_session", true },
                { "require_user_id", true },
                { "terminal_type", "browser" },
                { "firmware_version", FirmwareVersion }
            };

            var id = Interlocked.Increment(ref _requestId);
            var plain = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"login\",\"params\":" +
                        JsonEncodeParams(parameters) + "}";

            var key = RandomBytes(16);
            var encrypted = AesEncrypt(plain, key);

            var posted = new HttpRequestMessage(HttpMethod.Post, Path(baseUri, "login"));
            posted.Headers.TryAddWithoutValidation("Ares-Cipher", AresCipherHeader(key));
            posted.Content = new StringContent(encrypted, Encoding.UTF8, "application/json");

            string text;
            using (posted.Content)
            using (var response = _http.SendAsync(posted).GetAwaiter().GetResult())
            {
                var scrambled = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                text = AesDecrypt(scrambled, key);
            }

            if (string.IsNullOrEmpty(text))
                throw new InvalidOperationException("Портал отклонил вход.");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text);
            }
            catch
            {
                throw new InvalidOperationException("Портал отклонил вход.");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("result", out var r) ||
                    r.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Портал отклонил вход.");

                var session = new PortalSession
                {
                    SessionId = GetString(r, "session_id"),
                    UserId = GetString(r, "user_id")
                };

                if (r.TryGetProperty("macaddr", out var assigned) &&
                    assigned.ValueKind == JsonValueKind.String)
                {
                    SaveMacaddr(assigned.GetString());
                }

                return session;
            }
        }

        // ===== Медиа-список =====

        private static List<Channel> ParseMediaList(JsonElement medialist)
        {
            var channels = new List<Channel>();
            var rows = medialist.EnumerateArray().ToList();
            if (rows.Count < 2) return channels;

            var header = rows[0].EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : "")
                .ToList();

            int IndexOf(string name) => header.FindIndex(
                h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));

            int iProtocol = IndexOf("protocol"), iHost = IndexOf("host"), iPort = IndexOf("port"),
                iPath = IndexOf("path"), iName = IndexOf("name"), iTitle = IndexOf("title"),
                iLogo = IndexOf("logo_url"), iIsTv = IndexOf("is_tv"), iIsRadio = IndexOf("is_radio"),
                iIsCam = IndexOf("is_cam"), iEncrypted = IndexOf("encrypted"), iAuth = IndexOf("auth"),
                iPaid = IndexOf("paid"), iMediaId = IndexOf("id"), iGroupId = IndexOf("group_id");

            foreach (var row in rows.Skip(1))
            {
                if (row.ValueKind != JsonValueKind.Array) continue;
                var cells = row.EnumerateArray().ToList();

                string Get(int index)
                {
                    if (index < 0 || index >= cells.Count) return null;
                    var v = cells[index];
                    if (v.ValueKind == JsonValueKind.String) return v.GetString();
                    if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
                    return null;
                }

                bool GetFlag(int index)
                {
                    if (index < 0 || index >= cells.Count) return false;
                    var v = cells[index];
                    if (v.ValueKind == JsonValueKind.True) return true;
                    if (v.ValueKind == JsonValueKind.Number) return v.GetInt64() != 0;
                    if (v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString();
                        return s == "1" || s == "true";
                    }
                    return false;
                }

                var host = Get(iHost);
                var path = Get(iPath);
                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(path))
                    continue;

                if (!GetFlag(iIsTv) && !GetFlag(iIsRadio))
                    continue;
                if (GetFlag(iEncrypted) || GetFlag(iAuth) || GetFlag(iPaid) || GetFlag(iIsCam))
                    continue;

                var name = (Get(iName) ?? Get(iTitle) ?? "Канал").Trim();
                var port = Get(iPort) ?? "80";
                var url = Get(iProtocol) + "://" + host + ":" + port + "/" + path.TrimStart('/');

                channels.Add(new Channel
                {
                    Name = name,
                    Url = url,
                    TvgId = Get(iMediaId),
                    Logo = Get(iLogo),
                    Group = "Эфирные каналы"
                });
            }

            return channels;
        }

        // ===== EPG =====

        /// <summary>EPG портала на сегодня: get_epg по каждому медиа-id (параллельно).</summary>
        public static EpgData LoadEpg(Uri baseUri, string sessionId, List<Channel> channels)
        {
            return LoadEpg(baseUri, sessionId, channels, null);
        }

        public static EpgData LoadEpg(
            Uri baseUri, string sessionId, List<Channel> channels, Action<string> step)
        {
            var data = new EpgData();
            if (string.IsNullOrEmpty(sessionId) || channels == null || channels.Count == 0)
                return data;

            var byId = new Dictionary<string, List<EpgProgram>>(StringComparer.OrdinalIgnoreCase);
            var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var mediaIds = channels
                .Where(c => !string.IsNullOrWhiteSpace(c.TvgId))
                .Select(c => c.TvgId)
                .Distinct()
                .ToList();
            if (step != null) step("epg: ids=" + mediaIds.Count + " first=" +
                (mediaIds.Count > 0 ? mediaIds[0] : "-") + " today=" + today);

            int okCount = 0, errCount = 0;
            string firstRaw = null;

            Parallel.ForEach(mediaIds,
                new ParallelOptions { MaxDegreeOfParallelism = 6 },
                mediaId =>
                {
                    try
                    {
                        // портал ждёт media_id числом (в браузерном терминале — JSON number)
                        object mediaIdParam = mediaId;
                        long numericId;
                        if (long.TryParse(mediaId, out numericId))
                            mediaIdParam = numericId;

                        var parameters = new Dictionary<string, object>
                        {
                            { "session_id", sessionId },
                            { "version", 2 },
                            { "result_as", "csv" },
                            { "media_id", mediaIdParam },
                            { "language_id", 1 },
                            { "start", today + " 00:00:00" },
                            { "stop", today + " 23:59:59" }
                        };
                        using (var doc = JsonRpc(baseUri, "get_epg", parameters, false))
                        {
                            var root = doc.RootElement;
                            if (root.ValueKind != JsonValueKind.Object ||
                                !root.TryGetProperty("result", out var result) ||
                                result.ValueKind != JsonValueKind.Object ||
                                !result.TryGetProperty("programs", out var programs) ||
                                programs.ValueKind != JsonValueKind.Array)
                            {
                                lock (byId)
                                {
                                    errCount++;
                                    if (firstRaw == null && root.ValueKind == JsonValueKind.Object)
                                    {
                                        if (root.TryGetProperty("error", out var err))
                                            firstRaw = "error: " + err.GetRawText();
                                        else
                                            firstRaw = "raw: " + root.GetRawText();
                                        if (firstRaw.Length > 300) firstRaw = firstRaw.Substring(0, 300);
                                    }
                                }
                                return;
                            }

                            var list = new List<EpgProgram>();
                            foreach (var row in programs.EnumerateArray())
                            {
                                if (row.ValueKind != JsonValueKind.Array) continue;
                                var cells = row.EnumerateArray().ToList();
                                if (cells.Count < 3) continue;
                                if (cells[0].ValueKind != JsonValueKind.String ||
                                    cells[1].ValueKind != JsonValueKind.String ||
                                    cells[2].ValueKind != JsonValueKind.String)
                                    continue;

                                var title = cells[2].GetString();
                                if (string.IsNullOrWhiteSpace(title)) continue;

                                var start = ParsePortalTime(cells[0].GetString());
                                var stop = ParsePortalTime(cells[1].GetString());
                                if (start == DateTime.MinValue || stop == DateTime.MinValue)
                                    continue;

                                list.Add(new EpgProgram { Title = title, Start = start, Stop = stop });
                            }

                            if (list.Count > 0)
                            {
                                lock (byId)
                                {
                                    okCount++;
                                    byId[mediaId] = list;
                                }
                            }
                            else
                            {
                                lock (byId)
                                {
                                    if (firstRaw == null)
                                    {
                                        firstRaw = "emptyList: " +
                                            programs.GetRawText();
                                        if (firstRaw.Length > 300)
                                            firstRaw = firstRaw.Substring(0, 300);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (byId)
                        {
                            errCount++;
                            if (firstRaw == null)
                                firstRaw = "ex: " + ex.Message;
                        }
                    }
                });

            if (step != null)
                step("epg done: ok=" + okCount + " err=" + errCount +
                     (firstRaw != null ? " first=" + firstRaw : ""));

            foreach (var kv in byId)
                data.ById[kv.Key] = kv.Value;
            foreach (var channel in channels)
            {
                if (string.IsNullOrWhiteSpace(channel.TvgId)) continue;
                List<EpgProgram> list;
                if (byId.TryGetValue(channel.TvgId, out list) && list != null)
                {
                    var key = EpgLoader.NormalizeName(channel.Name);
                    if (key.Length > 0 && !data.ByName.ContainsKey(key))
                        data.ByName[key] = list;
                }
            }

            return data;
        }

        /// <summary>"2026-11-03 12:30:00+0300" → локальное время.</summary>
        private static DateTime ParsePortalTime(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return DateTime.MinValue;
            try
            {
                int sp = text.IndexOf(' ');
                int plus = text.IndexOf('+', Math.Max(sp, 0));
                if (plus > 0) text = text.Substring(0, plus);
                return DateTime.ParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }
}
