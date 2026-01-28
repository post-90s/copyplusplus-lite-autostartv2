using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CopyPlusPlus.Properties;
using Hardcodet.Wpf.TaskbarNotification;

namespace CopyPlusPlus
{
    /// <summary>
    ///     Main window for the lightweight build.
    ///     Keeps only:
    ///     - Merge newlines
    ///     - Remove spaces
    ///     Trigger: Control + C + C
    /// </summary>
    public partial class MainWindow
    {
        public static TaskbarIcon NotifyIcon;

        private readonly GlobalKeyboardHook _keyboardHook;

        // Ctrl+C spam detection: trigger once when Ctrl is held and C is pressed >= 2 times.
        private DateTime _lastCtrlCKeyDownUtc = DateTime.MinValue;
        private int _ctrlCCount;
        private DateTime _lastProcessUtc = DateTime.MinValue;
        private System.Threading.CancellationTokenSource _ctrlCTriggerCts;
        private readonly object _ctrlCTriggerLock = new object();

        private bool _isExiting;

        /// <summary>Global enable/disable (tray menu).</summary>
        public bool GlobalSwitch = true;

        public MainWindow()
        {
            InitializeComponent();

            // Tray icon (declared in NotifyIcon/TrayIocn.xaml)
            NotifyIcon = (TaskbarIcon)FindResource("MyNotifyIcon");
            NotifyIcon.Visibility = Visibility.Visible;

            RestoreUiState();

            // Ensure autostart registry entry is aligned with settings (and current exe path).
            AutoStartManager.ApplyFromSettings();

            _keyboardHook = new GlobalKeyboardHook();
            _keyboardHook.KeyDown += KeyboardHookOnKeyDown;
        }

        private void RestoreUiState()
        {
            // Backward compatible with the original SwitchCheck StringCollection.
            // We only use indices:
            // 0 = Merge newlines
            // 1 = Remove spaces
            // 2 = Shortcut enabled
            var list = Settings.Default.SwitchCheck?.Cast<string>().ToList() ?? new System.Collections.Generic.List<string>();

            SwitchMain.IsOn = ReadBool(list, 0, defaultValue: true);
            SwitchSpace.IsOn = ReadBool(list, 1, defaultValue: false);
            SwitchShortcut.IsOn = ReadBool(list, 2, defaultValue: true);
        }

        private static bool ReadBool(System.Collections.Generic.IReadOnlyList<string> list, int index, bool defaultValue)
        {
            if (index < 0 || index >= list.Count) return defaultValue;
            return bool.TryParse(list[index], out var v) ? v : defaultValue;
        }

        private void SaveUiState()
        {
            var sc = Settings.Default.SwitchCheck;
            if (sc == null) return;

            // Ensure at least 3 entries.
            while (sc.Count < 3) sc.Add(string.Empty);

            sc[0] = SwitchMain.IsOn.ToString();
            sc[1] = SwitchSpace.IsOn.ToString();
            sc[2] = SwitchShortcut.IsOn.ToString();

            Settings.Default.Save();
        }

        private void KeyboardHookOnKeyDown(object sender, GlobalKeyEventArgs e)
        {
            if (!GlobalSwitch) return;
            if (!SwitchShortcut.IsOn) return;

            // Detect Ctrl is held, and count C presses.
            if (!e.Ctrl || e.Key != Key.C) return;

            var now = DateTime.UtcNow;

            // Debounce key repeat.
            if ((now - _lastCtrlCKeyDownUtc).TotalMilliseconds < 35) return;

            // If there's a long gap, start a new sequence.
            if ((now - _lastCtrlCKeyDownUtc).TotalMilliseconds > 1500)
            {
                _ctrlCCount = 0;
            }

            _lastCtrlCKeyDownUtc = now;
            _ctrlCCount++;

            // Strategy:
            // - While Ctrl is held, if user presses C >= 2 times, trigger processing once.
            // - We wait for a short "idle" window after the *last* C press to avoid the
            //   case where extra C presses race with clipboard updates.
            ScheduleCtrlCProcessing();
        }

        private void ScheduleCtrlCProcessing()
        {
            System.Threading.CancellationTokenSource cts;
            lock (_ctrlCTriggerLock)
            {
                _ctrlCTriggerCts?.Cancel();
                _ctrlCTriggerCts?.Dispose();
                _ctrlCTriggerCts = new System.Threading.CancellationTokenSource();
                cts = _ctrlCTriggerCts;
            }

            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    // Wait for user to finish pressing C, and for clipboard to settle.
                    await Task.Delay(220, cts.Token);
                    if (cts.IsCancellationRequested) return;

                    // Require at least 2 presses in the current burst.
                    if (_ctrlCCount < 2) return;

                    // Ensure Ctrl is still held at trigger time.
                    if (!(Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)))
                    {
                        _ctrlCCount = 0;
                        return;
                    }

