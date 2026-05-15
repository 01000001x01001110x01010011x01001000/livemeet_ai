using System;
using System.IO;
using System.Windows;
using LiveMeetAI.AI;
using System.Threading.Tasks;
using System.Windows.Interop;
using LiveMeetAI.Audio;
using LiveMeetAI.STT;
using LiveMeetAI.Core;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Text;
using Newtonsoft.Json.Linq;

namespace LiveMeetAI.App
{
    public partial class MainWindow : Window
    {
        private AudioCaptureService? audioService;
        private ITranscriptionService? stt;
        private WebView2ChatController? chatController;
        private HotkeyManager? hotkeyManager;

        private bool isSystemRunning = false;
        private bool isMicRunning = false;
        private int pendingSttRequests = 0;

        private DateTime lastSpeechChunkTime = DateTime.MinValue;
        private StringBuilder transcriptBuffer = new StringBuilder();
        private readonly TimeSpan chunkDebounce = TimeSpan.FromSeconds(4);
        private readonly int minimumCharactersToSend = 5;

        private string _userDataFolder = Path.Combine(Path.GetTempPath(), "LiveMeetAI_WebView2");

        [DllImport("user32.dll")]
        private static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_S = 0x53;
        private const int VK_M = 0x4D;

        private DispatcherTimer? keyPollingTimer;
        private int currentHotkeyId = 0;

        public MainWindow()
        {
            InitializeComponent();
            FileLogger.Info("Application starting");

            var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            JObject? settings = null;
            if (File.Exists(settingsPath))
            {
                try { settings = JObject.Parse(File.ReadAllText(settingsPath)); }
                catch (Exception ex) { FileLogger.Error("Failed to parse settings.json: " + ex.Message); }
            }

            if (settings != null)
            {
                var provider = settings.Value<string>("TranscriptionProvider") ?? "Local";
                var localEndpoint = settings.Value<string>("LocalTranscriptionEndpoint") ?? "http://127.0.0.1:8080/inference";

                if (provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
                {
                    stt = new LocalWhisperTranscriptionService(localEndpoint);
                    stt.OnTranscriptChunk += OnTranscriptChunk;
                    FileLogger.Info("Local STT configured: " + localEndpoint);
                }

                var userDataDir = settings.Value<string>("UserDataDir");
                if (!string.IsNullOrWhiteSpace(userDataDir))
                    _userDataFolder = userDataDir;

                FileLogger.Info($"MainWindow: WebView2 UserDataFolder={_userDataFolder}");
            }

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var helper = new WindowInteropHelper(this);
                hotkeyManager = new HotkeyManager(helper.Handle);

                const uint MOD_CTRL = 0x0002;
                const uint MOD_SHIFT = 0x0004;
                const uint VK_S_KEY = 0x53;
                const uint VK_M_KEY = 0x4D;
                const uint VK_P_KEY = 0x50;
                const uint VK_Q_KEY = 0x51;

                hotkeyManager.RegisterHotKey(MOD_CTRL | MOD_SHIFT, VK_S_KEY);
                hotkeyManager.RegisterHotKey(MOD_CTRL | MOD_SHIFT, VK_M_KEY);
                hotkeyManager.RegisterHotKey(MOD_CTRL | MOD_SHIFT, VK_P_KEY);
                hotkeyManager.RegisterHotKey(MOD_CTRL | MOD_SHIFT, VK_Q_KEY);

                SetWindowDisplayAffinity(helper.Handle, WDA_EXCLUDEFROMCAPTURE);

                keyPollingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                keyPollingTimer.Tick += KeyPollingTimer_Tick;

                hotkeyManager.HotKeyPressed += id =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (id == 1) { if (!isSystemRunning) StartSystem(); currentHotkeyId = 1; keyPollingTimer?.Start(); }
                        else if (id == 2) { if (!isMicRunning) StartMic(); currentHotkeyId = 2; keyPollingTimer?.Start(); }
                        else if (id == 3) { StopAll(); }
                        else if (id == 4) { Task.Run(SimulateCopyAndSearch); }
                    });
                };

                FileLogger.Info("Hotkeys registered");

