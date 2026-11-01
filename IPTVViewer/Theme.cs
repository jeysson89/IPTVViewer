using System.Drawing;
using System.Windows.Forms;

namespace IPTVViewer
{
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(14, 15, 19);
        public static readonly Color Panel = Color.FromArgb(21, 23, 30);
        public static readonly Color PanelAlt = Color.FromArgb(30, 33, 42);
        public static readonly Color PanelHover = Color.FromArgb(38, 42, 54);
        public static readonly Color Border = Color.FromArgb(45, 49, 61);
        public static readonly Color Text = Color.FromArgb(236, 238, 243);
        public static readonly Color TextDim = Color.FromArgb(142, 148, 162);
        public static readonly Color Accent = Color.FromArgb(225, 29, 42);
        public static readonly Color AccentHover = Color.FromArgb(245, 62, 74);
        public static readonly Color Selection = Color.FromArgb(40, 44, 56);

        public static Font FontRegular = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        public static Font FontSmall = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        public static Font FontBold = new Font("Segoe UI", 10f, FontStyle.Bold);

        public static Button CreateButton(string text, bool primary = false)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : PanelAlt,
                ForeColor = Text,
                Font = FontRegular,
                Height = 34,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                TabStop = true
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = primary ? AccentHover : PanelHover;
            b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(190, 20, 32) : Color.FromArgb(48, 52, 66);
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
