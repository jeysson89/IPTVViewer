using System;
using System.Runtime.InteropServices;

namespace IPTVViewer
{
    /// <summary>
    /// Низкоуровневый перехват мыши: окно libvlc перекрывает VideoView и
    /// не пропускает клики к WinForms, поэтому двойной клик по видео
    /// ловим глобально. Установленный хук видит и реальные, и
    /// программно-введённые события мыши, окно VLC при этом не трогаем.
    /// </summary>
    public class LowLevelMouseHook : IDisposable
    {
        public event Action<System.Drawing.Point> LeftDoubleClicked;

        /// <summary>
        /// Вызывается при прокрутке колеса; обработчик решает — обработать
        /// прокрутку сам (true, событие "глотается") или пропустить дальше.
        /// </summary>
        public Func<System.Drawing.Point, int, bool> WheelHandler;

        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSEWHEEL = 0x020A;

        private delegate IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, MouseProc proc, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string name);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT Pt;
            public uint MouseData, Flags, Time;
            public IntPtr ExtraInfo;
        }

        private IntPtr _hook;
        private MouseProc _proc;
        private int _lastTick;
        private System.Drawing.Point _lastPt;

        public LowLevelMouseHook()
        {
            _proc = OnMouse;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        }

        private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && (int)wParam == WM_MOUSEWHEEL)
            {
                var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                var pt = new System.Drawing.Point(info.Pt.X, info.Pt.Y);
                // угол прокрутки лежит в старшем слове MouseData
                int delta = unchecked((short)((info.MouseData >> 16) & 0xFFFF));
                var handler = WheelHandler;
                if (handler != null && handler(pt, delta))
                    return (IntPtr)1; // прокрутка обработана — не пускаем дальше
                return CallNextHookEx(_hook, code, wParam, lParam);
            }

            if (code >= 0 && (int)wParam == WM_LBUTTONDOWN)
            {
                var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                var pt = new System.Drawing.Point(info.Pt.X, info.Pt.Y);
                var tick = Environment.TickCount;
                if (_lastTick != 0)
                {
                    var delta = unchecked(tick - _lastTick);
                    if (delta >= 0 && delta <= System.Windows.Forms.SystemInformation.DoubleClickTime &&
                        Math.Abs(pt.X - _lastPt.X) < 8 && Math.Abs(pt.Y - _lastPt.Y) < 8)
                    {
                        _lastTick = 0;
                        var handler = LeftDoubleClicked;
                        if (handler != null)
                            handler(pt);
                    }
                    else
                    {
                        _lastTick = tick;
                        _lastPt = pt;
                    }
                }
                else
                {
                    _lastTick = tick;
                    _lastPt = pt;
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}
