using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveMeetAI.ScreenShare
{
    /// <summary>
    /// Enumerates and manages visible windows on the desktop.
    /// </summary>
    public class WindowEnumerator
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public class WindowInfo
        {
            public IntPtr Handle { get; set; }
            public string Title { get; set; }
            public int ProcessId { get; set; }
            public string ProcessName { get; set; }
            public RECT Bounds { get; set; }
            public bool IsVisible { get; set; }
        }

        /// <summary>
        /// Get all visible windows on the desktop.
        /// </summary>
        public static List<WindowInfo> GetVisibleWindows()
        {
            var windows = new List<WindowInfo>();
            var processCache = new Dictionary<int, string>();

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true;

                var titleSb = new StringBuilder(256);
                GetWindowText(hWnd, titleSb, 256);
                var title = titleSb.ToString();

                if (string.IsNullOrWhiteSpace(title))
                    return true; // Skip windows with no title

                GetWindowThreadProcessId(hWnd, out uint processId);
                GetWindowRect(hWnd, out RECT rect);

                // Skip if window is off-screen or invalid
                if (rect.Width <= 0 || rect.Height <= 0)
                    return true;

                string processName = "Unknown";
                if (!processCache.TryGetValue((int)processId, out processName))
                {
                    try
                    {
                        var proc = Process.GetProcessById((int)processId);
                        processName = proc.ProcessName;
                        processCache[(int)processId] = processName;
                    }
                    catch { }
                }

                windows.Add(new WindowInfo
                {
                    Handle = hWnd,
                    Title = title,
                    ProcessId = (int)processId,
                    ProcessName = processName,
                    Bounds = rect,
                    IsVisible = true
                });

                return true;
            }, IntPtr.Zero);

            return windows;
        }

        /// <summary>
        /// Get list of running processes with their main window handles.
        /// </summary>
        public static List<(string ProcessName, int ProcessId)> GetRunningProcesses()
        {
            var result = new List<(string, int)>();
            var seen = new HashSet<int>();

            foreach (var proc in Process.GetProcesses())
            {
                if (seen.Contains(proc.Id))
                    continue;

                seen.Add(proc.Id);

                try
                {
                    if (proc.MainWindowHandle != IntPtr.Zero && IsWindowVisible(proc.MainWindowHandle))
                    {
                        result.Add((proc.ProcessName, proc.Id));
                    }
                }
                catch { }
            }

            return result;
        }
    }
}
