using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LiveMeetAI.ScreenShare;

namespace LiveMeetAI.App
{
    public partial class ScreenShareWindow : Window
    {
        private ScreenCaptureService? captureService;
        private List<WindowEnumerator.WindowInfo> availableWindows = new();
        private Dictionary<int, string> hiddenProcesses = new();

        public ScreenShareWindow()
        {
            InitializeComponent();
            RefreshProcessList();
        }

        private void RefreshProcessList()
        {
            try
            {
                ProcessComboBox.Items.Clear();
                availableWindows = WindowEnumerator.GetVisibleWindows();

                foreach (var win in availableWindows)
                {
                    var item = $"{win.ProcessName} - {win.Title}";
                    ProcessComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = item,
                        Tag = win.ProcessId,
                        ToolTip = $"PID: {win.ProcessId}"
                    });
                }

                if (ProcessComboBox.Items.Count > 0)
                    ProcessComboBox.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                FileLogger.Error($"ScreenShareWindow: RefreshProcessList error: {ex.Message}");
            }
        }

        private void StartCapture_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (captureService == null)
                {
                    captureService = new ScreenCaptureService();
                    captureService.OnFrameReady += CaptureService_OnFrameReady;

                    // Apply any existing hidden processes
                    foreach (var pid in hiddenProcesses.Keys)
                    {
                        captureService.AddHiddenProcess(pid);
                    }
                }

                captureService.StartCapture();
                StartCaptureBtn.IsEnabled = false;
                StopCaptureBtn.IsEnabled = true;
                NoSignalText.Visibility = Visibility.Collapsed;
                FileLogger.Info("ScreenShareWindow: Capture started");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start capture: {ex.Message}");
                FileLogger.Error($"ScreenShareWindow: StartCapture error: {ex.Message}");
            }
        }

        private void StopCapture_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                captureService?.StopCapture();
                StartCaptureBtn.IsEnabled = true;
                StopCaptureBtn.IsEnabled = false;
                NoSignalText.Text = "Capture stopped.";
                NoSignalText.Visibility = Visibility.Visible;
                FileLogger.Info("ScreenShareWindow: Capture stopped");
            }
            catch (Exception ex)
            {
                FileLogger.Error($"ScreenShareWindow: StopCapture error: {ex.Message}");
            }
        }

        private void AddHide_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ProcessComboBox.SelectedItem is not ComboBoxItem item)
                    return;

                var processId = (int)item.Tag;
                var processName = item.Content?.ToString() ?? "Unknown";

                if (hiddenProcesses.ContainsKey(processId))
                {
                    MessageBox.Show("This app is already hidden.");
                    return;
                }

                // Add to hidden list
                hiddenProcesses[processId] = processName;
                captureService?.AddHiddenProcess(processId);

                // Update UI
                UpdateHiddenAppsList();
                FileLogger.Info($"ScreenShareWindow: Hidden process {processName} (PID: {processId})");
            }
            catch (Exception ex)
            {
                FileLogger.Error($"ScreenShareWindow: AddHide error: {ex.Message}");
            }
        }

        private void UpdateHiddenAppsList()
        {
            HiddenAppsListBox.Items.Clear();

            foreach (var kvp in hiddenProcesses)
            {
                var btn = new Button
                {
                    Content = $"× {kvp.Value}",
                    Padding = new Thickness(8, 3, 8, 3),
                    Margin = new Thickness(3, 3, 3, 3),
                    Background = System.Windows.Media.Brushes.Red,
                    Foreground = System.Windows.Media.Brushes.White,
                    Tag = kvp.Key,
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                btn.Click += (s, e) =>
                {
                    var pid = (int)((Button)s).Tag;
                    if (hiddenProcesses.Remove(pid))
                    {
                        captureService?.RemoveHiddenProcess(pid);
                        UpdateHiddenAppsList();
                        FileLogger.Info($"ScreenShareWindow: Unhidden process PID {pid}");
                    }
                };
                HiddenAppsListBox.Items.Add(btn);
            }

            if (HiddenAppsListBox.Items.Count == 0)
            {
                HiddenAppsListBox.Items.Add(new TextBlock
                {
                    Text = "No apps hidden",
                    Foreground = System.Windows.Media.Brushes.Gray,
                    Margin = new Thickness(10, 10, 10, 10)
                });
            }
        }

        private void CaptureService_OnFrameReady(object? sender, ScreenCaptureEventArgs e)
        {
            try
            {
                if (e.Frame == null)
                    return;

                // Convert Bitmap to BitmapImage for display
                using (var ms = new System.IO.MemoryStream())
                {
                    e.Frame.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                    ms.Seek(0, System.IO.SeekOrigin.Begin);

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.StreamSource = ms;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    Dispatcher.Invoke(() =>
                    {
                        ScreenImage.Source = bitmap;
                    });
                }
            }
            catch (Exception ex)
            {
                FileLogger.Error($"ScreenShareWindow: Frame rendering error: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            captureService?.StopCapture();
            captureService?.Dispose();
            base.OnClosed(e);
        }
    }
}
