using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Windows.Forms;

namespace IPTVViewer
{
    public class UpdateForm : Form
    {
        private readonly UpdateInfo _info;
        private TextBox _txtNotes;
        private Label _lblAsset;
        private Label _lblProgress;
        private ProgressBar _progress;
        private Button _btnDownload;
        private Button _btnOpen;
        private Button _btnLater;
        private WebClient _client;
        private bool _downloading;

        public UpdateForm(UpdateInfo info)
        {
            _info = info;
            InitializeComponent();
            FillData();
        }

        private void InitializeComponent()
        {
            this.Text = "Обновление IPTV Newline";
            this.Icon = AppIcon.Get();
            this.Size = new Size(680, 580);
            this.MinimumSize = new Size(560, 460);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Theme.Bg;
            this.ForeColor = Theme.Text;
            this.Font = Theme.FontRegular;
            this.DoubleBuffered = true;

            var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Panel };
            var title = Theme.CreateLabel("Доступно обновление", false, 15f, true);
            title.Location = new Point(20, 12);
            header.Controls.Add(title);

            var subtitle = Theme.CreateLabel("Обновление загружается с GitHub Releases", true, 9f, false);
            subtitle.Location = new Point(22, 38);
            header.Controls.Add(subtitle);
            this.Controls.Add(header);

            var lblCurrent = Theme.CreateLabel("Текущая версия: " + _info.CurrentVersion, true, 9.5f, false);
            lblCurrent.Location = new Point(22, 82);
            this.Controls.Add(lblCurrent);

            var lblLatest = Theme.CreateLabel("Новая версия: " + _info.LatestVersion, false, 10.5f, true);
            lblLatest.ForeColor = Theme.AccentHover;
            lblLatest.Location = new Point(22, 106);
            this.Controls.Add(lblLatest);

            var lblNotes = Theme.CreateLabel("Что нового", true, 9f, false);
            lblNotes.Location = new Point(22, 142);
            this.Controls.Add(lblNotes);

