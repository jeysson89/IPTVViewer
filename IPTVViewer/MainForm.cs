using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;

namespace IPTVViewer
{
    public partial class MainForm : Form
    {
        private const string SearchPlaceholder = "Поиск канала…";

        private readonly AppSettings _settings;
        private List<Channel> _allChannels = new List<Channel>();
        private LibVLC _libVLC;
        private MediaPlayer _mediaPlayer;
        private Media _currentMedia;
        private VideoView _videoView;

        private ComboBox _cbSource;
        private TextBox _txtSearch;
        private ListBox _lbChannels;
        private Button _btnLoad;
        private Button _btnPlay;
        private Button _btnStop;
        private Button _btnSettings;
        private Button _btnExternal;
        private Button _btnUpdate;
        private TrackBar _volume;
        private StatusStrip _status;
        private ToolStripStatusLabel _lblStatus;
        private int _hoverIndex = -1;
        private bool _searchPlaceholderActive = true;
        private bool _updateCheckRunning;

        public MainForm()
        {
            _settings = SettingsManager.Load();
            InitializeComponent();
            InitVlc();
            BuildUi();
            ApplySettings();
            if (_settings.AutoLoad)
                LoadSelectedSource();
        }

        private void InitializeComponent()
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            }
            catch { }

            this.Text = "IPTV Viewer — NEWLINE network";
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(_settings.WindowX, _settings.WindowY);
            this.ClientSize = new Size(_settings.WindowW, _settings.WindowH);
            this.MinimumSize = new Size(840, 520);
            this.BackColor = Theme.Bg;
            this.ForeColor = Theme.Text;
            this.Font = Theme.FontRegular;
            this.DoubleBuffered = true;
        }

        private void InitVlc()
        {
            try
            {
                Core.Initialize();
                _libVLC = new LibVLC();
                _mediaPlayer = new MediaPlayer(_libVLC);
                _mediaPlayer.Volume = 80;
                _mediaPlayer.EncounteredError += (s, e) => SetStatus("VLC: ошибка воспроизведения потока");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось инициализировать VLC:\n" + ex.Message,
                    "VLC", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BuildUi()
        {
            // ===== Верхняя панель =====
            var top = new Panel { Dock = DockStyle.Top, Height = 120, BackColor = Theme.Panel };

            var badge = new RoundedPanel
            {
                Location = new Point(16, 14),
                Size = new Size(176, 58),
                Radius = 10,
                FillColor = Color.White,
                Padding = new Padding(10, 8, 10, 8)
            };
            var logo = LoadLogo();
            if (logo != null)
            {
                var pic = new PictureBox
                {
                    Dock = DockStyle.Fill,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.White,
                    Image = logo
                };
                badge.Controls.Add(pic);
            }
            top.Controls.Add(badge);

            var title = Theme.CreateLabel("IPTV Viewer", false, 17f, true);
            title.Location = new Point(208, 16);
            top.Controls.Add(title);

            var subtitle = Theme.CreateLabel("NEWLINE network  ·  VLC engine", true, 9f, false);
            subtitle.Location = new Point(210, 46);
            top.Controls.Add(subtitle);

            _btnSettings = Theme.CreateButton("Настройки", false);
            _btnSettings.Size = new Size(110, 34);
            _btnSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnSettings.Location = new Point(top.Width - 126, 18);
            _btnSettings.Click += (s, e) => OpenSettings();
            top.Controls.Add(_btnSettings);

            _btnUpdate = Theme.CreateButton("Обновления", false);
            _btnUpdate.Size = new Size(130, 34);
            _btnUpdate.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnUpdate.Location = new Point(top.Width - 126 - 138, 18);
            _btnUpdate.Click += (s, e) => CheckForUpdates(false);
            top.Controls.Add(_btnUpdate);

            Action layoutTopButtons = () =>
            {
                _btnSettings.Left = top.Width - _btnSettings.Width - 16;
                _btnUpdate.Left = _btnSettings.Left - _btnUpdate.Width - 8;
            };
            top.Resize += (s, e) => layoutTopButtons();
            layoutTopButtons();

            var lblSource = Theme.CreateLabel("Источник:", true, 9.5f, false);
            lblSource.Location = new Point(18, 87);
            top.Controls.Add(lblSource);

            _cbSource = new ComboBox
            {
                Location = new Point(94, 82),
                Size = new Size(330, 26)
            };
            Theme.StyleCombo(_cbSource);
            _cbSource.Items.Add("IPTVPortal — newline.iptvportal.ru");
            _cbSource.Items.Add("ISP-NL — tv.isp-nl.ru/nltv.m3u");
            top.Controls.Add(_cbSource);

            _btnLoad = Theme.CreateButton("Загрузить плейлист", true);
            _btnLoad.Location = new Point(434, 80);
            _btnLoad.Size = new Size(150, 34);
            _btnLoad.Click += (s, e) => LoadSelectedSource();
            top.Controls.Add(_btnLoad);

            _btnExternal = Theme.CreateButton("Внешний плеер", false);
            _btnExternal.Location = new Point(594, 80);
            _btnExternal.Size = new Size(150, 34);
            _btnExternal.Click += (s, e) => PlaySelectedExternally();
            top.Controls.Add(_btnExternal);

            this.Controls.Add(top);

            // ===== Контент: слева каналы, справа видео =====
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Border,
                SplitterWidth = 2
            };

            var left = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel
            };

            var searchWrap = new Panel { Location = new Point(0, 0), Size = new Size(300, 52), Padding = new Padding(12, 10, 12, 6), BackColor = Theme.Panel };
            _txtSearch = new TextBox { Dock = DockStyle.Fill };
            Theme.StyleTextBox(_txtSearch);
            _txtSearch.Text = SearchPlaceholder;
            _txtSearch.ForeColor = Theme.TextDim;
            _txtSearch.GotFocus += (s, e) =>
            {
                if (_searchPlaceholderActive)
                {
                    _searchPlaceholderActive = false;
                    _txtSearch.Text = "";
                    _txtSearch.ForeColor = Theme.Text;
                }
            };
            _txtSearch.LostFocus += (s, e) =>
            {
                if (string.IsNullOrEmpty(_txtSearch.Text))
                {
                    _searchPlaceholderActive = true;
                    _txtSearch.Text = SearchPlaceholder;
                    _txtSearch.ForeColor = Theme.TextDim;
                }
            };
            _txtSearch.TextChanged += (s, e) =>
            {
                if (!_searchPlaceholderActive)
                    ApplyFilter(_txtSearch.Text);
            };
            searchWrap.Controls.Add(_txtSearch);
            left.Controls.Add(searchWrap);

            _lbChannels = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 48,
                IntegralHeight = false,
                Font = Theme.FontRegular
            };
            _lbChannels.DrawItem += ChannelList_DrawItem;
            _lbChannels.MouseMove += (s, e) =>
            {
                int idx = _lbChannels.IndexFromPoint(e.Location);
                if (idx != _hoverIndex)
                {
                    _hoverIndex = idx;
                    _lbChannels.Invalidate();
                }
            };
            _lbChannels.MouseLeave += (s, e) => { _hoverIndex = -1; _lbChannels.Invalidate(); };
            _lbChannels.DoubleClick += (s, e) => PlaySelected();
            _lbChannels.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { PlaySelected(); e.Handled = true; }
            };
            var listWrap = new Panel { Location = new Point(0, 52), Size = new Size(300, 300), BackColor = Theme.Panel };
            listWrap.Controls.Add(_lbChannels);
            left.Controls.Add(listWrap);

            Action layoutLeft = () =>
            {
                int w = Math.Max(0, left.ClientSize.Width);
                int h = Math.Max(0, left.ClientSize.Height);
                searchWrap.Location = new Point(0, 0);
                searchWrap.Size = new Size(w, 52);
                listWrap.Location = new Point(0, 52);
                listWrap.Size = new Size(w, Math.Max(0, h - 52));
            };
            left.Resize += (s, e) => layoutLeft();

            split.Panel1.Controls.Add(left);

            var right = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Theme.Bg
            };
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

            var videoWrap = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Padding = new Padding(0) };
            if (_mediaPlayer != null)
            {
                _videoView = new VideoView
                {
                    Dock = DockStyle.Fill,
                    MediaPlayer = _mediaPlayer,
                    BackColor = Color.Black
                };
                videoWrap.Controls.Add(_videoView);
            }
            right.Controls.Add(videoWrap, 0, 0);

            var controls = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(12, 8, 12, 8) };
            _btnPlay = Theme.CreateButton("▶  Воспроизвести", true);
            _btnPlay.Location = new Point(12, 9);
            _btnPlay.Size = new Size(150, 34);
            _btnPlay.Click += (s, e) => PlaySelected();

            _btnStop = Theme.CreateButton("■  Стоп", false);
            _btnStop.Location = new Point(170, 9);
            _btnStop.Size = new Size(90, 34);
            _btnStop.Click += (s, e) => StopPlayback();

            var lblVol = Theme.CreateLabel("Громкость", true, 9f, false);
            lblVol.Location = new Point(290, 18);

            _volume = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 80,
                TickStyle = TickStyle.None,
                Location = new Point(360, 10),
                Size = new Size(150, 30),
                BackColor = Theme.Panel,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _volume.Scroll += (s, e) => { if (_mediaPlayer != null) _mediaPlayer.Volume = _volume.Value; };

            controls.Controls.Add(_btnPlay);
            controls.Controls.Add(_btnStop);
            controls.Controls.Add(lblVol);
            controls.Controls.Add(_volume);
            controls.Resize += (s, e) => _volume.Left = controls.Width - _volume.Width - 16;
            right.Controls.Add(controls, 0, 1);

            split.Panel2.Controls.Add(right);

            this.Controls.Add(split);
            this.Shown += (s, e) =>
            {
                try
                {
                    split.Panel1MinSize = 260;
                    split.Panel2MinSize = 320;
                    split.FixedPanel = FixedPanel.Panel1;
                    split.SplitterDistance = 340;
                }
                catch { }

                if (_settings.CheckUpdatesOnStartup)
                {
                    var startupTimer = new Timer { Interval = 3000 };
                    startupTimer.Tick += (s2, e2) =>
                    {
                        startupTimer.Stop();
                        startupTimer.Dispose();
                        CheckForUpdates(true);
                    };
                    startupTimer.Start();
                }
            };

            // ===== Статус =====
            _status = new StatusStrip
            {
                BackColor = Theme.Panel,
                ForeColor = Theme.TextDim,
                SizingGrip = false
            };
            _lblStatus = new ToolStripStatusLabel("Готово") { ForeColor = Theme.TextDim };
            _status.Items.Add(_lblStatus);
            this.Controls.Add(_status);

            // Важно: Fill-панель должна доковаться последней, иначе верхняя
            // панель перекрывает её содержимое.
            split.BringToFront();
        }

        private static Image LoadLogo()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream("IPTVViewer.Resources.logo.png"))
                {
                    if (stream != null)
                        return Image.FromStream(stream);
                }
            }
            catch { }
            return null;
        }

        private void ChannelList_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _lbChannels.Items.Count)
                return;
            var ch = _lbChannels.Items[e.Index] as Channel;
            if (ch == null) return;

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool hover = e.Index == _hoverIndex;
            var bg = selected ? Theme.Selection : (hover ? Theme.PanelHover : Theme.Panel);

            using (var b = new SolidBrush(bg))
                e.Graphics.FillRectangle(b, e.Bounds);

            if (selected)
            {
                using (var accent = new SolidBrush(Theme.Accent))
                    e.Graphics.FillRectangle(accent, new Rectangle(e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height));
            }

            var nameRect = new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 6, e.Bounds.Width - 24, 20);
            var groupRect = new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 25, e.Bounds.Width - 24, 16);
            TextRenderer.DrawText(e.Graphics, ch.Name ?? "", Theme.FontBold, nameRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (!string.IsNullOrWhiteSpace(ch.Group))
                TextRenderer.DrawText(e.Graphics, ch.Group, Theme.FontSmall, groupRect, Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            using (var pen = new Pen(Theme.Border))
                e.Graphics.DrawLine(pen, e.Bounds.X + 12, e.Bounds.Bottom - 1, e.Bounds.Right - 12, e.Bounds.Bottom - 1);
        }

        private void ApplySettings()
        {
            _cbSource.SelectedIndex = ClampIndex(_settings.SelectedSourceIndex, _cbSource.Items.Count);
        }

        private void ApplyFilter(string text)
        {
            _lbChannels.BeginUpdate();
            _lbChannels.Items.Clear();
            var q = (text ?? "").Trim().ToLowerInvariant();
            foreach (var ch in _allChannels)
            {
                if (q.Length == 0 ||
                    (ch.Name ?? "").ToLowerInvariant().Contains(q) ||
                    (ch.Group ?? "").ToLowerInvariant().Contains(q))
                {
                    _lbChannels.Items.Add(ch);
                }
            }
            _lbChannels.EndUpdate();
            SetStatus($"Каналов: {_lbChannels.Items.Count} из {_allChannels.Count}");
        }

        private void LoadSelectedSource()
        {
            var url = GetSelectedUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                SetStatus("Не указан URL плейлиста");
                return;
            }
            SetStatus("Загрузка плейлиста...");
            _btnLoad.Enabled = false;

            Task.Run(() =>
            {
                try
                {
                    var channels = PlaylistLoader.Load(url);
                    this.Invoke(new Action(() =>
                    {
                        _allChannels = channels;
                        if (!_searchPlaceholderActive && !string.IsNullOrEmpty(_txtSearch.Text))
                            ApplyFilter(_txtSearch.Text);
                        else
                            ApplyFilter("");
                    }));
                }
                catch (Exception ex)
                {
                    this.Invoke(new Action(() =>
                    {
                        SetStatus("Ошибка загрузки: " + ex.Message);
                        MessageBox.Show(this, "Не удалось загрузить плейлист:\n" + ex.Message,
                            "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                finally
                {
                    this.Invoke(new Action(() => _btnLoad.Enabled = true));
                }
            });
        }

        private void PlaySelected()
        {
            var ch = _lbChannels.SelectedItem as Channel;
            if (ch == null)
            {
                SetStatus("Выберите канал в списке");
                return;
            }
            PlayUrl(ch.Url, ch.Name);
        }

        private void PlaySelectedExternally()
        {
            var ch = _lbChannels.SelectedItem as Channel;
            if (ch == null)
            {
                MessageBox.Show(this, "Выберите канал в списке.", "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Process.Start(ch.Url);
                SetStatus("Открыто во внешнем проигрывателе: " + ch.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось открыть поток:\n" + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PlayUrl(string url, string name)
        {
            if (_mediaPlayer == null)
            {
                MessageBox.Show(this, "VLC не инициализирован. Проверьте наличие libvlc.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            try
            {
                StopPlayback();
                _currentMedia = new Media(_libVLC, new Uri(url));
                _mediaPlayer.Volume = _volume.Value;
                _mediaPlayer.Play(_currentMedia);
                SetStatus("Воспроизведение: " + name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось запустить воспроизведение:\n" + ex.Message,
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void StopPlayback()
        {
            try
            {
                if (_mediaPlayer != null)
                    _mediaPlayer.Stop();
                if (_currentMedia != null)
                {
                    _currentMedia.Dispose();
                    _currentMedia = null;
                }
            }
            catch { }
            SetStatus("Остановлено");
        }

        private void OpenSettings()
        {
            var dlg = new SettingsForm(_settings);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                SettingsManager.Save(_settings);
                ApplySettings();
            }
        }

        private string GetSelectedUrl()
        {
            if (_settings.SourceUrls != null && _settings.SourceUrls.Count > _cbSource.SelectedIndex)
                return _settings.SourceUrls[_cbSource.SelectedIndex];
            return null;
        }

        private static int ClampIndex(int value, int count)
        {
            if (count <= 0) return 0;
            if (value < 0) return 0;
            if (value >= count) return count - 1;
            return value;
        }

        private void CheckForUpdates(bool silent)
        {
            if (_updateCheckRunning) return;
            _updateCheckRunning = true;
            if (_btnUpdate != null) _btnUpdate.Enabled = false;
            if (!silent) SetStatus("Проверка обновлений на GitHub...");

            var repo = _settings.UpdateRepo;

            Task.Run(() =>
            {
                UpdateInfo info;
                try
                {
                    info = UpdateChecker.Check(repo);
                }
                catch (Exception ex)
                {
                    info = new UpdateInfo
                    {
                        CurrentVersion = UpdateChecker.CurrentVersion,
                        Error = "Не удалось проверить обновления: " + ex.Message
                    };
                }

                if (this.IsDisposed) return;
                try
                {
                    this.BeginInvoke(new Action(() => OnUpdateChecked(info, silent)));
                }
                catch { }
            });
        }

        private void OnUpdateChecked(UpdateInfo info, bool silent)
        {
            _updateCheckRunning = false;
            if (_btnUpdate != null) _btnUpdate.Enabled = true;

            if (!string.IsNullOrEmpty(info.Error))
            {
                SetStatus("Обновления: " + info.Error.Split('\n')[0]);
                if (!silent)
                {
                    MessageBox.Show(this, info.Error, "Проверка обновлений",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            if (info.HasUpdate)
            {
                SetStatus("Доступна новая версия " + info.LatestVersion);
                using (var dlg = new UpdateForm(info))
                    dlg.ShowDialog(this);
                SetStatus("Готово");
            }
            else
            {
                SetStatus("Установлена последняя версия (" + info.CurrentVersion + ")");
                if (!silent)
                {
                    MessageBox.Show(this, "У вас уже последняя версия: " + info.CurrentVersion,
                        "Обновления", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void SetStatus(string text)
        {
            if (_lblStatus != null)
                _lblStatus.Text = text;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            try
            {
                _settings.WindowX = this.Location.X;
                _settings.WindowY = this.Location.Y;
                _settings.WindowW = this.ClientSize.Width;
                _settings.WindowH = this.ClientSize.Height;
                _settings.SelectedSourceIndex = _cbSource.SelectedIndex;
                SettingsManager.Save(_settings);
            }
            catch { }
            try
            {
                if (_currentMedia != null) { _currentMedia.Dispose(); _currentMedia = null; }
                if (_mediaPlayer != null) { _mediaPlayer.Stop(); _mediaPlayer.Dispose(); }
                if (_libVLC != null) _libVLC.Dispose();
            }
            catch { }
        }
    }
}
