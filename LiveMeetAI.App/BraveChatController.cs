// BraveChatController.cs
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using LiveMeetAI.App; // for FileLogger

namespace LiveMeetAI.AI
{
    public class BraveChatController : IDisposable
    {
        private ChromeDriver? driver;
        private ChromeDriverService? service;
        private readonly string bravePath;
        private readonly string userDataDir;
        private readonly string? chromeDriverDir;
        private readonly TimeSpan defaultTimeout = TimeSpan.FromSeconds(60);
        private readonly SemaphoreSlim _driverLock = new SemaphoreSlim(1, 1);
        private bool _alwaysOnTop = true;

        public BraveChatController(string bravePath, string userDataDir, string? chromeDriverDir = null)
        {
            this.bravePath = bravePath ?? throw new ArgumentNullException(nameof(bravePath));
            this.userDataDir = userDataDir ?? throw new ArgumentNullException(nameof(userDataDir));
            this.chromeDriverDir = chromeDriverDir;
        }

        public bool IsAlwaysOnTopEnabled => _alwaysOnTop;

        public void SetAlwaysOnTop(bool enabled)
        {
            _alwaysOnTop = enabled;
            ApplyAlwaysOnTopToBraveWindows(enabled);
        }

        /// <summary>
        /// Start the browser (if not started) and navigate to ChatGPT.
        /// </summary>
        public void EnsureWindowStarted()
        {
            if (driver != null) return;

            try
            {
                // Resolve ChromeDriver directory:
                string? driverFolder = null;

                // check explicitly provided chromeDriverDir
                if (!string.IsNullOrWhiteSpace(chromeDriverDir) && Directory.Exists(chromeDriverDir))
                {
                    driverFolder = chromeDriverDir;
                    FileLogger.Info($"BraveChatController: using configured ChromeDriverDir: {driverFolder}");
                }
                else
                {
                    // Fallback 1: Application Directory (where user puts manual chromedriver.exe)
                    var appBin = AppDomain.CurrentDomain.BaseDirectory;
                    if (File.Exists(Path.Combine(appBin, "chromedriver.exe")))
                    {
                        driverFolder = appBin;
                        FileLogger.Info($"BraveChatController: found chromedriver.exe in app bin: {driverFolder}");
                    }
                    else
                    {
                         FileLogger.Warn("BraveChatController: chromedriver.exe not found in app bin or configured dir. Will try system PATH.");
                    }
                }

                // Create ChromeDriverService
                service = driverFolder != null && Directory.Exists(driverFolder)
                    ? ChromeDriverService.CreateDefaultService(driverFolder)
                    : ChromeDriverService.CreateDefaultService();

                service.SuppressInitialDiagnosticInformation = true;
                service.HideCommandPromptWindow = true; 

                // --- REMOTE DEBUGGING PATTERN ---
                // 1. Launch Brave process manually with --remote-debugging-port=9222
                // This bypasses the "automated test software" flag.
                
                int debugPort = 9222;
                string debugAddress = $"127.0.0.1:{debugPort}";
                
                // Check if port 9222 is already listening (browser open from previous run?)
                bool alreadyRunning = IsPortOpen("127.0.0.1", 9222);

                if (!alreadyRunning)
                {
                    // Ensure profile directory exists
                    if (!Directory.Exists(userDataDir))
                    {
                        try { Directory.CreateDirectory(userDataDir); } catch { /* best effort */ }
                    }

                    string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";
                    var psi = new System.Diagnostics.ProcessStartInfo(bravePath)
                    {
                        Arguments = $"--remote-debugging-port={debugPort} --user-data-dir=\"{userDataDir}\" --disable-blink-features=AutomationControlled --user-agent=\"{ua}\" \"https://chatgpt.com/?temporary-chat=true\"",
                        UseShellExecute = false
                    };
                    
                    FileLogger.Info($"BraveChatController: Launching Brave process manually: {bravePath} {psi.Arguments}");
                    
                    try 
                    {
                        System.Diagnostics.Process.Start(psi);
                        // Give it a moment to spin up the HTTP endpoint
                        Thread.Sleep(3000); 
                    }
                    catch (Exception pEx)
                    {
                        FileLogger.Error($"BraveChatController: Failed to launch Brave process: {pEx.Message}");
                        throw;
                    }
                }
                else
                {
                    FileLogger.Info("BraveChatController: Port 9222 is ALREADY OPEN. Attaching to existing browser...");
                }

                // 2. Attach Selenium to the existing running browser
                var options = new ChromeOptions();
                options.DebuggerAddress = debugAddress;
                
                // instantiate driver attached to the debug address
                try
                {
                    driver = new ChromeDriver(service, options, defaultTimeout);
                }
                catch (Exception ex) when (IsDriverVersionMismatch(ex) && !string.IsNullOrWhiteSpace(chromeDriverDir))
                {
                    FileLogger.Warn("BraveChatController: ChromeDriver version mismatch detected. Retrying with Selenium Manager/default driver resolution.");
                    try
                    {
                        service?.Dispose();
                    }
                    catch { /* ignore */ }

                    service = ChromeDriverService.CreateDefaultService();
                    service.SuppressInitialDiagnosticInformation = true;
                    service.HideCommandPromptWindow = true;
                    driver = new ChromeDriver(service, options, defaultTimeout);
                }

                FileLogger.Info("BraveChatController: Connected to Brave via remote debugging.");
                
                // Attempt to bring window to front / wake it up
                try
                {
                    if (driver.WindowHandles.Count > 0)
                    {
                        driver.SwitchTo().Window(driver.WindowHandles[0]);
                        // Simple toggle to wake up window manager
                        // driver.Manage().Window.Minimize(); 
                        driver.Manage().Window.Maximize();
                    }
                }
                catch (Exception wEx)
                {
                    FileLogger.Warn("BraveChatController: Could not maximize/focus window: " + wEx.Message);
                }

                ApplyAlwaysOnTopToBraveWindows(_alwaysOnTop);

                // Verify we are on the right page or navigate if needed
                try 
                {
                    // If we just launched it, it should be there. 
                    // If we attached to an existing one, we might need to navigate.
                    string url = driver.Url;
                    if (!url.Contains("openai.com"))
                    {
                        driver.Navigate().GoToUrl("https://chat.openai.com/");
                    }
                }
                catch { /* ignore, maybe can't read URL yet */ }

                // wait until input box is present (textarea)
                var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(30));
                wait.Until(d =>
                {
                    try
                    {
                        var ta = d.FindElement(By.Id("prompt-textarea"));
                        return ta != null && ta.Displayed;
                    }
                    catch
                    {
                        try 
                        {
                            var ta2 = d.FindElement(By.CssSelector("textarea"));
                            return ta2 != null && ta2.Displayed;
                        }
                        catch { return false; }
                    }
                });

                FileLogger.Info("BraveChatController: Enabling Temporary Chat...");
                TryEnableTemporaryChat(driver);

                FileLogger.Info("BraveChatController: Ready to chat.");
            }
            catch (WebDriverTimeoutException)
            {
                // If we timed out, it's likely the user is logging in or solving a captcha.
                // DO NOT Dispose/Quit, because that kills the browser the user is trying to use.
                FileLogger.Warn("BraveChatController: Timed out waiting for chat input. Please log in or solve the verification manually. The app will retry sending later.");
                // We keep 'driver' alive so we can try again on the next SendMessage call.
            }
            catch (Exception ex)
            {
                // For other errors (connectivity?), we might want to just log and keep going if possible, 
                // but if we failed to attach, 'driver' might be bad.
                FileLogger.Error("BraveChatController: failed to start/attach browser: " + ex);
                // Keep controller alive; only reset browser/session state.
                QuitBrowser();
            }
        }