                    // Throttle: avoid re-processing too frequently.
                    if ((DateTime.UtcNow - _lastProcessUtc).TotalMilliseconds < 250) return;
                    _lastProcessUtc = DateTime.UtcNow;

                    // Extra small delay to allow clipboard update after the final Ctrl+C.
                    await Task.Delay(120, cts.Token);
                    if (cts.IsCancellationRequested) return;

                    await ProcessClipboardTextWithRetryAsync(cts.Token);
                }
                catch (TaskCanceledException)
                {
                    // ignored
                }
                finally
                {
                    // IMPORTANT:
                    // If this scheduled task was canceled because the user pressed C again,
                    // we must NOT reset _ctrlCCount here; otherwise odd/even press counts
                    // can accidentally cancel out.
                    if (!cts.IsCancellationRequested)
                    {
                        _ctrlCCount = 0;
                    }
                }
            });
        }

        private async Task ProcessClipboardTextWithRetryAsync(System.Threading.CancellationToken token)
        {
            // Clipboard can be locked by other processes (or still updating right after Ctrl+C).
            // Retry a few times with small delays.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (!Clipboard.ContainsText()) return;
                    var input = Clipboard.GetText();
                    var output = ProcessText(input);
                    Clipboard.SetText(output);
                    return;
                }
                catch
                {
                    await Task.Delay(60, token);
                }
            }
        }

        private string ProcessText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Remove odd spaces introduced by CAJ viewer.
            text = text.Replace("", "");

            var mergeNewlines = SwitchMain.IsOn;
            var removeSpaces = SwitchSpace.IsOn;

            // Single-pass builder to avoid O(n^2) string.Remove allocations.
            var sb = new StringBuilder(text.Length);

            for (int i = 0; i < text.Length; i++)
            {
                var ch = text[i];

                // Normalize CRLF/CR to newline.
                if (ch == '\r')
                {
                    // Skip the following LF if present.
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    ch = '\n';
                }

                // Remove spaces.
                if (removeSpaces && IsRemovableSpace(ch))
                {
                    continue;
                }

                // Merge newlines.
                if (mergeNewlines && ch == '\n')
                {
                    // Optionally insert a space between two ASCII "word" chars.
                    if (!removeSpaces)
                    {
                        var prev = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
                        var next = PeekNextNonNewline(text, i + 1);
                        if (IsAsciiWordChar(prev) && IsAsciiWordChar(next))
                        {
                            sb.Append(' ');
                        }
                    }

                    continue;
                }

                sb.Append(ch);
            }

            return sb.ToString();
        }

        private static bool IsRemovableSpace(char ch)
        {
            // Regular space, tab, NBSP, full-width space.
            return ch == ' ' || ch == '\t' || ch == '\u00A0' || ch == '\u3000';
        }

        private static char PeekNextNonNewline(string text, int startIndex)
        {
            for (int i = startIndex; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '\r' || ch == '\n') continue;
                if (IsRemovableSpace(ch)) return ch; // treat space as a "real" next char for spacing decision
                return ch;
            }
            return '\0';
        }

        private static bool IsAsciiWordChar(char ch)
        {
            if (ch == '\0') return false;
            if (ch > 127) return false;
            return char.IsLetterOrDigit(ch) || ch == '_' || ch == '-';
        }

        /// <summary>Called from App startup when /AutoStart is passed.</summary>
        public void OnAutoStart(bool auto)
        {
            if (auto)
            {
                // Silent background start: do not display the window.
                ShowInTaskbar = false;
                Hide();
                NotifyIcon.Visibility = Visibility.Visible;
                return;
            }

            ShowInTaskbar = true;
            Show();
        }

        /// <summary>Called by tray menu to exit cleanly.</summary>
        public void RequestExit()
        {
            _isExiting = true;
            Application.Current.Shutdown();
        }

        /// <summary>
        /// Backward-compatible helper used by the tray ViewModel.
        /// Keeps the notify icon visible.
        /// </summary>
        public void HideNotifyIcon()
        {
            try
            {
                NotifyIcon.Visibility = Visibility.Visible;
            }
            catch
            {
                // ignored
            }
        }

        private async void ManualBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ProcessClipboardTextWithRetryAsync(System.Threading.CancellationToken.None);
            }
            catch
            {
                // ignored
            }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (_isExiting) return;

            try
            {
                NotifyIcon.Visibility = Visibility.Visible;
                NotifyIcon.ShowBalloonTip("Copy++", "软件已最小化至托盘，右键托盘图标可退出/禁用/开机自启动", BalloonIcon.Info);
            }
            catch
            {
                // ignored
            }

            Hide();
            e.Cancel = true;
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            SaveUiState();

            try
            {
                _keyboardHook.KeyDown -= KeyboardHookOnKeyDown;
                _keyboardHook.Dispose();
            }
            catch
            {
                // ignored
            }

            try
            {
                NotifyIcon?.Dispose();
            }
            catch
            {
                // ignored
            }
        }
    }
}
