using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace IPTVViewer
{
    /// <summary>
    /// Тёмный чекбокс с собственной отрисовкой: системный Flat-чекбокс
    /// на тёмном фоне плохо виден и не всегда попадает под клик.
    /// </summary>
    public class DarkCheckBox : CheckBox
    {
        private const int BoxSize = 18;

        public DarkCheckBox()
        {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Height = 26;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontRegular;
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            int top = (Height - BoxSize) / 2;
            var box = new Rectangle(0, top, BoxSize, BoxSize);

            using (var path = RoundedPanel.Round(box, 4))
            {
                using (var fill = new SolidBrush(Checked ? Theme.Accent : Theme.PanelAlt))
                    g.FillPath(fill, path);
                using (var pen = new Pen(Checked ? Theme.Accent : Theme.Border, 1.4f))
                    g.DrawPath(pen, path);
            }

            if (Checked)
            {
                using (var pen = new Pen(Color.White, 2f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[]
                    {
                        new PointF(box.X + 4.0f, box.Y + 9.0f),
                        new PointF(box.X + 7.5f, box.Y + 12.5f),
                        new PointF(box.X + 14.0f, box.Y + 5.5f)
                    });
                }
            }

            if (Focused)
            {
                using (var focus = new Pen(Color.FromArgb(90, Theme.Accent), 1f))
                    g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
            }

            var textRect = new Rectangle(BoxSize + 8, 0, Math.Max(0, Width - BoxSize - 8), Height);
            TextRenderer.DrawText(g, Text, Font, textRect,
                Enabled ? ForeColor : Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
