using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace IPTVViewer
{
    public class RoundedPanel : Panel
    {
        public int Radius { get; set; }
        public Color FillColor { get; set; }
        public Color BorderColor { get; set; }
        public float BorderWidth { get; set; }

        public RoundedPanel()
        {
            Radius = 12;
            FillColor = Color.White;
            BorderColor = Color.Transparent;
            BorderWidth = 1f;
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            using (var path = Round(ClientRectangle, Radius))
                Region = new Region(path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Round(ClientRectangle, Radius))
            {
                using (var b = new SolidBrush(FillColor))
                    e.Graphics.FillPath(b, path);
                if (BorderColor.A > 0 && BorderWidth > 0)
                {
                    using (var pen = new Pen(BorderColor, BorderWidth))
                        e.Graphics.DrawPath(pen, path);
                }
            }
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