            _txtNotes = new TextBox
            {
                Location = new Point(22, 164),
                Size = new Size(620, 250),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            Theme.StyleTextBox(_txtNotes);
            _txtNotes.BackColor = Theme.PanelAlt;
            this.Controls.Add(_txtNotes);

            _lblAsset = Theme.CreateLabel("", true, 8.5f, false);
            _lblAsset.Location = new Point(22, 424);
            _lblAsset.AutoSize = false;
            _lblAsset.Size = new Size(620, 18);
            _lblAsset.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            this.Controls.Add(_lblAsset);

            _progress = new ProgressBar
            {
                Location = new Point(22, 448),
                Size = new Size(500, 18),
                Visible = false,
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            this.Controls.Add(_progress);

            _lblProgress = Theme.CreateLabel("", true, 8.5f, false);
            _lblProgress.Location = new Point(530, 448);
            _lblProgress.AutoSize = false;
            _lblProgress.Size = new Size(112, 18);
            _lblProgress.TextAlign = ContentAlignment.MiddleRight;
            _lblProgress.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            _lblProgress.Visible = false;
            this.Controls.Add(_lblProgress);

            _btnDownload = Theme.CreateButton("Скачать обновление", true);
            _btnDownload.Size = new Size(190, 38);
            _btnDownload.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            _btnDownload.Location = new Point(452, 492);
            _btnDownload.Click += (s, e) => StartDownload();
            this.Controls.Add(_btnDownload);

            _btnOpen = Theme.CreateButton("Открыть на GitHub", false);
            _btnOpen.Size = new Size(160, 38);
            _btnOpen.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _btnOpen.Location = new Point(22, 492);
            _btnOpen.Click += (s, e) => OpenReleasePage();
            this.Controls.Add(_btnOpen);

            _btnLater = Theme.CreateButton("Позже", false);
            _btnLater.Size = new Size(90, 38);
            _btnLater.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            _btnLater.Location = new Point(352, 492);
            _btnLater.Click += (s, e) => this.Close();
            this.Controls.Add(_btnLater);

            this.CancelButton = _btnLater;
        }

        private void FillData()
        {
            var notes = string.IsNullOrWhiteSpace(_info.Notes)
                ? "Описание изменений не указано."
                : _info.Notes.Replace("\r\n", "\n").Replace("\n", "\r\n");
            _txtNotes.Text = notes;

            var asset = _info.PreferredAsset;
            if (asset != null && !string.IsNullOrEmpty(asset.DownloadUrl))
            {
                var size = UpdateChecker.FormatSize(asset.Size);
                var kind = IsExeAsset(asset) ? "однофайловая версия, установка в один клик" : "архив, установка вручную";
                _lblAsset.Text = "Файл: " + asset.Name + (string.IsNullOrEmpty(size) ? "" : "  ·  " + size) + "  ·  " + kind;
            }
            else
            {
                _lblAsset.Text = "Готовый файл обновления в релизе не найден — откройте страницу GitHub.";
                _btnDownload.Enabled = false;
            }
        }

        private static bool IsExeAsset(ReleaseAsset asset)
        {
            return asset != null && !string.IsNullOrEmpty(asset.Name) &&
                   asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        private void OpenReleasePage()
        {
            try
            {
                var url = string.IsNullOrEmpty(_info.ReleaseUrl)
                    ? "https://github.com/" + UpdateChecker.DefaultRepo + "/releases"
                    : _info.ReleaseUrl;
                Process.Start(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось открыть браузер:\n" + ex.Message,
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StartDownload()
        {
            if (_downloading) return;
            var asset = _info.PreferredAsset;
            if (asset == null || string.IsNullOrEmpty(asset.DownloadUrl))
            {
                OpenReleasePage();
                return;
            }

            string target;
            try
            {
                target = BuildTargetPath(asset);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось определить папку для загрузки:\n" + ex.Message,
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _downloading = true;
            _btnDownload.Enabled = false;
            _btnOpen.Enabled = false;
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Visible = true;
            _lblProgress.Visible = true;
            _progress.Value = 0;
            _lblProgress.Text = "0 %";

            try
            {
                _client = new WebClient();
                _client.Headers.Add("User-Agent", "IPTVViewer-Updater");
                _client.DownloadProgressChanged += (s, e) =>
                {
                    _progress.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
                    _lblProgress.Text = e.ProgressPercentage + " %";
                };
                _client.DownloadFileCompleted += (s, e) => OnDownloadCompleted(e, target);
                _client.DownloadFileAsync(new Uri(asset.DownloadUrl), target);
            }
            catch (Exception ex)
            {
                ResetDownloadState();
                MessageBox.Show(this, "Не удалось начать загрузку:\n" + ex.Message,
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnDownloadCompleted(System.ComponentModel.AsyncCompletedEventArgs e, string target)
        {
            if (this.IsDisposed) return;

            ResetDownloadState();

            if (e.Cancelled)
            {
                TryDelete(target);
                return;
            }

            if (e.Error != null)
            {
                TryDelete(target);
                MessageBox.Show(this, "Не удалось скачать обновление:\n" + e.Error.Message,
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (IsExeAsset(_info.PreferredAsset))
            {
                // однофайловая версия: предлагаем сразу установить
                var answer = MessageBox.Show(this,
                    "Обновление скачано (версия " + (_info.LatestVersion ?? "") + ").\n\n" +
                    "Установить сейчас? Приложение будет заменено новой версией и перезапущено.",
                    "Установка обновления", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (answer == DialogResult.Yes)
                    InstallAndUpdate(target);
                else
                    SetStatusDownloaded();
            }
            else
            {
                // запасной вариант (zip): просто показываем файл
                var answer = MessageBox.Show(this,
                    "Обновление скачано:\n" + target +
                    "\n\nОткрыть папку с файлом?",
                    "Готово", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

                if (answer == DialogResult.Yes)
                {
                    try { Process.Start("explorer.exe", "/select,\"" + target + "\""); }
                    catch { }
                }
            }
        }

        private void SetStatusDownloaded()
        {
            try
            {
                _lblAsset.Text = "Файл скачан. Установить можно позже, нажав «Скачать обновление».";
            }
            catch { }
        }

        private void InstallAndUpdate(string newExePath)
        {
            string currentExe;
            try
            {
                currentExe = Process.GetCurrentProcess().MainModule.FileName;
            }
            catch
            {
                currentExe = System.Reflection.Assembly.GetEntryAssembly().Location;
            }

            if (string.IsNullOrEmpty(currentExe) ||
                string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(newExePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Не удалось определить путь к приложению.",
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _btnDownload.Enabled = false;
            _btnOpen.Enabled = false;
            _lblAsset.Text = "Установка: замена приложения...";
            _progress.Style = ProgressBarStyle.Marquee;
            _progress.Visible = true;

            try
            {
                // Windows разрешает переименование работающего exe —
                // освобождаем имя файла и копируем на него новую версию.
                var oldPath = currentExe + ".old";
                try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
                File.Move(currentExe, oldPath);
                File.Copy(newExePath, currentExe, true);

                Process.Start(new ProcessStartInfo
                {
                    FileName = currentExe,
                    WorkingDirectory = Path.GetDirectoryName(currentExe)
                });

                // завершаем текущее приложение: новая версия уже запущена
                this.Close();
                Application.Exit();
            }
            catch (Exception ex)
            {
                ResetDownloadState();
                try { _progress.Visible = false; } catch { }
                MessageBox.Show(this,
                    "Не удалось установить обновление:\n" + ex.Message +
                    "\n\nНовый файл лежит здесь:\n" + newExePath,
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ResetDownloadState()
        {
            _downloading = false;
            if (_client != null)
            {
                try { _client.Dispose(); } catch { }
                _client = null;
            }
            _progress.Visible = false;
            _lblProgress.Visible = false;
            _btnDownload.Enabled = _info.PreferredAsset != null;
            _btnOpen.Enabled = true;
        }

        private static string BuildTargetPath(ReleaseAsset asset)
        {
            var name = string.IsNullOrWhiteSpace(asset.Name)
                ? "IPTVViewer-update.exe"
                : Path.GetFileName(asset.Name);
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            // исполнительный файл качаем во внутреннюю папку, откуда
            // InstallAndUpdate перезапишет текущее приложение;
            // резервный архив — в обычную папку загрузок
            var dir = IsExeAsset(asset)
                ? Path.Combine(SettingsManager.DataDir, "updates")
                : GetDownloadDirectory();
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, name);
            if (!IsExeAsset(asset) && File.Exists(path))
            {
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                path = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "-" + stamp + Path.GetExtension(name));
            }
            else if (File.Exists(path))
            {
                // повторно качаем exe — перезаписываем поверх
                try { File.Delete(path); } catch { }
            }
            return path;
        }

        private static string GetDownloadDirectory()
        {
            try
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var downloads = Path.Combine(profile, "Downloads");
                if (Directory.Exists(downloads)) return downloads;
            }
            catch { }

            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop)) return desktop;
            }
            catch { }

            return Path.Combine(SettingsManager.DataDir, "updates");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_downloading)
            {
                var answer = MessageBox.Show(this,
                    "Загрузка ещё не завершена. Прервать её?",
                    "Обновление", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                try
                {
                    if (_client != null) _client.CancelAsync();
                }
                catch { }
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_client != null)
                {
                    try { _client.Dispose(); } catch { }
                    _client = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
