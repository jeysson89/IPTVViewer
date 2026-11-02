using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace IPTVViewer
{
    public partial class SplashForm : Form
    {
        private Timer _timer;
        private Image _logo;
        private readonly Label _lblVersion;

        public SplashForm()
        {
            InitializeComponent();
            LoadLogo();

            _lblVersion = new Label
            {
                Text = "v" + UpdateChecker.CurrentVersion + "  ·  VLC engine",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Theme.TextDim,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Bottom,
                Height = 30
            };
            this.Controls.Add(_lblVersion);

            _timer = new Timer { Interval = 2600 };
            _timer.Tick += (s, e) => { _timer.Stop(); this.Close(); };
            _timer.Start();
            this.Click += (s, e) => { _timer.Stop(); this.Close(); };
        }

        private void InitializeComponent()
        {
            this.Text = "IPTV Viewer";
            this.Icon = AppIcon.Get();
            this.Size = new Size(660, 380);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Theme.Bg;
            this.DoubleBuffered = true;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        private void LoadLogo()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream("IPTVViewer.Resources.logo.png"))
                {
                    if (stream != null)
                        _logo = Image.FromStream(stream);
                }
            }
            catch { }

            if (_logo == null)
            {
                try
                {
                    var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    var path = Path.Combine(dir ?? "", "logo.png");
                    if (File.Exists(path))
                        _logo = Image.FromFile(path);
                }
                catch { }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            // Внешняя тонкая рамка
            using (var pen = new Pen(Theme.Border, 1))
                g.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);

            int cardW = Math.Min(540, this.Width - 70);
            int cardH = (int)(cardW * 640f / 1920f) + 44;
            var card = new Rectangle((this.Width - cardW) / 2, (this.Height - cardH) / 2 - 12, cardW, cardH);

            using (var path = RoundedPanel.Round(card, 18))
            using (var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            using (var fill = new SolidBrush(Color.White))
            {
                var shadowRect = card;
                shadowRect.Offset(0, 4);
                using (var sp = RoundedPanel.Round(shadowRect, 18))
                    g.FillPath(shadow, sp);
                g.FillPath(fill, path);
            }

            if (_logo != null)
            {
                int pad = 26;
                int availW = card.Width - pad * 2;
                int availH = card.Height - pad * 2;
                double ratio = Math.Min((double)availW / _logo.Width, (double)availH / _logo.Height);
                int w = (int)(_logo.Width * ratio);
                int h = (int)(_logo.Height * ratio);
                g.DrawImage(_logo, card.X + (card.Width - w) / 2, card.Y + (card.Height - h) / 2, w, h);
            }

            // Акцентная линия под карточкой
            using (var accent = new SolidBrush(Theme.Accent))
                g.FillRectangle(accent, this.Width / 2 - 40, card.Bottom + 22, 80, 3);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_timer != null) _timer.Dispose();
                if (_logo != null) _logo.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
