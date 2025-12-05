// BraveChatController.cs
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly TimeSpan defaultTimeout = TimeSpan.FromSeconds(20);

        public BraveChatController(string bravePath, string userDataDir, string? chromeDriverDir = null)
        {
            this.bravePath = bravePath ?? throw new ArgumentNullException(nameof(bravePath));
            this.userDataDir = userDataDir ?? throw new ArgumentNullException(nameof(userDataDir));
            this.chromeDriverDir = chromeDriverDir;
        }

        /// <summary>
        /// Start the browser (if not started) and navigate to ChatGPT.
        /// </summary>
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
                    var psi = new System.Diagnostics.ProcessStartInfo(bravePath)
                    {
                        Arguments = $"--remote-debugging-port={debugPort} --user-data-dir=\"{userDataDir}\" \"https://chat.openai.com\"",
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
                driver = new ChromeDriver(service, options, defaultTimeout);

                FileLogger.Info("BraveChatController: Connected to Brave via remote debugging.");
                
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
                // If driver is null/broken, we might need to dispose, but let's try to be lenient.
                if (driver == null) Dispose(); 
            }
        }

        /// <summary>
        /// Send message to ChatGPT synchronously. Returns true when message was entered & submitted.
        /// </summary>
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

        public void Dispose()
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
    }
}
