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
        private const int ChannelPanelWidth = 360;

        private readonly AppSettings _settings;
        private readonly bool _openSettingsOnStart;
        private List<Channel> _allChannels = new List<Channel>();
        private EpgData _epg;
        private string _epgUrl;
        private bool _epgLoading;
        private int _epgRequest;

        private LibVLC _libVLC;
        private MediaPlayer _mediaPlayer;
        private Media _currentMedia;
        private VideoView _videoView;

        private Panel _topPanel;
        private SplitContainer _split;
        private Panel _channelPanel;
        private Panel _searchWrap;
        private Panel _listWrap;
        private Panel _videoWrap;
        private Panel _controlsPanel;
        private Panel _fsOverlay;
        private StatusStrip _status;

        private TextBox _txtSearch;
        private ListBox _lbChannels;
        private Button _btnSourcePortal;
        private Button _btnSourceIsp;
        private Button _btnPlay;
        private Button _btnStop;
        private Button _btnSettings;
        private Button _btnFullscreen;
        private TrackBar _volume;
        private ToolStripStatusLabel _lblStatus;

        private Timer _startupTimer;
        private Timer _settingsTimer;

        private int _hoverIndex = -1;
        private bool _searchPlaceholderActive = true;
        private bool _updateCheckRunning;
        private bool _uiBuilt;

        private bool _fullscreen;
        private FormBorderStyle _prevBorder;
        private FormWindowState _prevState;
        private Rectangle _prevBounds;
        private Timer _fsTimer;
        private Point _lastCursor;
        private int _idleTicks;
        private Timer _epgTimer;

        public MainForm()
        {
            _settings = SettingsManager.Load();
            Theme.SetMode(_settings.LightTheme);
            _openSettingsOnStart = Array.IndexOf(Environment.GetCommandLineArgs(), "--settings") >= 0;
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
            this.KeyPreview = true;
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
            _topPanel = new Panel { Dock = DockStyle.Top, Height = 120, BackColor = Theme.Panel };

            var badge = new RoundedPanel
            {
                Location = new Point(16, 14),
                Size = new Size(176, 58),
                Radius = 10,
                FillColor = Color.White,
                BorderColor = Theme.Border,
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
            _topPanel.Controls.Add(badge);

            var title = Theme.CreateLabel("IPTV Viewer", false, 17f, true);
            title.Location = new Point(208, 16);
            _topPanel.Controls.Add(title);

            var subtitle = Theme.CreateLabel("NEWLINE network  ·  VLC engine  ·  EPG", true, 9f, false);
            subtitle.Location = new Point(210, 46);
            _topPanel.Controls.Add(subtitle);

            _btnSettings = Theme.CreateButton("Настройки", false);
            _btnSettings.Size = new Size(110, 34);
            _btnSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnSettings.Click += (s, e) => OpenSettings();
            _topPanel.Controls.Add(_btnSettings);

            Action layoutTopButtons = () =>
            {
                _btnSettings.Left = _topPanel.Width - _btnSettings.Width - 16;
            };
            _topPanel.Resize += (s, e) => layoutTopButtons();
            layoutTopButtons();

            var lblSource = Theme.CreateLabel("Источник:", true, 9.5f, false);
            lblSource.Location = new Point(18, 87);
            _topPanel.Controls.Add(lblSource);

            _btnSourcePortal = CreateSourceButton("IPTVPORTAL", 0);
            _btnSourcePortal.Location = new Point(94, 80);
            _topPanel.Controls.Add(_btnSourcePortal);

            _btnSourceIsp = CreateSourceButton("IPTV", 1);
            _btnSourceIsp.Location = new Point(244, 80);
            _topPanel.Controls.Add(_btnSourceIsp);

            var sourceTips = new ToolTip();
            sourceTips.SetToolTip(_btnSourcePortal, "Источник: newline.iptvportal.ru");
            sourceTips.SetToolTip(_btnSourceIsp, "Источник: tv.isp-nl.ru/nltv.m3u");

            this.Controls.Add(_topPanel);

            // ===== Контент: слева видео, справа каналы =====
            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Border,
                SplitterWidth = 2
            };

            var videoHost = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Theme.Bg
            };
            videoHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            videoHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

            _videoWrap = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Padding = new Padding(0) };
            if (_mediaPlayer != null)
            {
                _videoView = new VideoView
                {
                    Dock = DockStyle.Fill,
                    MediaPlayer = _mediaPlayer,
                    BackColor = Color.Black
                };
                _videoView.DoubleClick += (s, e) => ToggleFullscreen();
                _videoWrap.Controls.Add(_videoView);
            }
            _videoWrap.DoubleClick += (s, e) => ToggleFullscreen();
            videoHost.Controls.Add(_videoWrap, 0, 0);

            _controlsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(12, 8, 12, 8)
            };

            _btnPlay = Theme.CreateButton("▶  Воспроизвести", true);
            _btnPlay.Location = new Point(12, 9);
            _btnPlay.Size = new Size(140, 34);
            _btnPlay.Click += (s, e) => PlaySelected();

            _btnStop = Theme.CreateButton("■  Стоп", false);
            _btnStop.Location = new Point(160, 9);
            _btnStop.Size = new Size(86, 34);
            _btnStop.Click += (s, e) => StopPlayback();

            _btnFullscreen = Theme.CreateButton("На весь экран", false);
            _btnFullscreen.Location = new Point(254, 9);
            _btnFullscreen.Size = new Size(128, 34);
            _btnFullscreen.Click += (s, e) => ToggleFullscreen();

            _volume = new TrackBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 80,
                TickStyle = TickStyle.None,
                Location = new Point(400, 10),
                Size = new Size(120, 30),
                BackColor = Theme.Panel,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _volume.Scroll += (s, e) => { if (_mediaPlayer != null) _mediaPlayer.Volume = _volume.Value; };

            var tips = new ToolTip();
            tips.SetToolTip(_btnFullscreen, "Полный экран (F11), выход — Esc");
            tips.SetToolTip(_volume, "Громкость");
            tips.SetToolTip(_btnStop, "Остановить воспроизведение");

            _controlsPanel.Controls.Add(_btnPlay);
            _controlsPanel.Controls.Add(_btnStop);
            _controlsPanel.Controls.Add(_btnFullscreen);
            _controlsPanel.Controls.Add(_volume);
            _controlsPanel.Resize += (s, e) => _volume.Left = _controlsPanel.Width - _volume.Width - 16;
            videoHost.Controls.Add(_controlsPanel, 0, 1);

            _split.Panel1.Controls.Add(videoHost);

            // ===== Панель каналов (справа) =====
            _channelPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel
            };

            _searchWrap = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(300, 52),
                Padding = new Padding(12, 10, 12, 6),
                BackColor = Theme.Panel
            };
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
            _searchWrap.Controls.Add(_txtSearch);
            _channelPanel.Controls.Add(_searchWrap);

            _lbChannels = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 50,
                IntegralHeight = false,
                Font = Theme.FontRegular
            };
            _lbChannels.DrawItem += ChannelList_DrawItem;
            _lbChannels.MouseMove += ChannelList_MouseMove;
            _lbChannels.MouseLeave += (s, e) => { _hoverIndex = -1; _lbChannels.Invalidate(); };
            _lbChannels.DoubleClick += (s, e) => PlaySelected();
            _lbChannels.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                int idx = _lbChannels.IndexFromPoint(e.Location);
                if (idx < 0 || idx >= _lbChannels.Items.Count) return;
                _lbChannels.SelectedIndex = idx;
                ShowEpgForSelected();
            };
            _lbChannels.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { PlaySelected(); e.Handled = true; }
            };

            _listWrap = new Panel { Location = new Point(0, 52), Size = new Size(300, 300), BackColor = Theme.Panel };
            _listWrap.Controls.Add(_lbChannels);
            _channelPanel.Controls.Add(_listWrap);

            _channelPanel.Resize += (s, e) => LayoutChannelPanel();
            LayoutChannelPanel();

            _split.Panel2.Controls.Add(_channelPanel);

            this.Controls.Add(_split);

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

            // ===== Оверлей каналов для полного экрана =====
            _fsOverlay = new Panel
            {
                BackColor = Theme.Panel,
                Visible = false
            };
            this.Controls.Add(_fsOverlay);

            if (_fsTimer == null)
            {
                _fsTimer = new Timer { Interval = 250 };
                _fsTimer.Tick += FsTimer_Tick;
            }

            this.Resize += (s, e) =>
            {
                if (_fullscreen)
                    LayoutFullscreenOverlay();
            };
            this.Activated += (s, e) =>
            {
                if (_fullscreen)
                    ShowFullscreenOverlay();
            };

            if (!_uiBuilt)
            {
                _uiBuilt = true;
                this.Shown += OnFormShown;
            }

            // Fill-панель должна быть поверх остальных.
            _split.BringToFront();
        }

        private void LayoutChannelPanel()
        {
            if (_channelPanel == null) return;
            int w = Math.Max(0, _channelPanel.ClientSize.Width);
            int h = Math.Max(0, _channelPanel.ClientSize.Height);
            _searchWrap.Location = new Point(0, 0);
            _searchWrap.Size = new Size(w, 52);
            _listWrap.Location = new Point(0, 52);
            _listWrap.Size = new Size(w, Math.Max(0, h - 52));
        }

        private void LayoutFullscreenOverlay()
        {
            if (_fsOverlay == null) return;
            _fsOverlay.Bounds = new Rectangle(
                Math.Max(0, ClientSize.Width - ChannelPanelWidth), 0,
                ChannelPanelWidth, ClientSize.Height);
        }

        private void OnFormShown(object sender, EventArgs e)
        {
            try { ApplySplitLayout(); }
            catch { }

            if (_settings.CheckUpdatesOnStartup)
            {
                _startupTimer = new Timer { Interval = 3000 };
                _startupTimer.Tick += (s2, e2) =>
                {
                    _startupTimer.Stop();
                    _startupTimer.Dispose();
                    _startupTimer = null;
                    CheckForUpdates(true);
                };
                _startupTimer.Start();
            }

            if (_openSettingsOnStart)
            {
                _settingsTimer = new Timer { Interval = 500 };
                _settingsTimer.Tick += (s3, e3) =>
                {
                    _settingsTimer.Stop();
                    _settingsTimer.Dispose();
                    _settingsTimer = null;
                    OpenSettings();
                };
                _settingsTimer.Start();
            }
        }

        private void ApplySplitLayout()
        {
            if (_split == null) return;
            _split.Panel1MinSize = 320;
            _split.Panel2MinSize = 280;
            _split.FixedPanel = FixedPanel.Panel2;

            int distance = _split.Width - ChannelPanelWidth - _split.SplitterWidth;
            if (distance > 0)
                _split.SplitterDistance = distance;
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

        // ===== Полный экран =====

        private void ToggleFullscreen()
        {
            if (_fullscreen)
                ExitFullscreen();
            else
                EnterFullscreen();
        }

        private void EnterFullscreen()
        {
            if (_fullscreen) return;

            _prevBorder = FormBorderStyle;
            _prevState = WindowState;
            _prevBounds = Bounds;

            if (WindowState == FormWindowState.Maximized)
                WindowState = FormWindowState.Normal;

            _topPanel.Visible = false;
            _status.Visible = false;
            _controlsPanel.Visible = false;
            _split.Panel2Collapsed = true;

            AttachChannelPanelToOverlay();
            _fsOverlay.Visible = true;
            _fullscreen = true;
            LayoutFullscreenOverlay();
            _fsOverlay.BringToFront();

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;

            _btnFullscreen.Text = "Выйти (Esc)";
            _lastCursor = Cursor.Position;
            _idleTicks = 0;
            _fsTimer.Start();
        }

        private void ExitFullscreen()
        {
            if (!_fullscreen) return;
            _fullscreen = false;
            _fsTimer.Stop();
            _fsOverlay.Visible = false;

            AttachChannelPanelToSplit();
            _split.Panel2Collapsed = false;

            FormBorderStyle = _prevBorder;
            WindowState = FormWindowState.Normal;
            Bounds = _prevBounds;
            if (_prevState == FormWindowState.Maximized)
                WindowState = FormWindowState.Maximized;

            _topPanel.Visible = true;
            _status.Visible = true;
            _controlsPanel.Visible = true;
            _btnFullscreen.Text = "На весь экран";

            BeginInvoke(new Action(() =>
            {
                try { ApplySplitLayout(); }
                catch { }
            }));
        }

        private void AttachChannelPanelToOverlay()
        {
            if (_channelPanel == null || _fsOverlay == null) return;
            if (_channelPanel.Parent == _fsOverlay) return;
            if (_channelPanel.Parent != null)
                _channelPanel.Parent.Controls.Remove(_channelPanel);
            _fsOverlay.Controls.Add(_channelPanel);
            _channelPanel.Dock = DockStyle.Fill;
        }

        private void AttachChannelPanelToSplit()
        {
            if (_channelPanel == null || _split == null) return;
            if (_channelPanel.Parent == _split.Panel2) return;
            if (_channelPanel.Parent != null)
                _channelPanel.Parent.Controls.Remove(_channelPanel);
            _split.Panel2.Controls.Add(_channelPanel);
            _channelPanel.Dock = DockStyle.Fill;
        }

        private void ShowFullscreenOverlay()
        {
            if (!_fullscreen || _fsOverlay == null) return;
            _fsOverlay.Visible = true;
            _fsOverlay.BringToFront();
            _idleTicks = 0;
        }

        private void FsTimer_Tick(object sender, EventArgs e)
        {
            if (!_fullscreen) return;

            var pos = Cursor.Position;
            if (pos != _lastCursor)
            {
                _lastCursor = pos;
                ShowFullscreenOverlay();
                return;
            }

            if (!_fsOverlay.Visible) return;

            var local = _fsOverlay.PointToClient(pos);
            if (_fsOverlay.ClientRectangle.Contains(local))
            {
                _idleTicks = 0;
                return;
            }

            _idleTicks++;
            if (_idleTicks >= 12)
            {
                _fsOverlay.Visible = false;
                _idleTicks = 0;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape && _fullscreen)
            {
                ExitFullscreen();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.F && _txtSearch != null)
            {
                _txtSearch.Focus();
                e.Handled = true;
            }
        }

        // ===== Список каналов =====

        private void ChannelList_MouseMove(object sender, MouseEventArgs e)
        {
            int idx = _lbChannels.IndexFromPoint(e.Location);
            if (idx == _hoverIndex) return;

            _hoverIndex = idx;
            _lbChannels.Invalidate();

            if (idx >= 0 && idx < _lbChannels.Items.Count)
                UpdateStatusForChannel(_lbChannels.Items[idx] as Channel);
        }

        private void UpdateStatusForChannel(Channel ch)
        {
            if (ch == null) return;
            var current = _epg != null ? _epg.GetCurrent(ch, DateTime.Now) : null;
            if (current != null)
                SetStatus(ch.Name + " — " + current.Title + " (" + current.TimeRange + ")");
            else
                SetStatus(ch.Name);
        }

        private void ShowEpgForSelected()
        {
            var ch = _lbChannels.SelectedItem as Channel;
            if (ch == null) return;

            if (_epgLoading)
            {
                MessageBox.Show(this,
                    "Телепрограмма ещё загружается.\n\n" +
                    "Данные появятся в списке автоматически (обычно около минуты, включая скачивание архива).",
                    "Телепрограмма", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_epg == null || _epg.IsEmpty)
            {
                MessageBox.Show(this,
                    "Телепрограмма для этого канала ещё не загружена.\n\n" +
                    "Включите загрузку EPG в настройках и загрузите плейлист.",
                    "Телепрограмма", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new EpgForm(ch, _epg))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    PlaySelected();
            }
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
            var secondRect = new Rectangle(e.Bounds.X + 14, e.Bounds.Y + 26, e.Bounds.Width - 24, 17);

            TextRenderer.DrawText(e.Graphics, ch.Name ?? "", Theme.FontBold, nameRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            string second = ch.Group;
            Color secondColor = Theme.TextDim;
            bool showProgram = false;

            if ((hover || selected) && _epg != null)
            {
                var current = _epg.GetCurrent(ch, DateTime.Now);
                if (current != null)
                {
                    second = "Сейчас: " + current.Title + "  " + current.TimeRange;
                    secondColor = Theme.Accent;
                    showProgram = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(second))
            {
                TextRenderer.DrawText(e.Graphics, second, showProgram ? Theme.FontSmall : Theme.FontSmall,
                    secondRect, secondColor,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            using (var pen = new Pen(Theme.Border))
                e.Graphics.DrawLine(pen, e.Bounds.X + 12, e.Bounds.Bottom - 1, e.Bounds.Right - 12, e.Bounds.Bottom - 1);
        }

        // ===== Настройки и данные =====

        private void ApplySettings()
        {
            UpdateSourceButtons();
        }

        private Button CreateSourceButton(string text, int index)
        {
            var b = Theme.CreateButton(text, false);
            b.Size = new Size(140, 34);
            b.Click += (s, e) => SelectSource(index, true);
            return b;
        }

        private void SelectSource(int index, bool load)
        {
            int count = _settings.SourceUrls != null ? _settings.SourceUrls.Count : 2;
            if (count <= 0) count = 2;
            _settings.SelectedSourceIndex = ClampIndex(index, count);
            UpdateSourceButtons();
            if (load)
                LoadSelectedSource();
        }

        private void UpdateSourceButtons()
        {
            StyleSourceButton(_btnSourcePortal, _settings.SelectedSourceIndex == 0);
            StyleSourceButton(_btnSourceIsp, _settings.SelectedSourceIndex == 1);
        }

        private static void StyleSourceButton(Button b, bool active)
        {
            if (b == null) return;
            b.BackColor = active ? Theme.Accent : Theme.PanelAlt;
            b.ForeColor = active ? Color.White : Theme.Text;
            b.FlatAppearance.BorderSize = active ? 0 : 1;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.MouseOverBackColor = active ? Theme.AccentHover : Theme.PanelHover;
        }

        private void SetSourceButtonsEnabled(bool enabled)
        {
            if (_btnSourcePortal != null) _btnSourcePortal.Enabled = enabled;
            if (_btnSourceIsp != null) _btnSourceIsp.Enabled = enabled;
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
            SetSourceButtonsEnabled(false);

            Task.Run(() =>
            {
                try
                {
                    var result = PlaylistLoader.Load(url);
                    if (this.IsDisposed) return;
                    this.BeginInvoke(new Action(() => OnPlaylistLoaded(result)));
                }
                catch (Exception ex)
                {
                    if (this.IsDisposed) return;
                    this.BeginInvoke(new Action(() =>
                    {
                        SetStatus("Ошибка загрузки: " + ex.Message);
                        MessageBox.Show(this, "Не удалось загрузить плейлист:\n" + ex.Message,
                            "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
                finally
                {
                    if (!this.IsDisposed)
                        this.BeginInvoke(new Action(() => SetSourceButtonsEnabled(true)));
                }
            });
        }

        private void OnPlaylistLoaded(PlaylistResult result)
        {
            _allChannels = result.Channels;
            _epg = null;
            _epgLoading = false;
            _epgRequest++;
            _epgUrl = !string.IsNullOrWhiteSpace(_settings.EpgUrl)
                ? _settings.EpgUrl.Trim()
                : result.EpgUrl;

            if (!_searchPlaceholderActive && !string.IsNullOrEmpty(_txtSearch.Text))
                ApplyFilter(_txtSearch.Text);
            else
                ApplyFilter("");

            if (_settings.EpgEnabled && !string.IsNullOrWhiteSpace(_epgUrl) && _allChannels.Count > 0)
                LoadEpgAsync(_epgUrl, _allChannels);
        }

        private void LoadEpgAsync(string epgUrl, List<Channel> channels)
        {
            _epgLoading = true;
            int request = ++_epgRequest;
            SetStatus("Загрузка телепрограммы (EPG)…");

            Task.Run(() =>
            {
                var data = EpgLoader.Load(epgUrl, channels);
                if (this.IsDisposed || request != _epgRequest) return;
                try
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        if (request != _epgRequest) return;
                        _epgLoading = false;
                        _epg = data;
                        _lbChannels.Invalidate();
                        SetStatus(data.IsEmpty
                            ? "EPG: данные не найдены"
                            : "EPG загружена — ПКМ по каналу откроет телепрограмму");

                        if (_epgTimer == null)
                        {
                            _epgTimer = new Timer { Interval = 60000 };
                            _epgTimer.Tick += (s, e) => _lbChannels.Invalidate();
                            _epgTimer.Start();
                        }
                    }));
                }
                catch { }
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

            if (_settings.UseExternalPlayer)
            {
                PlaySelectedExternally();
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
            bool lightBefore = _settings.LightTheme;
            bool epgBefore = _settings.EpgEnabled;

            SettingsForm dlg = null;
            dlg = new SettingsForm(_settings, () => CheckForUpdates(false, dlg));
            using (dlg)
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;

                SettingsManager.Save(_settings);
                ApplySettings();

                if (_settings.LightTheme != lightBefore)
                {
                    RebuildUi();
                }

                if (_settings.EpgEnabled && !epgBefore && _epg == null &&
                    !string.IsNullOrWhiteSpace(_epgUrl) && _allChannels.Count > 0)
                {
                    LoadEpgAsync(_epgUrl, _allChannels);
                }
            }
        }

        private void RebuildUi()
        {
            if (_fullscreen)
                ExitFullscreen();

            Theme.SetMode(_settings.LightTheme);
            this.BackColor = Theme.Bg;
            this.ForeColor = Theme.Text;

            SuspendLayout();
            var old = new List<Control>();
            foreach (Control c in Controls)
                old.Add(c);
            Controls.Clear();
            foreach (var c in old)
            {
                try { c.Dispose(); }
                catch { }
            }

            _hoverIndex = -1;
            _searchPlaceholderActive = true;
            BuildUi();
            ApplySettings();
            ApplyFilter("");
            ResumeLayout(true);

            BeginInvoke(new Action(() =>
            {
                try { ApplySplitLayout(); }
                catch { }
            }));
        }

        private string GetSelectedUrl()
        {
            if (_settings.SourceUrls != null && _settings.SourceUrls.Count > _settings.SelectedSourceIndex)
                return _settings.SourceUrls[_settings.SelectedSourceIndex];
            return null;
        }

        private static int ClampIndex(int value, int count)
        {
            if (count <= 0) return 0;
            if (value < 0) return 0;
            if (value >= count) return count - 1;
            return value;
        }

        private void SetStatus(string text)
        {
            if (_lblStatus != null)
                _lblStatus.Text = text;
        }

        private void CheckForUpdates(bool silent)
        {
            CheckForUpdates(silent, this);
        }

        private void CheckForUpdates(bool silent, IWin32Window owner)
        {
            if (_updateCheckRunning)
            {
                if (!silent)
                {
                    MessageBox.Show(owner ?? (IWin32Window)this,
                        "Проверка обновлений уже выполняется.",
                        "Обновления", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }
            _updateCheckRunning = true;
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
                    this.BeginInvoke(new Action(() => OnUpdateChecked(info, silent, owner)));
                }
                catch { }
            });
        }

        private void OnUpdateChecked(UpdateInfo info, bool silent, IWin32Window owner)
        {
            _updateCheckRunning = false;

            var ownerForm = owner as Form;
            if (ownerForm == null || ownerForm.IsDisposed)
                owner = this;

            if (!string.IsNullOrEmpty(info.Error))
            {
                SetStatus("Обновления: " + info.Error.Split('\n')[0]);
                if (!silent)
                {
                    MessageBox.Show(owner, info.Error, "Проверка обновлений",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            if (info.HasUpdate)
            {
                SetStatus("Доступна новая версия " + info.LatestVersion);
                using (var dlg = new UpdateForm(info))
                    dlg.ShowDialog(owner);
                SetStatus("Готово");
            }
            else
            {
                SetStatus("Используется актуальная версия (" + info.CurrentVersion + ")");
                if (!silent)
                {
                    MessageBox.Show(owner, "Используется актуальная версия: " + info.CurrentVersion,
                        "Обновления", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            if (_fullscreen)
            {
                _fullscreen = false;
                _fsTimer.Stop();
            }

            try
            {
                _settings.WindowX = this.Location.X;
                _settings.WindowY = this.Location.Y;
                _settings.WindowW = this.ClientSize.Width;
                _settings.WindowH = this.ClientSize.Height;
                SettingsManager.Save(_settings);
            }
            catch { }

            try
            {
                if (_epgTimer != null) { _epgTimer.Stop(); _epgTimer.Dispose(); }
                if (_fsTimer != null) { _fsTimer.Stop(); _fsTimer.Dispose(); }
                if (_currentMedia != null) { _currentMedia.Dispose(); _currentMedia = null; }
                if (_mediaPlayer != null) { _mediaPlayer.Stop(); _mediaPlayer.Dispose(); }
                if (_libVLC != null) _libVLC.Dispose();
            }
            catch { }
        }
    }
}