        private void TryEnableTemporaryChat(ChromeDriver d)
        {
            try
            {
                // URL-based method is much more reliable than clicking UI elements.
                string currentUrl = d.Url;
                if (!currentUrl.Contains("temporary-chat=true"))
                {
                    FileLogger.Info("BraveChatController: Enabling Temporary Chat via URL navigation...");
                    // If we are on some other page, go to the temporary one.
                    d.Navigate().GoToUrl("https://chatgpt.com/?temporary-chat=true");
                    
                    // Wait for it to load
                    Thread.Sleep(2000);
                }
                else
                {
                    FileLogger.Info("BraveChatController: Temporary Chat already enabled (via URL).");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Warn("BraveChatController: Failed to enable temporary chat via URL: " + ex.Message);
            }
        }
        public bool SendMessageToChatGpt(string message)
        {
            return SendMessageToChatGptAsync(message).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Send message to ChatGPT async — waits for the input element and sends keys + submit.
        /// </summary>
        public async Task<bool> SendMessageToChatGptAsync(string message, int timeoutMs = 60000)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;
            
            // Acquire lock to prevent concurrent Selenium commands
            try
            {
                await _driverLock.WaitAsync();
            }
            catch (ObjectDisposedException)
            {
                FileLogger.Error("BraveChatController: send skipped because controller is disposed.");
                return false;
            }
            try
            {
                EnsureWindowStarted();
                if (driver == null) return false;

                try
                {
                    var wait = new WebDriverWait(driver, TimeSpan.FromMilliseconds(timeoutMs));
                    var ta = wait.Until(d => {
                        try
                        {
                            // ChatGPT standard input ID
                            var found = d.FindElement(By.Id("prompt-textarea"));
                            if (found != null && found.Displayed && found.Enabled) return found;
                            
                            // Fallback
                            found = d.FindElement(By.CssSelector("textarea"));
                            return (found.Displayed && found.Enabled) ? found : null;
                        }
                        catch { return null; }
                    });

                    if (ta == null)
                    {
                        FileLogger.Error("BraveChatController: textarea not found.");
                        return false;
                    }

                    // Focus and type
                    ta.Click();
                    // ta.Clear(); // Clearing might trigger weird React state bugs if not careful, but usually OK.
                    // Better to just send keys if we assume it's empty or appending.
                    // Let's try sending keys directly.
                    ta.SendKeys(message);

                    // try to submit via Enter
                    ta.SendKeys(Keys.Enter);

                    // short wait
                    await Task.Delay(500);

                    // fallback: click submit button if it looks like it didn't send (button still there/active?)
                    // ChatGPT send button usually has data-testid="send-button"
                    try
                    {
                        var sendButtons = driver.FindElements(By.CssSelector("button[data-testid='send-button']"));
                        foreach (var b in sendButtons)
                        {
                            if (b.Displayed && b.Enabled)
                            {
                                b.Click();
                                FileLogger.Debug("BraveChatController: Clicked send button fallback.");
                                break;
                            }
                        }
                    }
                    catch { /* ignore fallback errors */ }

                    FileLogger.Info("BraveChatController: message sent.");
                    return true;
                }
                catch (Exception ex)
                {
                    FileLogger.Error("BraveChatController.SendMessageToChatGptAsync error: " + ex);
                    return false;
                }
            }
            finally
            {
                _driverLock.Release();
            }
        }

        public void RestartBrowser()
        {
            FileLogger.Info("Forcing browser restart...");
            QuitBrowser();
            EnsureWindowStarted();
        }

        public void Dispose()
        {
            _driverLock.Dispose();
            QuitBrowser();
        }

        private void QuitBrowser() 
        {
            try
            {
                driver?.Quit();
                driver?.Dispose();
            }
            catch { /* ignore */ }
            try
            {
                service?.Dispose();
            }
            catch { /* ignore */ }
            service = null;
            driver = null;
        }

        private bool IsPortOpen(string host, int port)
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var result = client.BeginConnect(host, port, null, null);
                    var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
                    if (!success) return false;
                    client.EndConnect(result);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDriverVersionMismatch(Exception ex)
        {
            var msg = ex.ToString();
            return msg.IndexOf("only supports Chrome version", StringComparison.OrdinalIgnoreCase) >= 0
                   || msg.IndexOf("session not created", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ApplyAlwaysOnTopToBraveWindows(bool enabled)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName("brave"))
                {
                    try
                    {
                        var hwnd = process.MainWindowHandle;
                        if (hwnd == IntPtr.Zero) continue;

                        SetWindowPos(
                            hwnd,
                            enabled ? HWND_TOPMOST : HWND_NOTOPMOST,
                            0,
                            0,
                            0,
                            0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    }
                    catch { /* best effort */ }
                }

                FileLogger.Info($"BraveChatController: AlwaysOnTop {(enabled ? "enabled" : "disabled")}");
            }
            catch (Exception ex)
            {
                FileLogger.Warn("BraveChatController: failed applying AlwaysOnTop: " + ex.Message);
            }
        }

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);
    }
}