                // Initialize embedded ChatGPT browser
                chatController = new WebView2ChatController(ChatWebView, _userDataFolder, Dispatcher);
                await chatController.InitializeAsync();
            }
            catch (Exception ex)
            {
                FileLogger.Error("MainWindow_Loaded failed: " + ex.Message);
            }
        }

        private void KeyPollingTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                bool key = currentHotkeyId == 1
                    ? (GetAsyncKeyState(VK_S) & 0x8000) != 0
                    : (GetAsyncKeyState(VK_M) & 0x8000) != 0;

                if (!(ctrl && shift && key))
                {
                    keyPollingTimer?.Stop();
                    StopAll(flush: true);
                }
            }
            catch (Exception ex) { FileLogger.Error("Polling timer error: " + ex.Message); }
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                audioService?.Dispose();
                chatController?.Dispose();
                hotkeyManager?.Dispose();
                FileLogger.Info("Application shutting down");
            }
            catch (Exception ex) { FileLogger.Error("Shutdown error: " + ex.Message); }
        }

        private void AppendStatus(string text)
        {
            Dispatcher.Invoke(() =>
            {
                if (StatusBar != null)
                    StatusBar.Text = text.TrimEnd();
            });
        }

        private void OnTranscriptChunk(string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;

                FileLogger.Info("STT chunk received: " + (text.Length > 50 ? text[..50] + "..." : text));

                lock (transcriptBuffer)
                {
                    transcriptBuffer.Append(text);
                    transcriptBuffer.Append(' ');
                }

                lastSpeechChunkTime = DateTime.Now;

                Task.Run(async () =>
                {
                    await Task.Delay(chunkDebounce);
                    if ((DateTime.Now - lastSpeechChunkTime) < chunkDebounce) return;
                    if (isMicRunning || isSystemRunning) return;

                    string candidate;
                    lock (transcriptBuffer)
                    {
                        candidate = transcriptBuffer.ToString().Trim();
                    }

                    if (candidate.Length >= minimumCharactersToSend)
                    {
                        FileLogger.Info($"Debounce triggered. Sending: '{candidate}'");
                        await TrySendToChat(candidate);
                        lock (transcriptBuffer) { transcriptBuffer.Clear(); }
                    }
                });
            }
            catch (Exception ex) { FileLogger.Error("OnTranscriptChunk failed: " + ex.Message); }
        }

        private void FlushTranscript()
        {
            string candidate;
            lock (transcriptBuffer)
            {
                candidate = transcriptBuffer.ToString().Trim();
                transcriptBuffer.Clear();
            }

            if (!string.IsNullOrEmpty(candidate))
            {
                FileLogger.Info($"Flush triggered. Sending: '{candidate}'");
                Task.Run(() => TrySendToChat(candidate));
            }
        }

        private async Task TrySendToChat(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (chatController == null)
            {
                FileLogger.Warn("Chat controller not ready.");
                AppendStatus("Chat controller not ready.");
                return;
            }

            try
            {
                var sent = await chatController.SendMessageAsync(message);
                if (sent)
                {
                    FileLogger.Info("Sent to ChatGPT.");
                    AppendStatus($"Sent: {(message.Length > 80 ? message[..80] + "..." : message)}");
                }
                else
                {
                    FileLogger.Warn("Failed to send to ChatGPT.");
                    AppendStatus("Send failed — ChatGPT input not found. Are you logged in?");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Error("TrySendToChat failed: " + ex.Message);
                AppendStatus("Send error: " + ex.Message);
            }
        }

        private void StartSystem_Click(object sender, RoutedEventArgs e) { if (isSystemRunning) StopAll(); else StartSystem(); }

        private void StartSystem()
        {
            try
            {
                if (isSystemRunning) return;
                audioService = new AudioCaptureService();
                audioService.OnChunkReady += async (s, args) =>
                {
                    if (stt != null)
                    {
                        System.Threading.Interlocked.Increment(ref pendingSttRequests);
                        try { await stt.TranscribeChunkAsync(args.WavBytes); }
                        catch (Exception ex) { FileLogger.Error("STT error: " + ex.Message); }
                        finally { System.Threading.Interlocked.Decrement(ref pendingSttRequests); }
                    }
                };
                audioService.Start(CaptureSource.SystemLoopback);
                isSystemRunning = true;
                AppendStatus("System audio capture started.");
                FileLogger.Info("Started system capture");
            }
            catch (Exception ex)
            {
                FileLogger.Error("StartSystem failed: " + ex.Message);
                MessageBox.Show("Failed to start system capture: " + ex.Message);
            }
        }

        private void StartMic_Click(object sender, RoutedEventArgs e) { if (isMicRunning) StopAll(); else StartMic(); }

        private void StartMic()
        {
            try
            {
                if (isMicRunning) return;
                audioService = new AudioCaptureService();
                audioService.OnChunkReady += async (s, args) =>
                {
                    if (stt != null)
                    {
                        System.Threading.Interlocked.Increment(ref pendingSttRequests);
                        try { await stt.TranscribeChunkAsync(args.WavBytes); }
                        catch (Exception ex) { FileLogger.Error("STT error: " + ex.Message); }
                        finally { System.Threading.Interlocked.Decrement(ref pendingSttRequests); }
                    }
                };
                audioService.Start(CaptureSource.Microphone);
                isMicRunning = true;
                AppendStatus("Microphone capture started.");
                FileLogger.Info("Started microphone capture");
            }
            catch (Exception ex)
            {
                FileLogger.Error("StartMic failed: " + ex.Message);
                MessageBox.Show("Failed to start mic capture: " + ex.Message);
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e) { StopAll(); }

        private void StopAll(bool flush = false)
        {
            try
            {
                if (!isMicRunning && !isSystemRunning) return;
                audioService?.Stop();
                isMicRunning = isSystemRunning = false;
                AppendStatus("Capture stopped.");
                FileLogger.Info("Capture stopped");

                if (flush)
                {
                    Task.Run(async () =>
                    {
                        var start = DateTime.Now;
                        while (pendingSttRequests > 0 && (DateTime.Now - start).TotalSeconds < 5)
                            await Task.Delay(50);
                        if (pendingSttRequests > 0) FileLogger.Warn("StopAll: Timeout waiting for pending STT.");
                        FlushTranscript();
                    });
                }
            }
            catch (Exception ex) { FileLogger.Error("StopAll failed: " + ex.Message); }
        }

        private void OpenBrowser_Click(object sender, RoutedEventArgs e)
        {
            chatController?.NavigateToNewChat();
            AppendStatus("Navigated to new temporary chat.");
        }

        private void ScreenShare_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var screenShareWindow = new ScreenShareWindow();
                screenShareWindow.Show();
                FileLogger.Info("Screen Share window opened");
                AppendStatus("Screen Share window opened.");
            }
            catch (Exception ex)
            {
                FileLogger.Error("ScreenShare_Click failed: " + ex.Message);
                MessageBox.Show("Failed to open Screen Share: " + ex.Message);
            }
        }

        private async Task SimulateCopyAndSearch()
        {
            try
            {
                const byte VK_SHIFT_B = 0x10;
                const byte VK_C_B = 0x43;
                const byte VK_CTRL_B = 0x11;
                const int KEYEVENTF_KEYUP = 0x0002;

                // The user held Ctrl+Shift+C to trigger the hotkey.
                // Release Shift so the resulting keystroke is Ctrl+C (copy), not Ctrl+Shift+C.
                keybd_event(VK_SHIFT_B, 0, KEYEVENTF_KEYUP, 0);
                keybd_event(VK_C_B, 0, 0, 0);
                keybd_event(VK_C_B, 0, KEYEVENTF_KEYUP, 0);
                keybd_event(VK_CTRL_B, 0, KEYEVENTF_KEYUP, 0);

                await Task.Delay(150);

                string? text = null;
                Dispatcher.Invoke(() =>
                {
                    try { text = Clipboard.GetText(); }
                    catch (Exception ex) { FileLogger.Warn("Clipboard read failed: " + ex.Message); }
                });

                if (!string.IsNullOrWhiteSpace(text))
                {
                    FileLogger.Info($"Copy-search: '{(text.Length > 50 ? text[..50] + "..." : text)}'");
                    await TrySendToChat(text);
                }
                else
                {
                    AppendStatus("Copy-search: nothing selected.");
                }
            }
            catch (Exception ex) { FileLogger.Error("SimulateCopyAndSearch failed: " + ex.Message); }
        }
    }
}
