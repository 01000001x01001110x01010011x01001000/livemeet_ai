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
        // Core components
        private AudioCaptureService? audioService;
        private ITranscriptionService? stt;
        private TranscriptManager transcriptManager;
        private BraveChatController? braveController;
        private HotkeyManager? hotkeyManager;

        // state
        // state
        private bool isSystemRunning = false;
        private bool isMicRunning = false;
        private int pendingSttRequests = 0;

        // debounce
        private DateTime lastSpeechChunkTime = DateTime.MinValue;

        private System.Text.StringBuilder transcriptBuffer = new System.Text.StringBuilder();
        private readonly TimeSpan chunkDebounce = TimeSpan.FromSeconds(4);
        private readonly int minimumCharactersToSend = 5;

        // P/Invoke
        [DllImport("user32.dll")]
        private static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_S = 0x53; // 'S'
        private const int VK_M = 0x4D; // 'M'

        private DispatcherTimer? keyPollingTimer;
        private int currentHotkeyId = 0; // 1=System, 2=Mic

        public MainWindow()
        {
            InitializeComponent();

            FileLogger.Info("Application starting");

            transcriptManager = new TranscriptManager();
            FileLogger.Debug("TranscriptManager created");

            // Load settings.json if present
            var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            JObject? settings = null;
            if (File.Exists(settingsPath))
            {
                try
                {
                    settings = JObject.Parse(File.ReadAllText(settingsPath));
                }
                catch (Exception ex)
                {
                    FileLogger.Error("Failed to parse settings.json: " + ex.Message);
                }
            }
            else
            {
                FileLogger.Warn("settings.json not found; configure it to enable STT and Brave features.");
            }

            // Configure STT and Brave controller
            if (settings != null)
            {
                var provider = settings.Value<string>("TranscriptionProvider") ?? "Local";
                var localEndpoint = settings.Value<string>("LocalTranscriptionEndpoint") ?? "http://127.0.0.1:8080/inference";
                var apiKey = settings.Value<string>("OpenAIApiKey");

                try
                {
                    if (provider.Equals("Local", StringComparison.OrdinalIgnoreCase))
                    {
                        stt = new LocalWhisperTranscriptionService(localEndpoint);
                        stt.OnTranscriptChunk += OnTranscriptChunk;
                        FileLogger.Info("Local STT configured: " + localEndpoint);
                    }
                    else
                    {
                        FileLogger.Warn("TranscriptionProvider not recognized; STT disabled.");
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Error("Failed to initialize STT: " + ex.Message);
                }

                // --- Brave controller config (fixed variable names and single initialization) ---
                var bravePath = settings.Value<string>("BravePath")
                                ?? @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe";

                // prefer an explicit profile folder; fall back to a sensible local-appdata path
                var userDataDir = settings.Value<string>("UserDataDir")
                                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                  "BraveSoftware", "Brave-Browser", "User Data", "LiveMeetProfile");

                // optional: path to the directory that contains chromedriver.exe (exact folder)
                var chromeDriverDir = settings.Value<string>("ChromeDriverDir");

                FileLogger.Info($"MainWindow: BravePath={bravePath}");
                FileLogger.Info($"MainWindow: UserDataDir={userDataDir}");
                FileLogger.Info($"MainWindow: ChromeDriverDir={(chromeDriverDir ?? "<null>")}");

                // Only create Brave controller if bravePath and userDataDir look OK
                if (!string.IsNullOrWhiteSpace(bravePath) && !string.IsNullOrWhiteSpace(userDataDir))
                {
                    try
                    {
                        braveController = new BraveChatController(bravePath, userDataDir, chromeDriverDir);
                        FileLogger.Info("Brave controller configured.");
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Error("Failed to initialize Brave controller: " + ex);
                        braveController = null;
                    }

                    // diagnostic: attempt to start the browser now (will log useful diagnostic info)
                    try
                    {
                        FileLogger.Info("TEST: forcing Brave startup for diagnostics...");
                        braveController?.EnsureWindowStarted();
                        FileLogger.Info("TEST: EnsureWindowStarted() returned without exception.");
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Error("TEST: EnsureWindowStarted() threw: " + ex);
                    }
                }
                else
                {
                    FileLogger.Warn("Brave controller not configured because BravePath or UserDataDir are missing.");
                }
                // --- end Brave block ---
            }

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var helper = new WindowInteropHelper(this);
                hotkeyManager = new HotkeyManager(helper.Handle);

                const uint MOD_CTRL = 0x0002;
                const uint MOD_SHIFT = 0x0004;
                const uint VK_S = 0x53; // 'S'
                const uint VK_M = 0x4D; // 'M'
                const uint VK_P = 0x50; // 'P'

                // register - map ids 1,2,3
                hotkeyManager.RegisterHotKey(MOD_CTRL | MOD_SHIFT, VK_S); // id = 1
                hotkeyManager.RegisterHotKey(MOD_CTRL | MOD_SHIFT, VK_M); // id = 2
                hotkeyManager.RegisterHotKey(MOD_CTRL | MOD_SHIFT, VK_P); // id = 3

                // Hide from screen capture
                SetWindowDisplayAffinity(helper.Handle, WDA_EXCLUDEFROMCAPTURE);

                // Setup PTT polling timer
                keyPollingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                keyPollingTimer.Tick += KeyPollingTimer_Tick;

                hotkeyManager.HotKeyPressed += id =>
                {
                    // FileLogger.Debug("Hotkey pressed id=" + id); // spammy if held
                    if (id == 1) // System
                    {
                        Dispatcher.Invoke(() => 
                        {
                            if (!isSystemRunning) StartSystem();
                            currentHotkeyId = 1;
                            if (keyPollingTimer?.IsEnabled == false) keyPollingTimer.Start();
                        });
                    }
                    else if (id == 2) // Mic
                    {
                        Dispatcher.Invoke(() => 
                        {
                            if (!isMicRunning) StartMic();
                            currentHotkeyId = 2;
                            if (keyPollingTimer?.IsEnabled == false) keyPollingTimer.Start();
                        });
                    }
                    else if (id == 3) // Stop (Manual Panic)
                    {
                        Dispatcher.Invoke(() => StopAll());
                    }
                };

                FileLogger.Info("Hotkeys registered");
            }
            catch (Exception ex)
            {
                FileLogger.Warn("Hotkey setup failed: " + ex.Message);
            }
        }

        private void KeyPollingTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                
                bool key = false;
                if (currentHotkeyId == 1) key = (GetAsyncKeyState(VK_S) & 0x8000) != 0;
                else if (currentHotkeyId == 2) key = (GetAsyncKeyState(VK_M) & 0x8000) != 0;

                // Check if released (any modification key or the main key)
                bool isHeld = ctrl && shift && key;

                if (!isHeld)
                {
                    keyPollingTimer?.Stop();
                    StopAll(flush: true);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Error("Polling timer error: " + ex.Message);
            }
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                audioService?.Dispose();
                stt = null;
                braveController?.Dispose();
                hotkeyManager?.Dispose();
                FileLogger.Info("Application shutting down");
            }
            catch (Exception ex)
            {
                FileLogger.Error("Error during shutdown: " + ex.Message);
            }
        }

        private void OnTranscriptChunk(string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return;

                transcriptManager.Add(text);
                FileLogger.Info("STT chunk received: " + (text.Length > 50 ? text.Substring(0, 50) + "..." : text));

                Dispatcher.Invoke(() =>
                {
                    TranscriptBox?.AppendText($"{DateTime.Now:HH:mm:ss}  {text}\n");
                    TranscriptBox?.ScrollToEnd();
                });

                // Aggregate text
                lock (transcriptBuffer) 
                {
                    transcriptBuffer.Append(text);
                    transcriptBuffer.Append(" ");
                }
                
                lastSpeechChunkTime = DateTime.Now;

                Task.Run(async () =>
                {
                    await Task.Delay(chunkDebounce);
                    
                    // Check if enough time passed since ANY chunk arrived
                    var diff = DateTime.Now - lastSpeechChunkTime;
                    if (diff >= chunkDebounce)
                    {
                        // If user is still holding the talk key (active capture), DO NOT auto-send.
                        // We wait for them to release the key, which calls StopAll(flush: true).
                        if (isMicRunning || isSystemRunning) return;

                        string candidate = "";
                        lock (transcriptBuffer)
                        {
                            candidate = transcriptBuffer.ToString().Trim();
                        }
                        
                        if (!string.IsNullOrEmpty(candidate) && candidate.Length >= minimumCharactersToSend)
                        {
                            // If it's substantial, send it and clear buffer
                            FileLogger.Info($"Debounce triggered (Buffer). Sending: '{candidate}'");
                            
                            TrySendToBraveChat(candidate);
                            
                            lock (transcriptBuffer)
                            {
                                transcriptBuffer.Clear();
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                FileLogger.Error("OnTranscriptChunk failed: " + ex.Message);
            }
        }

        private void FlushTranscript()
        {
            try
            {
                string candidate = "";
                lock (transcriptBuffer)
                {
                    candidate = transcriptBuffer.ToString().Trim();
                    transcriptBuffer.Clear(); // Clear immediately to prevent double send
                }

                if (!string.IsNullOrEmpty(candidate))
                {
                    FileLogger.Info($"Flush triggered. Sending: '{candidate}'");
                    TrySendToBraveChat(candidate);
                }
            }
            catch (Exception ex)
            {
                FileLogger.Error("FlushTranscript failed: " + ex.Message);
            }
        }

        private void TrySendToBraveChat(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (braveController == null)
            {
                FileLogger.Warn("Brave controller not configured.");
                Dispatcher.Invoke(() =>
                {
                    AiBox?.AppendText($"(Brave disabled) Would send: {message}\n\n");
                    AiBox?.ScrollToEnd();
                });
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    braveController.SendMessageToChatGpt(message);
                    FileLogger.Info("Sent to ChatGPT via Brave.");
                    Dispatcher.Invoke(() =>
                    {
                        AiBox?.AppendText($"Sent to ChatGPT: {message}\n\n");
                        AiBox?.ScrollToEnd();
                    });
                }
                catch (Exception ex)
                {
                    FileLogger.Error("TrySendToBraveChat failed: " + ex.Message);
                    Dispatcher.Invoke(() =>
                    {
                        AiBox?.AppendText($"Failed sending to ChatGPT: {ex.Message}\n\n");
                        AiBox?.ScrollToEnd();
                    });
                }
            });
        }

        private void StartSystem_Click(object sender, RoutedEventArgs? e)
        {
            if (isSystemRunning) StopAll();
            else StartSystem();
        }

        private void StartSystem()
        {
            try
            {
                if (isSystemRunning) return;

                audioService = new AudioCaptureService();
                audioService.OnChunkReady += async (s, args) =>
                {
                    // FileLogger.Debug("Audio chunk ready (system)"); // reduced log spam
                    if (stt != null)
                    {
                        try 
                        { 
                            System.Threading.Interlocked.Increment(ref pendingSttRequests);
                            await stt.TranscribeChunkAsync(args.WavBytes); 
                        }
                        catch (Exception ex) { FileLogger.Error("STT error: " + ex.Message); }
                        finally { System.Threading.Interlocked.Decrement(ref pendingSttRequests); }
                    }
                };

                audioService.Start(CaptureSource.SystemLoopback);
                isSystemRunning = true;
                AiBox?.AppendText("Started system capture (waiting for release...)\n");
                FileLogger.Info("Started system capture");
            }
            catch (Exception ex)
            {
                FileLogger.Error("StartSystem failed: " + ex.Message);
                MessageBox.Show("Failed to start system capture: " + ex.Message);
            }
        }

        private void StartMic_Click(object sender, RoutedEventArgs? e)
        {
            if (isMicRunning) StopAll();
            else StartMic();
        }

        private void StartMic()
        {
            try
            {
                if (isMicRunning) return;

                audioService = new AudioCaptureService();
                audioService.OnChunkReady += async (s, args) =>
                {
                    // FileLogger.Debug("Audio chunk ready (mic)");
                    if (stt != null)
                    {
                        try 
                        { 
                            System.Threading.Interlocked.Increment(ref pendingSttRequests);
                            await stt.TranscribeChunkAsync(args.WavBytes); 
                        }
                        catch (Exception ex) { FileLogger.Error("STT error: " + ex.Message); }
                        finally { System.Threading.Interlocked.Decrement(ref pendingSttRequests); }
                    }
                };

                audioService.Start(CaptureSource.Microphone);
                isMicRunning = true;
                AiBox?.AppendText("Started microphone capture (waiting for release...)\n");
                FileLogger.Info("Started microphone capture");
            }
            catch (Exception ex)
            {
                FileLogger.Error("StartMic failed: " + ex.Message);
                MessageBox.Show("Failed to start mic capture: " + ex.Message);
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs? e)
        {
            StopAll();
        }

        private void StopAll(bool flush = false)
        {
            try
            {
                if (!isMicRunning && !isSystemRunning) return;

                audioService?.Stop();
                isMicRunning = isSystemRunning = false;
                AiBox?.AppendText("Capture stopped\n");
                FileLogger.Info("Capture stopped");

                if (flush)
                {
                    // Wait for pending STT (e.g. the last chunk we just flushed in Stop())
                    // Run logic in background to avoid blocking UI
                    Task.Run(async () =>
                    {
                        var start = DateTime.Now;
                        while (pendingSttRequests > 0 && (DateTime.Now - start).TotalSeconds < 5)
                        {
                            await Task.Delay(50);
                        }
                        if (pendingSttRequests > 0) FileLogger.Warn("StopAll: Timeout waiting for pending STT.");
                        
                        FlushTranscript();
                    });
                }
            }
            catch (Exception ex)
            {
                FileLogger.Error("StopAll failed: " + ex.Message);
            }
        }

        private async void Ask_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var q = AskBox.Text?.Trim();
                if (string.IsNullOrEmpty(q)) return;

                if (braveController != null)
                {
                    await Task.Run(() => braveController.SendMessageToChatGpt(q));
                    AiBox?.AppendText($"Q: {q}\nSent to ChatGPT\n\n");
                }
                else
                {
                    AiBox?.AppendText($"Q: {q}\nNo Brave controller configured.\n\n");
                }

                AskBox.Clear();
            }
            catch (Exception ex)
            {
                FileLogger.Error("Ask_Click failed: " + ex.Message);
                MessageBox.Show("Ask failed: " + ex.Message);
            }
        }

        private void OpenBrowser_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (braveController != null)
                {
                    Task.Run(() => 
                    {
                        try
                        {
                            braveController.RestartBrowser();
                            Dispatcher.Invoke(() => AiBox?.AppendText("Browser restarted/opened manually.\n"));
                        }
                        catch (Exception ex)
                        {
                             Dispatcher.Invoke(() => MessageBox.Show("Failed to open browser: " + ex.Message));
                        }
                    });
                }
                else
                {
                    MessageBox.Show("Brave controller is not configured (check settings.json).");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error opening browser: " + ex.Message);
            }
        }
    }
}
