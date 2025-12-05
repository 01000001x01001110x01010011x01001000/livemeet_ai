using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LiveMeetAI.App
{
    public class HotkeyManager : IDisposable
    {
        private readonly IntPtr windowHandle;
        private HwndSource? source;
        private int currentId = 0;
        public event Action<int>? HotKeyPressed;

        public HotkeyManager(IntPtr hwnd)
        {
            windowHandle = hwnd;
            source = HwndSource.FromHwnd(hwnd);
            source.AddHook(HwndHook);
        }

        public int RegisterHotKey(uint modifiers, uint vk)
        {
            currentId++;
            if (!RegisterHotKey(windowHandle, currentId, modifiers, vk))
            {
                throw new InvalidOperationException("Could not register hotkey.");
            }
            return currentId;
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                HotKeyPressed?.Invoke(id);
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            for (int i = 1; i <= currentId; i++)
            {
                UnregisterHotKey(windowHandle, i);
            }
            if (source != null)
            {
                source.RemoveHook(HwndHook);
                source = null;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
