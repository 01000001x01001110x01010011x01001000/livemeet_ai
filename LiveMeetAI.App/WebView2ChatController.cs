using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using LiveMeetAI.App;

namespace LiveMeetAI.AI
{
    public class WebView2ChatController : IDisposable
    {
        private readonly WebView2 _webView;
        private readonly string _userDataFolder;
        private readonly Dispatcher _dispatcher;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private bool _pageReady = false;

        public WebView2ChatController(WebView2 webView, string userDataFolder, Dispatcher dispatcher)
        {
            _webView = webView;
            _userDataFolder = userDataFolder;
            _dispatcher = dispatcher;
        }

        // Must be called from the UI thread (e.g. inside MainWindow_Loaded).
        public async Task InitializeAsync()
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
                await _webView.EnsureCoreWebView2Async(env);

                _webView.CoreWebView2.NavigationCompleted += (s, e) =>
                {
                    _pageReady = e.IsSuccess;
                    FileLogger.Info($"WebView2: Navigation completed. Success={e.IsSuccess}");
                };

                // Disable DevTools so Ctrl+Shift+C (and other browser debug shortcuts) don't fire.
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

                // Force dark mode so ChatGPT loads in dark theme
                _webView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;

                _webView.Source = new Uri("https://chatgpt.com/?temporary-chat=true");
                FileLogger.Info("WebView2: Initialized and navigating to ChatGPT.");
            }
            catch (Exception ex)
            {
                FileLogger.Error("WebView2: Initialization failed: " + ex.Message);
            }
        }

        public async Task<bool> SendMessageAsync(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;

            await _sendLock.WaitAsync();
            try
            {
                var escaped = message
                    .Replace("\\", "\\\\")
                    .Replace("'", "\\'")
                    .Replace("\r", "")
                    .Replace("\n", " ");

                var script = $@"
(function() {{
    try {{
        var editor = document.getElementById('prompt-textarea');
        if (!editor) {{
            var all = document.querySelectorAll('[contenteditable=""true""]');
            for (var i = 0; i < all.length; i++) {{
                if (all[i].offsetParent !== null) {{ editor = all[i]; break; }}
            }}
        }}
        if (!editor) return 'notfound';
        editor.focus();
        document.execCommand('insertText', false, '{escaped}');
        setTimeout(function() {{
            var btn = document.querySelector('button[data-testid=""send-button""]');
            if (btn && !btn.disabled) {{
                btn.click();
            }} else {{
                editor.dispatchEvent(new KeyboardEvent('keydown', {{key:'Enter',code:'Enter',keyCode:13,which:13,bubbles:true}}));
            }}
        }}, 300);
        return 'sent';
    }} catch(e) {{ return 'error:' + e.message; }}
}})()";

                // ExecuteScriptAsync must run on the UI thread.
                var tcs = new TaskCompletionSource<string?>();
                _dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        var result = await _webView.ExecuteScriptAsync(script);
                        tcs.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });

                var result = await tcs.Task;
                bool success = result != null && result.Contains("sent");
                FileLogger.Info($"WebView2: SendMessage result={result}");
                return success;
            }
            catch (Exception ex)
            {
                FileLogger.Error("WebView2: SendMessageAsync failed: " + ex.Message);
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void NavigateToNewChat()
        {
            _dispatcher.BeginInvoke(() =>
            {
                try { _webView.CoreWebView2?.Navigate("https://chatgpt.com/?temporary-chat=true"); }
                catch (Exception ex) { FileLogger.Warn("WebView2: NavigateToNewChat failed: " + ex.Message); }
            });
        }

        public void Dispose()
        {
            _sendLock.Dispose();
        }
    }
}
