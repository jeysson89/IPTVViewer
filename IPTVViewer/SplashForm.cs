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
                Text = "v" + UpdateChecker.CurrentVersion,
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(130, 0, 0, 0),
                BackColor = Color.White, // на самой карточке
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Bottom,
                Height = 26
            };
            this.Controls.Add(_lblVersion);

            _timer = new Timer { Interval = 2600 };
            _timer.Tick += (s, e) => { _timer.Stop(); this.Close(); };
            _timer.Start();
            this.Click += (s, e) => { _timer.Stop(); this.Close(); };
        }

        private void InitializeComponent()
        {
            this.Text = "IPTV Newline";
            this.Icon = AppIcon.Get();
            this.ClientSize = new Size(540, 236);
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.White; // фон окна — сама карточка, лишнего нет
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

            // окно = сама карточка со скруглёнными углами, фона вокруг нет
            using (var path = RoundedPanel.Round(new Rectangle(0, 0, this.Width, this.Height), 18))
                this.Region = new Region(path);

            // тонкий контур по краю карточки
            using (var pen = new Pen(Color.FromArgb(45, 0, 0, 0), 1))
                g.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);

            if (_logo != null)
            {
                int logoW = this.Width - 40;
                int logoH = (int)(logoW * 640f / 1920f);
                g.DrawImage(_logo, (this.Width - logoW) / 2, 14, logoW, logoH);

                // Акцентная линия под логотипом — внутри карточки
                using (var accent = new SolidBrush(Theme.Accent))
                    g.FillRectangle(accent, this.Width / 2 - 40, 14 + logoH + 16, 80, 3);
            }
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
