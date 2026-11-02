using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace IPTVViewer
{
    /// <summary>
    /// Иконка приложения (буквы NL): единый источник для всех окон.
    /// Берётся из exe-ресурса, при его отсутствии — из exe-файла.
    /// </summary>
    public static class AppIcon
    {
        private static readonly object Sync = new object();
        private static Icon _icon;

        public static Icon Get()
        {
            lock (Sync)
            {
                if (_icon != null)
                    return (Icon)_icon.Clone();

                try
                {
                    var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("IPTVViewer.app.ico");
                    if (stream != null && stream.Length > 0)
                        using (stream)
                            _icon = new Icon(stream);
                }
                catch { }

                if (_icon == null)
                {
                    try { _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                    catch { }
                }

                return _icon != null ? (Icon)_icon.Clone() : null;
            }
        }
    }
}
