using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LiveMeetAI.App;

namespace LiveMeetAI.ScreenShare
{
    /// <summary>
    /// Captures screen with ability to filter/hide specific windows.
    /// </summary>
    public class ScreenCaptureService : IDisposable
    {
        private const uint GWL_EXSTYLE = 20;
        private const uint WS_EX_TRANSPARENT = 0x00000020;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out WindowEnumerator.RECT rect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private Bitmap? captureBuffer;
        private readonly int screenWidth;
        private readonly int screenHeight;
        private readonly HashSet<int> hiddenProcessIds = new();
        private CancellationTokenSource? captureLoopCts;
        private Task? captureLoopTask;
        private readonly object hiddenWindowsLock = new();

        public event EventHandler<ScreenCaptureEventArgs>? OnFrameReady;

        public ScreenCaptureService()
        {
            var screen = Screen.PrimaryScreen;
            screenWidth = screen.Bounds.Width;
            screenHeight = screen.Bounds.Height;
            captureBuffer = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppRgb);
        }

        /// <summary>
        /// Add a process ID to the hidden windows list.
        /// </summary>
        public void AddHiddenProcess(int processId)
        {
            lock (hiddenWindowsLock)
            {
                hiddenProcessIds.Add(processId);
            }
        }

        /// <summary>
        /// Remove a process ID from the hidden windows list.
        /// </summary>
        public void RemoveHiddenProcess(int processId)
        {
            lock (hiddenWindowsLock)
            {
                hiddenProcessIds.Remove(processId);
            }
        }

        /// <summary>
        /// Clear all hidden processes.
        /// </summary>
        public void ClearHiddenProcesses()
        {
            lock (hiddenWindowsLock)
            {
                hiddenProcessIds.Clear();
            }
        }

        /// <summary>
        /// Get list of currently hidden process IDs.
        /// </summary>
        public List<int> GetHiddenProcessIds()
        {
            lock (hiddenWindowsLock)
            {
                return new List<int>(hiddenProcessIds);
            }
        }

        /// <summary>
        /// Start continuous screen capture loop.
        /// </summary>
        public void StartCapture()
        {
            if (captureLoopTask != null && !captureLoopTask.IsCompleted)
                return;

            captureLoopCts = new CancellationTokenSource();
            captureLoopTask = CaptureLoopAsync(captureLoopCts.Token);
        }

        /// <summary>
        /// Stop screen capture loop.
        /// </summary>
        public void StopCapture()
        {
            captureLoopCts?.Cancel();
            try { captureLoopTask?.Wait(5000); } catch { }
        }

        private async Task CaptureLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var frame = CaptureScreen();
                        OnFrameReady?.Invoke(this, new ScreenCaptureEventArgs { Frame = frame });
                        await Task.Delay(33, ct); // ~30 FPS
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Error($"ScreenCaptureService: Capture error: {ex.Message}");
                        await Task.Delay(100, ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Capture current screen, excluding hidden windows.
        /// </summary>
        private Bitmap? CaptureScreen()
        {
            if (captureBuffer == null)
                return null;

            IntPtr screenDC = IntPtr.Zero;
            IntPtr memDC = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;

            try
            {
                // Get desktop DC
                screenDC = GetDC(IntPtr.Zero);
                if (screenDC == IntPtr.Zero)
                    return null;

                // Create memory DC
                memDC = WindowsInterop.CreateCompatibleDC(screenDC);
                if (memDC == IntPtr.Zero)
                    return null;

                // Create compatible bitmap
                hBitmap = WindowsInterop.CreateCompatibleBitmap(screenDC, screenWidth, screenHeight);
                if (hBitmap == IntPtr.Zero)
                    return null;

                // Select bitmap into memory DC
                var oldBitmap = WindowsInterop.SelectObject(memDC, hBitmap);

                // Copy screen to memory DC
                BitBlt(memDC, 0, 0, screenWidth, screenHeight, screenDC, 0, 0, 0xCC0020); // SRCCOPY

                // Get hidden process list
                List<int> hidden;
                lock (hiddenWindowsLock)
                {
                    hidden = new List<int>(hiddenProcessIds);
                }

                // Paint over hidden windows with black
                if (hidden.Count > 0)
                {
                    var windows = WindowEnumerator.GetVisibleWindows();
                    var brush = WindowsInterop.CreateSolidBrush(0x000000); // Black

                    foreach (var win in windows)
                    {
                        if (hidden.Contains(win.ProcessId) && IsWindowVisible(win.Handle))
                        {
                            var rect = win.Bounds;
                            WindowsInterop.FillRect(memDC, ref rect, brush);
                        }
                    }

                    WindowsInterop.DeleteObject(brush);
                }

                // Convert to Bitmap
                var result = Image.FromHbitmap(hBitmap);

                // Cleanup
                WindowsInterop.SelectObject(memDC, oldBitmap);
                return (Bitmap)result;
            }
            catch (Exception ex)
            {
                FileLogger.Error($"ScreenCaptureService: CaptureScreen error: {ex.Message}");
                return null;
            }
            finally
            {
                // Cleanup GDI handles
                if (hBitmap != IntPtr.Zero)
                    WindowsInterop.DeleteObject(hBitmap);
                if (memDC != IntPtr.Zero)
                    WindowsInterop.DeleteDC(memDC);
                if (screenDC != IntPtr.Zero)
                    ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        public void Dispose()
        {
            StopCapture();
            captureLoopCts?.Dispose();
            captureBuffer?.Dispose();
        }
    }

    public class ScreenCaptureEventArgs : EventArgs
    {
        public Bitmap? Frame { get; set; }
    }

    /// <summary>
    /// GDI32 interop helpers.
    /// </summary>
    internal static class WindowsInterop
    {
        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        internal static extern IntPtr CreateSolidBrush(uint crColor);

        [DllImport("user32.dll")]
        internal static extern bool FillRect(IntPtr hDC, ref WindowEnumerator.RECT lprc, IntPtr hbr);
    }
}
