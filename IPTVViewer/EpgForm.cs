using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace IPTVViewer
{
    /// <summary>
    /// Окно телепрограммы канала: список передач на загруженное окно EPG
    /// с подсветкой текущей передачи.
    /// </summary>
    public class EpgForm : Form
    {
        private readonly Channel _channel;
        private readonly EpgData _epg;
        private ListBox _list;
        private Label _lblNow;

        public EpgForm(Channel channel, EpgData epg)
        {
            _channel = channel;
            _epg = epg;

            this.Text = "Телепрограмма — " + (channel != null ? channel.Name : "");
            this.Icon = AppIcon.Get();
            this.Size = new Size(660, 660);
            this.MinimumSize = new Size(520, 420);
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = Theme.Bg;
            this.ForeColor = Theme.Text;
            this.Font = Theme.FontRegular;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.DoubleBuffered = true;

            BuildUi();
            FillPrograms();
        }

        private void BuildUi()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Theme.Panel };
            var name = Theme.CreateLabel(_channel != null ? _channel.Name : "Канал", false, 15f, true);
            name.Location = new Point(18, 14);
            header.Controls.Add(name);

            var sub = Theme.CreateLabel(
                string.IsNullOrWhiteSpace(_channel != null ? _channel.Group : null)
                    ? "Телепрограмма"
                    : _channel.Group + "  ·  телепрограмма",
                true, 9f, false);
            sub.Location = new Point(20, 46);
            header.Controls.Add(sub);

            _lblNow = Theme.CreateLabel("", true, 9f, false);
            _lblNow.AutoSize = false;
            _lblNow.TextAlign = ContentAlignment.MiddleRight;
            _lblNow.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _lblNow.Size = new Size(260, 22);
            _lblNow.Location = new Point(header.Width - 278, 18);
            header.Controls.Add(_lblNow);
            header.Resize += (s, e) => _lblNow.Left = header.Width - _lblNow.Width - 18;

            this.Controls.Add(header);

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Panel };
            var btnPlay = Theme.CreateButton("▶  Смотреть канал", true);
            btnPlay.Size = new Size(180, 36);
            btnPlay.Location = new Point(18, 11);
            btnPlay.Click += (s, e) => { this.DialogResult = DialogResult.OK; this.Close(); };

            var btnClose = Theme.CreateButton("Закрыть", false);
            btnClose.Size = new Size(110, 36);
            btnClose.DialogResult = DialogResult.Cancel;
            btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnClose.Location = new Point(footer.Width - 128, 11);
            footer.Resize += (s, e) => btnClose.Left = footer.Width - btnClose.Width - 18;

            footer.Controls.Add(btnPlay);
            footer.Controls.Add(btnClose);
            this.Controls.Add(footer);

            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Panel,
                ForeColor = Theme.Text,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 52,
                IntegralHeight = false,
                Font = Theme.FontRegular
            };
            _list.DrawItem += List_DrawItem;
            _list.DoubleClick += (s, e) =>
            {
                if (_list.SelectedIndex >= 0)
                {
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
            };

            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(10, 6, 10, 6) };
            host.Controls.Add(_list);
            this.Controls.Add(host);

            this.CancelButton = btnClose;
            this.AcceptButton = btnPlay;
        }

        private void FillPrograms()
        {
            var programs = _epg != null ? _epg.GetPrograms(_channel) : null;
            if (programs == null || programs.Count == 0)
            {
                _list.Items.Add("Нет данных о телепрограмме");
                _lblNow.Text = "";
                return;
            }

            var now = DateTime.Now;
            int currentIndex = -1;

            for (int i = 0; i < programs.Count; i++)
            {
                var p = programs[i];
                _list.Items.Add(p);
                if (currentIndex < 0 && p.Start <= now && now < p.Stop)
                    currentIndex = i;
            }

            var current = _epg.GetCurrent(_channel, now);
            if (current != null)
                _lblNow.Text = "Сейчас: " + current.TimeRange;

            if (currentIndex < 0)
            {
                for (int i = 0; i < programs.Count; i++)
                {
                    if (programs[i].Start > now)
                    {
                        currentIndex = i;
                        break;
                    }
                }
            }

            if (currentIndex >= 0)
            {
                _list.SelectedIndex = currentIndex;
                int top = Math.Max(0, currentIndex - 2);
                if (top < _list.Items.Count)
                    _list.TopIndex = top;
            }
        }

        private void List_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _list.Items.Count) return;

            var program = _list.Items[e.Index] as EpgProgram;
            var now = DateTime.Now;

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool live = program != null && program.Start <= now && now < program.Stop;
            bool past = program != null && program.Stop <= now;

            var bg = selected ? Theme.Selection : Theme.Panel;
            using (var b = new SolidBrush(bg))
                e.Graphics.FillRectangle(b, e.Bounds);

            if (live)
            {
                using (var accent = new SolidBrush(Theme.Accent))
                    e.Graphics.FillRectangle(accent, new Rectangle(e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height));
            }

            var timeRect = new Rectangle(e.Bounds.X + 16, e.Bounds.Y + 8, 118, 20);
            var titleRect = new Rectangle(e.Bounds.X + 140, e.Bounds.Y + 7, e.Bounds.Width - 160, 22);
            var subRect = new Rectangle(e.Bounds.X + 140, e.Bounds.Y + 28, e.Bounds.Width - 160, 18);

            if (program == null)
            {
                TextRenderer.DrawText(e.Graphics, Convert.ToString(_list.Items[e.Index]),
                    Theme.FontRegular, titleRect, Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                return;
            }

            var timeColor = live ? Theme.Accent : (past ? Theme.TextDim : Theme.Text);
            var titleColor = past ? Theme.TextDim : Theme.Text;

            TextRenderer.DrawText(e.Graphics, program.TimeRange, Theme.FontSmall, timeRect, timeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(e.Graphics, program.Title, Theme.FontBold, titleRect, titleColor,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            string sub = live ? "Сейчас в эфире" : (past ? "Завершилась" : "Далее");
            var subColor = live ? Theme.Accent : Theme.TextDim;
            TextRenderer.DrawText(e.Graphics, sub, Theme.FontSmall, subRect, subColor,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            using (var pen = new Pen(Theme.Border))
                e.Graphics.DrawLine(pen, e.Bounds.X + 14, e.Bounds.Bottom - 1, e.Bounds.Right - 14, e.Bounds.Bottom - 1);
        }
    }
}
