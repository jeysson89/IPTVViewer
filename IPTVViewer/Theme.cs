using System.Drawing;
using System.Windows.Forms;

namespace IPTVViewer
{
    internal static class Theme
    {
        public static bool IsLight { get; private set; }

        public static Color Bg;
        public static Color Panel;
        public static Color PanelAlt;
        public static Color PanelHover;
        public static Color Border;
        public static Color Text;
        public static Color TextDim;
        public static Color Accent;
        public static Color AccentHover;
        public static Color Selection;

        public static readonly Font FontRegular = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        public static readonly Font FontSmall = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        public static readonly Font FontBold = new Font("Segoe UI", 10f, FontStyle.Bold);

        static Theme()
        {
            SetMode(false);
        }

        public static void SetMode(bool light)
        {
            IsLight = light;
            if (light)
            {
                Bg = Color.FromArgb(244, 245, 247);
                Panel = Color.FromArgb(255, 255, 255);
                PanelAlt = Color.FromArgb(237, 239, 243);
                PanelHover = Color.FromArgb(227, 231, 238);
                Border = Color.FromArgb(213, 218, 227);
                Text = Color.FromArgb(26, 28, 34);
                TextDim = Color.FromArgb(106, 114, 128);
                Accent = Color.FromArgb(225, 29, 42);
                AccentHover = Color.FromArgb(200, 24, 36);
                Selection = Color.FromArgb(253, 236, 238);
            }
            else
            {
                Bg = Color.FromArgb(14, 15, 19);
                Panel = Color.FromArgb(21, 23, 30);
                PanelAlt = Color.FromArgb(30, 33, 42);
                PanelHover = Color.FromArgb(38, 42, 54);
                Border = Color.FromArgb(45, 49, 61);
                Text = Color.FromArgb(236, 238, 243);
                TextDim = Color.FromArgb(142, 148, 162);
                Accent = Color.FromArgb(225, 29, 42);
                AccentHover = Color.FromArgb(245, 62, 74);
                Selection = Color.FromArgb(40, 44, 56);
            }
        }

        public static Button CreateButton(string text, bool primary = false)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : PanelAlt,
                ForeColor = primary ? Color.White : Text,
                Font = FontRegular,
                Height = 34,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                TabStop = true
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = primary ? AccentHover : PanelHover;
            b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(190, 20, 32) : PanelHover;
            return b;
        }

        public static Label CreateLabel(string text, bool dim = false, float size = 9.5f, bool bold = false)
        {
            return new Label
            {
                Text = text,
                ForeColor = dim ? TextDim : Text,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                AutoSize = true
            };
        }

        public static void StyleTextBox(TextBox tb)
        {
            tb.BackColor = PanelAlt;
            tb.ForeColor = Text;
            tb.BorderStyle = BorderStyle.FixedSingle;
            tb.Font = FontRegular;
        }

        public static void StyleCombo(ComboBox cb)
        {
            cb.BackColor = PanelAlt;
            cb.ForeColor = Text;
            cb.FlatStyle = FlatStyle.Flat;
            cb.Font = FontRegular;
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
        }
    }
}
