using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;

namespace IPTVViewer
{
    public partial class MainForm : Form
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (_mouseHook != null)
                    {
                        _mouseHook.Dispose();
                        _mouseHook = null;
                    }
                }
                catch { }
                try
                {
                    if (_volumeBadgeTimer != null)
                    {
                        _volumeBadgeTimer.Stop();
                        _volumeBadgeTimer.Dispose();
                        _volumeBadgeTimer = null;
                    }
                }
                catch { }
            }
            base.Dispose(disposing);
        }
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
        private Panel _catWrap;
        private Panel _catScroll;
        private Button _btnCatLeft;
        private Button _btnCatRight;
        private readonly List<Button> _catButtons = new List<Button>();
        private string _selectedGroup = "";
        private int _catScrollX;
        private int _catContentWidth;
        private bool _categoriesShown;
        private Panel _listWrap;
        private readonly Dictionary<string, Image> _logoCache = new Dictionary<string, Image>();
        private readonly object _cacheLock = new object();
        private int _logoGeneration;
        private static readonly Font LogoLetterFont = new Font(Theme.FontSmall.FontFamily, 10f, FontStyle.Bold);
        private Panel _videoWrap;
        private Panel _videoIdle;
        private Image _appLogo;
        private TableLayoutPanel _videoHost;
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
        private Button _btnOnTop;
        private Button _btnToggleChannels;
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
        private bool _channelsHidden;
        private FormBorderStyle _prevBorder;
        private FormWindowState _prevState;
        private Rectangle _prevBounds;
        private Timer _fsTimer;
        private Point _lastCursor;
        private int _idleTicks;
        private Timer _epgTimer;
        private LowLevelMouseHook _mouseHook;
        private Label _volumeBadge;
        private Timer _volumeBadgeTimer;
        private bool _autoPlayOnLoad;

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
            {
                _autoPlayOnLoad = true;
                LoadSelectedSource();
            }

            InstallMouseHook();
        }

        private void InstallMouseHook()
        {
            try
            {
                if (_mouseHook != null) return;
                _mouseHook = new LowLevelMouseHook();
                _mouseHook.LeftDoubleClicked += pt => this.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_videoView == null || !_videoView.Visible) return;
                        var playArea = _videoView.RectangleToScreen(
                            new Rectangle(0, 0, _videoView.Width, _videoView.Height));
                        if (_fullscreen ? true : playArea.Contains(pt))
                            ToggleFullscreen();
                    }
                    catch { }
                }));
                _mouseHook.WheelHandler = OnMouseWheelHook;
            }
            catch { }
        }

        /// <summary>Колесо мыши: над списком каналов — прокрутка списка,
        /// над видео — громкость. Работает и в окне, и в полном экране.</summary>
        private bool OnMouseWheelHook(Point pt, int delta)
        {
            try
            {
                // реагируем только когда наше окно активно — не глотаем чужую прокрутку
                if (Form.ActiveForm != this) return false;

                // список каналов листается всегда, когда курсор над ним
                // (и в оконном режиме, и в полноэкранном оверлее)
                if (IsOverChannelList(pt))
                {
                    this.BeginInvoke(new Action(() => ScrollChannelList(delta)));
                    return true;
                }

                if (_videoView == null || !_videoView.Visible) return false;

                var playArea = _videoView.RectangleToScreen(
                    new Rectangle(0, 0, _videoView.Width, _videoView.Height));
                if (!_fullscreen && !playArea.Contains(pt)) return false;
                if (_fullscreen && !this.RectangleToScreen(this.ClientRectangle).Contains(pt)) return false;

                int step = 5; // один щелчок колеса = 5 %
                int amount = Math.Sign(delta) * step; // прокрутка «от себя» (+120) — громче
                this.BeginInvoke(new Action(() => ChangeVolume(amount)));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsOverChannelList(Point pt)
        {
            if (_lbChannels == null || !_lbChannels.Visible) return false;
            if (_channelPanel == null || !_channelPanel.Visible) return false;
            return _lbChannels.RectangleToScreen(_lbChannels.ClientRectangle).Contains(pt);
        }

        private void ScrollChannelList(int delta)
        {
            try
            {
                int lines = SystemInformation.MouseWheelScrollLines;
                if (lines <= 0) lines = 3; // 0 = «на страницу» — берём привычные 3 строки
                int step = Math.Max(1, lines);
                int target = delta > 0 ? _lbChannels.TopIndex - step
                                       : _lbChannels.TopIndex + step;
                int max = Math.Max(0, _lbChannels.Items.Count - 1);
                _lbChannels.TopIndex = Math.Max(0, Math.Min(max, target));
            }
            catch { }
        }

        private void ChangeVolume(int delta)
        {
            try
            {
                int current = _mediaPlayer != null ? _mediaPlayer.Volume : _volume.Value;
                int v = Math.Max(0, Math.Min(100, current + delta));
                if (_mediaPlayer != null && _mediaPlayer.Volume != v)
                    _mediaPlayer.Volume = v;
                if (_volume.Value != v)
                    _volume.Value = v;
                ShowVolumeBadge(v);
                SetStatus("Громкость: " + v + " %");
            }
            catch { }
        }

        private void ShowVolumeBadge(int percent)
        {
            try
            {
                if (_volumeBadge == null)
                {
                    _volumeBadge = new Label
                    {
                        Size = new Size(150, 44),
                        TextAlign = ContentAlignment.MiddleCenter,
                        Font = Theme.FontBold,
                        BackColor = Theme.PanelAlt,
                        ForeColor = Theme.AccentHover,
                        BorderStyle = BorderStyle.None,
                        Visible = false
                    };
                    this.Controls.Add(_volumeBadge);
                }

                _volumeBadge.Text = "Громкость " + percent + " %";

                // показываем по центру сверху над областью видео
                var r = _videoView.RectangleToScreen(
                    new Rectangle(0, 0, _videoView.Width, _videoView.Height));
                var local = this.PointToClient(new Point(
                    r.Left + (r.Width - _volumeBadge.Width) / 2,
                    r.Top + 24));
                _volumeBadge.Location = local;
                _volumeBadge.Visible = true;
                _volumeBadge.BringToFront();

                if (_volumeBadgeTimer == null)
                {
                    _volumeBadgeTimer = new Timer { Interval = 1200 };
                    _volumeBadgeTimer.Tick += (s, e) =>
                    {
                        try
                        {
                            _volumeBadgeTimer.Stop();
                            _volumeBadge.Visible = false;
                        }
                        catch { }
                    };
                }
                _volumeBadgeTimer.Stop();
                _volumeBadgeTimer.Start();
            }
            catch { }
        }

        private void InitializeComponent()
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            }
            catch { }

            this.Text = "IPTV Newline — NEWLINE network";
            this.Icon = AppIcon.Get();
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(_settings.WindowX, _settings.WindowY);
            // 970: кнопки источников (до x=590) + правая цепочка
            // (иконка настроек + 2 кнопки, 362 px) — уже нельзя
            this.MinimumSize = new Size(986, 520);
            this.ClientSize = new Size(
                Math.Max(_settings.WindowW, 970),
                Math.Max(_settings.WindowH, 520));
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
                Core.Initialize(VlcLocator.Ensure(SettingsManager.DataDir));
                _libVLC = new LibVLC();
                _mediaPlayer = new MediaPlayer(_libVLC);
                _mediaPlayer.Volume = 80;
                _mediaPlayer.EncounteredError += (s, e) =>
                {
                    SetStatus("VLC: ошибка воспроизведения потока");
                    ShowIdleIfStopped();
                };
                _mediaPlayer.EndReached += (s, e) => ShowIdleIfStopped();
                _mediaPlayer.Stopped += (s, e) => ShowIdleIfStopped();
                _mediaPlayer.Playing += (s, e) => HideIdleOnPlaying();
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
            _topPanel = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Theme.Panel };

            // логотип — прямо на панели, без рамки и подложки
            // (картинка с прозрачным фоном, цвет панели под ней родной)
            var logo = LoadLogo();
            if (logo != null)
            {
                var pic = new PictureBox
                {
                    Location = new Point(12, 14),
                    Size = new Size(120, 40),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Theme.Panel,
                    Image = logo
                };
                _topPanel.Controls.Add(pic);
            }

            var title = Theme.CreateLabel("IPTV Newline", false, 15f, true);
            title.Location = new Point(156, 22);
            _topPanel.Controls.Add(title);

            _btnSettings = CreateIconButton(CreateGearIcon(18, Theme.Accent));
            _btnSettings.Size = new Size(36, 34);
            _btnSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnSettings.Click += (s, e) => OpenSettings();
            _topPanel.Controls.Add(_btnSettings);

            _btnOnTop = Theme.CreateButton("Поверх всех окон", false);
            _btnOnTop.Size = new Size(160, 30);
            _btnOnTop.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnOnTop.Click += (s, e) => ToggleTopMost();
            StyleTopMostButton();
            _topPanel.Controls.Add(_btnOnTop);

            _btnToggleChannels = Theme.CreateButton("Скрыть список", false);
            _btnToggleChannels.Size = new Size(120, 30);
            _btnToggleChannels.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnToggleChannels.Click += (s, e) => ToggleChannelList();
            _topPanel.Controls.Add(_btnToggleChannels);

            Action layoutTopButtons = () =>
            {
                _btnSettings.Left = _topPanel.Width - _btnSettings.Width - 14;
                _btnOnTop.Left = _btnSettings.Left - _btnOnTop.Width - 8;
                _btnToggleChannels.Left = _btnOnTop.Left - _btnToggleChannels.Width - 8;
                _btnSettings.Top = (_topPanel.Height - _btnSettings.Height) / 2;
                _btnOnTop.Top = (_topPanel.Height - _btnOnTop.Height) / 2;
                _btnToggleChannels.Top = (_topPanel.Height - _btnToggleChannels.Height) / 2;
            };
            _topPanel.Resize += (s, e) => layoutTopButtons();
            layoutTopButtons();

            _btnSourcePortal = CreateSourceButton("IPTVPORTAL", 0);
            _btnSourcePortal.Location = new Point(300, 19);
            _topPanel.Controls.Add(_btnSourcePortal);

            _btnSourceIsp = CreateSourceButton("M3U", 1);
            _btnSourceIsp.Location = new Point(450, 19);
            _topPanel.Controls.Add(_btnSourceIsp);

            var sourceTips = new ToolTip
            {
                InitialDelay = 400,      // появляется быстро при наведении
                ReshowDelay = 200,       // и мгновенно при переходе на соседнюю кнопку
                AutoPopDelay = 8000
            };
            sourceTips.SetToolTip(_btnSourcePortal, "Источник: newline.iptvportal.ru");
            sourceTips.SetToolTip(_btnSourceIsp, "Источник: tv.isp-nl.ru/nltv.m3u");
            sourceTips.SetToolTip(_btnOnTop, "Держать окно поверх всех программ");
            sourceTips.SetToolTip(_btnSettings, "Настройки");
            sourceTips.SetToolTip(_btnToggleChannels,
                "Скрыть или показать список каналов (Ctrl+L)");

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
                BackColor = Theme.Bg,
                Padding = new Padding(0)
            };
            _videoHost = videoHost;
            videoHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            videoHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

            _videoWrap = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Padding = new Padding(0), Margin = new Padding(0) };
            if (_mediaPlayer != null)
            {
                // двойной клик по видео ловится глобальным хуком (InstallMouseHook),
                // окно libvlc перекрывает View и не пропускает клики к WinForms
                _videoView = new VideoView
                {
                    Dock = DockStyle.Fill,
                    MediaPlayer = _mediaPlayer,
                    BackColor = Color.Black
                };
                _videoWrap.Controls.Add(_videoView);
            }

            // Заставка вместо чёрного экрана: логотип, когда воспроизведение остановлено
            _appLogo = LoadLogo();
            _videoIdle = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            _videoIdle.Paint += VideoIdle_Paint;
            _videoWrap.Controls.Add(_videoIdle);
            _videoIdle.BringToFront();

            videoHost.Controls.Add(_videoWrap, 0, 0);

            _controlsPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(12, 8, 12, 8),
                Margin = new Padding(0)
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
            _volume.Scroll += (s, e) =>
            {
                if (_mediaPlayer != null) _mediaPlayer.Volume = _volume.Value;
                ShowVolumeBadge(_volume.Value);
            };

            var tips = new ToolTip
            {
                InitialDelay = 400,
                ReshowDelay = 200,
                AutoPopDelay = 8000
            };
            tips.SetToolTip(_btnPlay, "Воспроизвести выбранный канал");
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

            // ===== Категории каналов (полоса кнопок с прокруткой) =====
            _catWrap = new Panel
            {
                Location = new Point(0, 52),
                Size = new Size(300, 40),
                BackColor = Theme.Panel,
                Visible = false
            };

            _btnCatLeft = CreateCatArrow("‹");
            _btnCatLeft.Click += (s, e) => ScrollCategories(-140);
            _btnCatRight = CreateCatArrow("›");
            _btnCatRight.Click += (s, e) => ScrollCategories(140);

            _catScroll = new Panel
            {
                Location = new Point(32, 0),
                Size = new Size(236, 40),
                BackColor = Theme.Panel
            };

            _catWrap.Controls.Add(_catScroll);
            _catWrap.Controls.Add(_btnCatLeft);
            _catWrap.Controls.Add(_btnCatRight);
            _channelPanel.Controls.Add(_catWrap);

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
            int top = 52;

            _searchWrap.Location = new Point(0, 0);
            _searchWrap.Size = new Size(w, 52);

            if (_catWrap != null)
            {
                bool show = _categoriesShown;
                _catWrap.Location = new Point(0, 52);
                _catWrap.Size = new Size(w, show ? 40 : 0);

                _btnCatLeft.Location = new Point(6, 6);
                _btnCatLeft.Size = new Size(22, 28);
                _btnCatRight.Location = new Point(Math.Max(0, w - 28), 6);
                _btnCatRight.Size = new Size(22, 28);
                _catScroll.Location = new Point(32, 0);
                _catScroll.Size = new Size(Math.Max(0, w - 64), 40);

                if (show)
                {
                    PositionCategoryButtons();
                    UpdateCatArrows();
                    top = 92;
                }
            }

            _listWrap.Location = new Point(0, top);
            _listWrap.Size = new Size(w, Math.Max(0, h - top));
        }

        // ===== Категории каналов =====

        private Button CreateCatArrow(string text)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.PanelAlt,
                ForeColor = Theme.TextDim,
                Font = new Font(Theme.FontBold.FontFamily, 12f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TabStop = false,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.PanelHover;
            b.FlatAppearance.MouseDownBackColor = Theme.PanelHover;
            return b;
        }

        /// <summary>Строит кнопки категорий по группам загруженных каналов.</summary>
        private void RebuildCategories()
        {
            if (_catScroll == null) return;

            foreach (var b in _catButtons)
            {
                _catScroll.Controls.Remove(b);
                b.Dispose();
            }
            _catButtons.Clear();

            var groups = new List<KeyValuePair<string, int>>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in _allChannels)
            {
                if (ch == null || string.IsNullOrWhiteSpace(ch.Group)) continue;
                int n;
                if (seen.TryGetValue(ch.Group, out n)) seen[ch.Group] = n + 1;
                else seen[ch.Group] = 1;
            }
            foreach (var kv in seen)
                groups.Add(new KeyValuePair<string, int>(kv.Key, kv.Value));
            groups.Sort((a, b) => b.Value != a.Value
                ? b.Value.CompareTo(a.Value)
                : string.Compare(a.Key, b.Key, StringComparison.CurrentCultureIgnoreCase));

            bool show = groups.Count > 1;
            _categoriesShown = show;
            _catWrap.Visible = show;
            if (!show)
            {
                _selectedGroup = "";
                _catScrollX = 0;
                LayoutChannelPanel();
                return;
            }

            if (_selectedGroup.Length > 0 && !seen.ContainsKey(_selectedGroup))
                _selectedGroup = "";

            _catButtons.Add(CreateCategoryButton("Все", ""));
            foreach (var g in groups)
                _catButtons.Add(CreateCategoryButton(g.Key, g.Key));

            foreach (var b in _catButtons)
                _catScroll.Controls.Add(b);

            _catScrollX = 0;
            StyleCategoryButtons();
            LayoutChannelPanel();
        }

        private Button CreateCategoryButton(string title, string group)
        {
            var b = new Button
            {
                Text = title,
                Height = 28,
                Top = 6,
                FlatStyle = FlatStyle.Flat,
                Font = Theme.FontSmall,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                UseVisualStyleBackColor = false,
                Tag = group
            };
            int width = TextRenderer.MeasureText(title, Theme.FontSmall).Width + 24;
            b.Width = Math.Max(40, width);
            b.FlatAppearance.BorderSize = 0;
            b.Click += (s, e) => SelectCategory(group);
            return b;
        }

        private void SelectCategory(string group)
        {
            _selectedGroup = group ?? "";
            StyleCategoryButtons();
            ApplyFilter(_searchPlaceholderActive ? "" : (_txtSearch.Text ?? ""));
        }

        /// <summary>
        /// Если источник не даёт категорий (портал — плоский список), берём
        /// группы из другого настроенного плейлиста и сопоставляем по названию.
        /// </summary>
        private void TryFillGroupsFromOtherSource()
        {
            if (_allChannels == null || _allChannels.Count == 0) return;

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in _allChannels)
                if (!string.IsNullOrWhiteSpace(ch.Group)) known.Add(ch.Group);
            if (known.Count > 1) return;

            string current = GetSelectedUrl();
            var urls = new List<string>();
            if (_settings.SourceUrls != null)
            {
                foreach (var u in _settings.SourceUrls)
                {
                    if (string.IsNullOrWhiteSpace(u)) continue;
                    if (string.Equals(u, current, StringComparison.OrdinalIgnoreCase)) continue;
                    if (u.IndexOf("iptvportal.ru", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    urls.Add(u);
                }
            }
            if (urls.Count == 0) return;

            var channels = _allChannels;
            Task.Run(() =>
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var u in urls)
                {
                    try
                    {
                        var playlist = PlaylistLoader.Load(u, false);
                        foreach (var c in playlist.Channels)
                        {
                            if (string.IsNullOrWhiteSpace(c.Group)) continue;
                            var key = GroupKey(c.Name);
                            if (key.Length == 0 || map.ContainsKey(key)) continue;
                            map[key] = c.Group;
                        }
                    }
                    catch { }
                }
                if (map.Count == 0 || this.IsDisposed) return;
                try
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        if (this.IsDisposed || !ReferenceEquals(channels, _allChannels)) return;
                        bool changed = false;
                        foreach (var ch in channels)
                        {
                            if (!string.IsNullOrWhiteSpace(ch.Group) &&
                                !string.Equals(ch.Group, "Эфирные каналы", StringComparison.OrdinalIgnoreCase))
                                continue;
                            string group;
                            var key = GroupKey(ch.Name);
                            if (key.Length > 0 && map.TryGetValue(key, out group))
                            {
                                ch.Group = group;
                                changed = true;
                            }
                        }
                        if (!changed) return;
                        RebuildCategories();
                        ApplyFilter(_searchPlaceholderActive ? "" : (_txtSearch.Text ?? ""));
                    }));
                }
                catch { }
            });
        }

        /// <summary>Ключ сопоставления каналов: без пробелов и знаков, но с HD.</summary>
        private static string GroupKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            return System.Text.RegularExpressions.Regex.Replace(
                name.ToLowerInvariant(), @"[^0-9a-zа-яё]+", "");
        }

        private void StyleCategoryButtons()
        {
            foreach (var b in _catButtons)
            {
                bool active = string.Equals((b.Tag as string) ?? "",
                    _selectedGroup ?? "", StringComparison.OrdinalIgnoreCase);
                b.BackColor = active ? Theme.Accent : Theme.PanelAlt;
                b.ForeColor = active ? Color.White : Theme.Text;
                b.FlatAppearance.MouseOverBackColor = active ? Theme.AccentHover : Theme.PanelHover;
            }
        }

        private void PositionCategoryButtons()
        {
            if (_catScroll == null) return;
            int x = 0;
            foreach (var b in _catButtons)
            {
                b.Left = x - _catScrollX;
                x += b.Width + 6;
            }
            _catContentWidth = _catButtons.Count > 0 ? Math.Max(0, x - 6) : 0;
        }

        private void ScrollCategories(int delta)
        {
            int view = _catScroll != null ? _catScroll.ClientSize.Width : 0;
            int max = Math.Max(0, _catContentWidth - view);
            int next = _catScrollX + delta;
            if (next < 0) next = 0;
            if (next > max) next = max;
            _catScrollX = next;
            PositionCategoryButtons();
            UpdateCatArrows();
        }

        private void UpdateCatArrows()
        {
            if (_btnCatLeft == null || _btnCatRight == null) return;
            int view = _catScroll != null ? _catScroll.ClientSize.Width : 0;
            int max = Math.Max(0, _catContentWidth - view);
            _btnCatLeft.Enabled = _catScrollX > 0;
            _btnCatRight.Enabled = _catScrollX < max;
            _btnCatLeft.ForeColor = _btnCatLeft.Enabled ? Theme.TextDim : Theme.Border;
            _btnCatRight.ForeColor = _btnCatRight.Enabled ? Theme.TextDim : Theme.Border;
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

            // состояние списка каналов из прошлых запусков — тихо, без статуса
            _channelsHidden = _settings.ChannelsHidden;
            ApplyChannelListVisibility(true);

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

            if (_channelsHidden && !_fullscreen)
            {
                // панель каналов схлопнута — видео занимает всё окно
                try { _split.Panel2Collapsed = true; } catch { }
                return;
            }

            int distance = _split.Width - ChannelPanelWidth - _split.SplitterWidth;
            if (distance > 0)
                _split.SplitterDistance = distance;
        }

        private void ToggleTopMost()
        {
            TopMost = !TopMost;
            StyleTopMostButton();
            SetStatus(TopMost ? "Окно закреплено поверх всех программ" : "Обычный режим окна");
        }

        // ===== Кнопка-иконка (шестерёнка) =====

        /// <summary>Плоская кнопка без рамки: прозрачный фон, подсветка при наведении.</summary>
        private Button CreateIconButton(Image icon)
        {
            var b = new Button
            {
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Panel,
                Image = icon,
                ImageAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                TabStop = true,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.PanelHover;
            b.FlatAppearance.MouseDownBackColor = Theme.PanelHover;
            return b;
        }

        /// <summary>Рисует шестерёнку: 8 зубьев + тело, отверстие прозрачное.</summary>
        private static Bitmap CreateGearIcon(int size, Color color)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;

                float cx = size / 2f, cy = size / 2f;
                float rOuter = size * 0.44f; // кончики зубьев
                float rBody = size * 0.32f;  // тело шестерни
                float rHole = size * 0.13f;  // отверстие

                using (var path = new GraphicsPath(FillMode.Alternate))
                {
                    int teeth = 8;
                    float toothWidth = (float)(2 * Math.PI * rBody / teeth * 0.52);
                    for (int i = 0; i < teeth; i++)
                    {
                        float angle = i * 360f / teeth;
                        var pts = new[]
                        {
                            new PointF(-toothWidth / 2, -rOuter),
                            new PointF( toothWidth / 2, -rOuter),
                            new PointF( toothWidth / 2, -rBody),
                            new PointF(-toothWidth / 2, -rBody)
                        };
                        var rotated = RotatePoints(pts, angle, cx, cy);
                        path.AddPolygon(rotated);
                    }
                    path.AddEllipse(cx - rBody, cy - rBody, rBody * 2, rBody * 2);
                    path.AddEllipse(cx - rHole, cy - rHole, rHole * 2, rHole * 2);

                    using (var brush = new SolidBrush(color))
                        g.FillPath(brush, path);
                }
            }
            return bmp;
        }

        private static PointF[] RotatePoints(PointF[] pts, float degrees, float cx, float cy)
        {
            var rad = degrees * Math.PI / 180.0;
            var cos = (float)Math.Cos(rad);
            var sin = (float)Math.Sin(rad);
            var result = new PointF[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                float x = pts[i].X, y = pts[i].Y;
                result[i] = new PointF(
                    cx + x * cos - y * sin,
                    cy + x * sin + y * cos);
            }
            return result;
        }

        // ===== Скрытие списка каналов (оконный режим) =====

        private void ToggleChannelList()
        {
            if (_fullscreen)
                return; // в полном экране список и так скрыт за оверлеем

            _channelsHidden = !_channelsHidden;
            var clientBefore = this.ClientSize;
            ApplyChannelListVisibility(false);

            // SplitContainer при схлопывании/развороте панели сдвигает
            // разделитель и меняет ширину окна — возвращаем как было
            BeginInvoke(new Action(() =>
            {
                try
                {
                    if (this.ClientSize != clientBefore)
                        this.ClientSize = clientBefore;
                    ApplySplitLayout();
                }
                catch { }
            }));

            try
            {
                _settings.ChannelsHidden = _channelsHidden;
                SettingsManager.Save(_settings);
            }
            catch { }
        }

        private void ApplyChannelListVisibility(bool silent)
        {
            if (_split == null || _btnToggleChannels == null) return;

            _split.Panel2Collapsed = _channelsHidden && !_fullscreen;
            _btnToggleChannels.Text = _channelsHidden ? "Показать список" : "Скрыть список";
            _btnToggleChannels.Width = _channelsHidden ? 136 : 120;

            if (!silent)
                SetStatus(_channelsHidden ? "Список каналов скрыт" : "Список каналов показан");
        }

        private void StyleTopMostButton()
        {
            try
            {
                if (_btnOnTop == null) return;
                _btnOnTop.Text = TopMost ? "Поверх всех окон (вкл)" : "Поверх всех окон";
                if (TopMost)
                {
                    _btnOnTop.BackColor = Theme.Accent;
                    _btnOnTop.ForeColor = Color.White;
                    _btnOnTop.FlatAppearance.MouseOverBackColor = Theme.AccentHover;
                }
                else
                {
                    _btnOnTop.BackColor = Theme.PanelAlt;
                    _btnOnTop.ForeColor = Theme.Text;
                    _btnOnTop.FlatAppearance.MouseOverBackColor = Theme.PanelHover;
                }
                _btnOnTop.Invalidate();
            }
            catch { }
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

        /// <summary>
        /// Логотип канала: из плейлиста (tvg-logo) либо выводится из адреса потока
        /// провайдера: /play/a001/index.m3u8 -> /playlist/tvg-logo/a001.png.
        /// </summary>
        private static string ResolveLogoUrl(Channel ch)
        {
            if (ch == null) return null;
            if (!string.IsNullOrWhiteSpace(ch.Logo)) return ch.Logo.Trim();
            if (string.IsNullOrWhiteSpace(ch.Url)) return null;

            var m = System.Text.RegularExpressions.Regex.Match(ch.Url,
                @"^(https?://[^/]+)/play/([^/]+)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return m.Groups[1].Value + "/playlist/tvg-logo/" + m.Groups[2].Value + ".png";
        }

        /// <summary>Фоновая загрузка логотипов: первые 200 каналов, кэшируется по URL.</summary>
        private void StartLoadingLogos()
        {
            int generation;
            lock (_cacheLock)
            {
                _logoCache.Clear();
                generation = ++_logoGeneration;
            }
            if (_allChannels == null) return;

            var urls = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in _allChannels.Take(200))
            {
                var u = ResolveLogoUrl(ch);
                if (string.IsNullOrWhiteSpace(u)) continue;
                ch.Logo = u;
                if (seen.Add(u)) urls.Add(u);
            }
            if (urls.Count == 0) return;

            Task.Run(() =>
            {
                int sinceRepaint = 0;
                foreach (var u in urls)
                {
                    if (System.Threading.Volatile.Read(ref _logoGeneration) != generation) return;

                    try
                    {
                        lock (_cacheLock)
                        {
                            if (_logoCache.ContainsKey(u)) continue;
                        }

                        var icon = LoadImageAsync(u);
                        if (icon != null)
                        {
                            lock (_cacheLock)
                            {
                                if (_logoCache.ContainsKey(u)) continue;
                                _logoCache[u] = icon;
                            }
                            sinceRepaint++;
                        }
                    }
                    catch { }

                    // перерисовываем список по мере поступления логотипов
                    if (sinceRepaint >= 4)
                    {
                        sinceRepaint = 0;
                        RepaintChannelList();
                    }
                }
                RepaintChannelList();
            });
        }

        private void RepaintChannelList()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated || _lbChannels == null) return;
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed && _lbChannels != null) _lbChannels.Invalidate();
                }));
            }
            catch { }
        }

        /// <summary>Скачивает PNG в память и отдаёт независимую копию (поток закрывается).</summary>
        private static Image LoadImageAsync(string url)
        {
            try
            {
                using (var wc = new System.Net.WebClient())
                {
                    wc.Headers.Add("User-Agent", "IPTVViewer/1.0");
                    wc.Headers.Add("Referer", "http://tv.isp-nl.ru/");
                    var data = wc.DownloadData(url);
                    using (var ms = new System.IO.MemoryStream(data))
                    using (var tmp = Image.FromStream(ms))
                        return new Bitmap(tmp);
                }
            }
            catch
            {
                return null;
            }
        }

        private int DrawChannelLogo(Graphics g, Channel ch, int x, int y, int width, int height, bool selected)
        {
            var url = ResolveLogoUrl(ch);

            Image logo = null;
            if (!string.IsNullOrWhiteSpace(url))
            {
                lock (_cacheLock)
                {
                    _logoCache.TryGetValue(url, out logo);
                }
            }

            if (logo == null || logo.Width <= 0 || logo.Height <= 0)
            {
                // Заглушка, пока логотип загружается: плашка с первой буквой
                using (var b = new SolidBrush(Color.FromArgb(34, Theme.TextDim)))
                    g.FillRectangle(b, x, y, width, height);
                var letter = string.IsNullOrWhiteSpace(ch.Name) ? "?" : ch.Name.Trim().Substring(0, 1).ToUpperInvariant();
                TextRenderer.DrawText(g, letter, LogoLetterFont, new Rectangle(x, y, width, height), Theme.TextDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return x + width + 8;
            }

            float sw = logo.Width, sh = logo.Height;
            float scale = Math.Min((float)width / sw, (float)height / sh);
            int dw = Math.Max(1, (int)(sw * scale));
            int dh = Math.Max(1, (int)(sh * scale));
            int dx = x + (width - dw) / 2;
            int dy = y + (height - dh) / 2;

            g.DrawImage(logo, dx, dy, dw, dh);
            return x + width + 8;
        }

        // ===== Заставка видео (когда воспроизведение остановлено) =====

        private void VideoIdle_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.Black);

            int w = _videoIdle.ClientSize.Width;
            int h = _videoIdle.ClientSize.Height;
            if (w <= 0 || h <= 0) return;

            int logoH = 0;
            if (_appLogo != null && _appLogo.Width > 0 && _appLogo.Height > 0)
            {
                int maxW = Math.Max(120, (int)(w * 0.34));
                int maxH = Math.Max(60, (int)(h * 0.34));
                float scale = Math.Min((float)maxW / _appLogo.Width, (float)maxH / _appLogo.Height);
                if (scale > 1f) scale = 1f;

                int logoW = Math.Max(1, (int)(_appLogo.Width * scale));
                logoH = Math.Max(1, (int)(_appLogo.Height * scale));

                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_appLogo, (w - logoW) / 2, (h - logoH) / 2 - 14, logoW, logoH);
            }

            var textRect = new Rectangle(0, (h + logoH) / 2 + 4, w, 28);
            TextRenderer.DrawText(g, "IPTV Newline", Theme.FontRegular, textRect, Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
        }

        private void ShowVideoIdle()
        {
            if (_videoIdle == null) return;
            try
            {
                if (_videoView != null) _videoView.Visible = false;
                _videoIdle.Visible = true;
                _videoIdle.BringToFront();
                _videoIdle.Invalidate();
            }
            catch { }
        }

        private void HideVideoIdle()
        {
            if (_videoIdle == null) return;
            try
            {
                _videoIdle.Visible = false;
                if (_videoView != null) _videoView.Visible = true;
            }
            catch { }
        }

        /// <summary>Показать заставку, если плеер уже не играет (события VLC).</summary>
        private void ShowIdleIfStopped()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (IsDisposed || _mediaPlayer == null) return;

                        // Событие Stopped приходит и при переключении канала —
                        // не показываем логотип, если уже идёт открытие/буферизация.
                        var state = _mediaPlayer.State;
                        if (_mediaPlayer.IsPlaying ||
                            state == VLCState.Opening ||
                            state == VLCState.Buffering) return;

                        ShowVideoIdle();
                    }
                    catch { }
                }));
            }
            catch { }
        }

        private void HideIdleOnPlaying()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed) HideVideoIdle();
                }));
            }
            catch { }
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

            // убираем зарезервированную строку контролов — видео занимает весь экран
            try { _videoHost.RowStyles[1].Height = 0f; } catch { }

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
            // возвращаем список каналов, если он не был скрыт пользователем
            _split.Panel2Collapsed = _channelsHidden;

            // возвращаем строку контролов
            try { _videoHost.RowStyles[1].Height = 52f; } catch { }

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
            else if (e.Control && e.KeyCode == Keys.L && !_fullscreen)
            {
                ToggleChannelList();
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
            var now = DateTime.Now;
            var current = _epg != null ? _epg.GetCurrent(ch, now) : null;
            if (current != null)
            {
                int percent = 0;
                var total = (current.Stop - current.Start).TotalSeconds;
                if (total > 0)
                    percent = (int)Math.Round(100 * (now - current.Start).TotalSeconds / total);
                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;
                SetStatus(ch.Name + " — " + current.Title + " (" + current.TimeRange + ") — " + percent + "%");
            }
            else
            {
                SetStatus(ch.Name);
            }
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

            int logoX = DrawChannelLogo(e.Graphics, ch, e.Bounds.X + 8, e.Bounds.Y + 5, 36, 26, selected);
            int textX = logoX + 6;
            int textW = Math.Max(20, e.Bounds.Right - 14 - textX);
            var nameRect = new Rectangle(textX, e.Bounds.Y + 6, textW, 20);
            var secondRect = new Rectangle(textX, e.Bounds.Y + 26, textW, 17);

            TextRenderer.DrawText(e.Graphics, ch.Name ?? "", Theme.FontBold, nameRect, Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            string second = ch.Group;
            Color secondColor = Theme.TextDim;
            EpgProgram current = _epg != null ? _epg.GetCurrent(ch, DateTime.Now) : null;

            if (current != null)
            {
                // у всех каналов с телепрограммой показываем текущую передачу
                second = current.Title;
                secondColor = (hover || selected) ? Theme.Accent : Theme.TextDim;
            }

            if (!string.IsNullOrWhiteSpace(second))
            {
                TextRenderer.DrawText(e.Graphics, second, Theme.FontSmall,
                    secondRect, secondColor,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            // тонкий индикатор прогресса текущей передачи
            if (current != null && current.Stop > current.Start)
            {
                double frac = (DateTime.Now - current.Start).TotalSeconds /
                              (current.Stop - current.Start).TotalSeconds;
                if (frac < 0) frac = 0;
                if (frac > 1) frac = 1;

                int trackLeft = e.Bounds.X + 14;
                int trackWidth = Math.Max(0, e.Bounds.Right - 14 - trackLeft);
                int trackY = e.Bounds.Bottom - 5;

                using (var track = new SolidBrush(Color.FromArgb(70, Theme.Border)))
                    e.Graphics.FillRectangle(track, trackLeft, trackY, trackWidth, 2);

                int fillWidth = (int)Math.Round(trackWidth * frac);
                if (fillWidth > 0)
                {
                    Color fillColor = (hover || selected)
                        ? Theme.Accent
                        : Color.FromArgb(150, Theme.Accent);
                    using (var fill = new SolidBrush(fillColor))
                        e.Graphics.FillRectangle(fill, trackLeft, trackY, fillWidth, 2);
                }
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
            bool byGroup = !string.IsNullOrEmpty(_selectedGroup);
            foreach (var ch in _allChannels)
            {
                if (byGroup && !string.Equals(ch.Group ?? "", _selectedGroup, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (q.Length == 0 ||
                    (ch.Name ?? "").ToLowerInvariant().Contains(q) ||
                    (ch.Group ?? "").ToLowerInvariant().Contains(q))
                {
                    _lbChannels.Items.Add(ch);
                }
            }
            _lbChannels.EndUpdate();
            SetStatus(byGroup
                ? _selectedGroup + ": " + _lbChannels.Items.Count + " из " + _allChannels.Count
                : "Каналов: " + _lbChannels.Items.Count + " из " + _allChannels.Count);
        }

        private void LoadSelectedSource()
        {
            var url = GetSelectedUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                SetStatus("Не указан URL плейлиста");
                return;
            }

            SetStatus("Запуск плеера — загрузка списка каналов…");
            SetSourceButtonsEnabled(false);

            Task.Run(() =>
            {
                try
                {
                    // Каналы без ожидания EPG: список и воспроизведение
                    // появляются сразу, телепрограмма догружается фоном.
                    var result = PlaylistLoader.Load(url, false);
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
            _selectedGroup = "";
            RebuildCategories();
            TryFillGroupsFromOtherSource();
            StartLoadingLogos();
            _epgUrl = !string.IsNullOrWhiteSpace(_settings.EpgUrl)
                ? _settings.EpgUrl.Trim()
                : result.EpgUrl;

            if (!_searchPlaceholderActive && !string.IsNullOrEmpty(_txtSearch.Text))
                ApplyFilter(_txtSearch.Text);
            else
                ApplyFilter("");

            // Портал: телепрограмму грузим отдельным проходом, не задерживая старт
            if (_settings.EpgEnabled)
            {
                if (result.Portal != null &&
                    result.Portal.BaseUri != null &&
                    !string.IsNullOrEmpty(result.Portal.SessionId))
                {
                    LoadPortalEpgAsync(result.Portal);
                }
                else if (!string.IsNullOrWhiteSpace(_epgUrl) && _allChannels.Count > 0)
                {
                    LoadEpgAsync(_epgUrl, _allChannels);
                }
                else
                {
                    SetStatus("Готово. Каналов: " + _allChannels.Count);
                }
            }
            else
            {
                SetStatus("Готово. Каналов: " + _allChannels.Count);
            }

            // Автовоспроизведение: последний просмотренный канал, иначе первый
            if (_autoPlayOnLoad && _allChannels.Count > 0)
            {
                _autoPlayOnLoad = false;
                try
                {
                    var wanted = _settings.LastChannelUrl;
                    if (!string.IsNullOrWhiteSpace(wanted))
                    {
                        foreach (var ch in _allChannels)
                        {
                            if (ch != null && ch.Url == wanted)
                            {
                                AutoSelectAndPlay(ch);
                                return;
                            }
                        }
                    }
                    AutoSelectAndPlay(_allChannels[0]);
                }
                catch { }
            }
        }

        private void AutoSelectAndPlay(Channel channel)
        {
            if (channel == null) return;
            for (int i = 0; i < _lbChannels.Items.Count; i++)
            {
                if (_lbChannels.Items[i] == channel)
                {
                    _lbChannels.SelectedIndex = i;
                    break;
                }
            }
            PlaySelected();
        }

        /// <summary>
        /// Телепрограмма портала — отдельным фоновым проходом, после того как
        /// список каналов уже показан и канал начал воспроизводиться.
        /// </summary>
        private void LoadPortalEpgAsync(PortalClient.PortalResult portal)
        {
            if (portal == null || portal.BaseUri == null || string.IsNullOrEmpty(portal.SessionId))
                return;

            _epgLoading = true;
            int request = ++_epgRequest;
            SetStatus("Загрузка телепрограммы…");

            Task.Run(async () =>
            {
                // небольшая пауза: даём видеопотоку спокойно начать
                // воспроизведение, чтобы EPG не конкурировала за сеть
                try { await Task.Delay(3000); } catch { }

                if (this.IsDisposed || request != _epgRequest) return;
                try
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        if (this.IsDisposed || request != _epgRequest) return;
                        SetStatus("Загрузка телепрограммы…");
                    }));
                }
                catch { }

                EpgData data = null;
                try { data = PortalClient.LoadEpg(portal.BaseUri, portal.SessionId, portal.Channels); }
                catch { }
                if (this.IsDisposed || request != _epgRequest) return;
                try
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        if (this.IsDisposed || request != _epgRequest) return;
                        _epgLoading = false;
                        _epg = data ?? new EpgData();
                        _lbChannels.Invalidate();
                        SetStatus(_epg.IsEmpty
                            ? "Готово. Каналов: " + _allChannels.Count
                            : "Телепрограмма загружена. Каналов: " + _allChannels.Count);

                        if (!_epg.IsEmpty && _epgTimer == null)
                        {
                            _epgTimer = new Timer { Interval = 30000 };
                            _epgTimer.Tick += (s, e) => _lbChannels.Invalidate();
                            _epgTimer.Start();
                        }
                    }));
                }
                catch { }
            });
        }

        private void LoadEpgAsync(string epgUrl, List<Channel> channels)
        {
            _epgLoading = true;
            int request = ++_epgRequest;
            SetStatus("Загрузка телепрограммы…");

            Task.Run(() =>
            {
                EpgData data = null;
                try { data = EpgLoader.Load(epgUrl, channels); }
                catch { }
                if (this.IsDisposed || request != _epgRequest) return;
                try
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        if (request != _epgRequest) return;
                        _epgLoading = false;
                        _epg = data ?? new EpgData();
                        _lbChannels.Invalidate();
                        SetStatus(_epg.IsEmpty
                            ? "Готово. Каналов: " + _allChannels.Count
                            : "Телепрограмма загружена. Каналов: " + _allChannels.Count);

                        if (!_epg.IsEmpty && _epgTimer == null)
                        {
                            _epgTimer = new Timer { Interval = 30000 };
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

            // запоминаем последний просмотренный канал (для автовоспроизведения)
            if (!string.IsNullOrWhiteSpace(ch.Url) && _settings.LastChannelUrl != ch.Url)
            {
                _settings.LastChannelUrl = ch.Url;
                SettingsManager.Save(_settings);
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
                StopPlayback(false);
                _currentMedia = new Media(_libVLC, new Uri(url));
                _mediaPlayer.Volume = _volume.Value;
                _mediaPlayer.Play(_currentMedia);
                HideVideoIdle();
                SetStatus("Воспроизведение: " + name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось запустить воспроизведение:\n" + ex.Message,
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void StopPlayback(bool showIdle = true)
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

            if (showIdle)
                ShowVideoIdle();
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
            if (!silent) SetStatus("Проверка обновлений...");

            var repo = _settings.UpdateRepo;

            // служебный ключ --current X.Y.Z позволяет проверить поток
            // обновления, притворившись более старой версией
            string versionOverride = null;
            var cliArgs = Environment.GetCommandLineArgs();
            for (int i = 0; i < cliArgs.Length - 1; i++)
            {
                if (string.Equals(cliArgs[i], "--current", StringComparison.OrdinalIgnoreCase))
                    versionOverride = cliArgs[i + 1];
            }

            Task.Run(() =>
            {
                UpdateInfo info;
                try
                {
                    info = versionOverride != null
                        ? UpdateChecker.Check(repo, versionOverride)
                        : UpdateChecker.Check(repo);
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
                _settings.ChannelsHidden = _channelsHidden && !_fullscreen;
                SettingsManager.Save(_settings);
            }
            catch { }

            try
            {
                if (_epgTimer != null) { _epgTimer.Stop(); _epgTimer.Dispose(); }
                if (_fsTimer != null) { _fsTimer.Stop(); _fsTimer.Dispose(); }
            }
            catch { }

            // VLC выключаем в фоновом потоке: _mediaPlayer.Stop()/Dispose()
            // синхронно дожидается сетевых потоков libvlc и на живом
            // IPTV-стриме может блокировать UI-поток — окно зависало при
            // закрытии. Форма закрывается сразу, Main дождётся выключения
            // не дольше нескольких секунд (Program.WaitForVlcShutdown).
            try
            {
                var mp = _mediaPlayer; _mediaPlayer = null;
                var lib = _libVLC; _libVLC = null;
                var media = _currentMedia; _currentMedia = null;

                if (mp != null || lib != null)
                {
                    var worker = new System.Threading.Thread(() =>
                    {
                        try { if (mp != null) mp.Stop(); } catch { }
                        try { if (media != null) media.Dispose(); } catch { }
                        try { if (mp != null) mp.Dispose(); } catch { }
                        try { if (lib != null) lib.Dispose(); } catch { }
                        Program.SignalVlcShutdownDone();
                    });
                    worker.IsBackground = true;
                    worker.Start();
                }
                else
                {
                    Program.SignalVlcShutdownDone();
                }
            }
            catch
            {
                Program.SignalVlcShutdownDone();
            }
        }
    }
}
