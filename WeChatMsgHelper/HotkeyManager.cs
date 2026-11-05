using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WeChatMsgHelper
{
    internal sealed class HotkeyManager : IDisposable
    {
        private sealed class FallbackHotkey
        {
            internal uint Modifiers;
            internal Keys Key;
        }

        internal const int ReadLatestId = 1;
        internal const int ToggleNotifyId = 2;
        internal const int ToggleModeId = 3;
        internal const int OpenSettingsId = 4;
        internal const int TestSpeechId = 5;
        internal const int FocusOfficialAccountsId = 10;
        internal const int FocusContactsId = 11;
        internal const int FocusVoipId = 12;
        internal const int ReviewPreviousId = 13;
        internal const int ReviewNextId = 14;
        internal const int ReviewFirstId = 15;
        internal const int ReviewLastId = 16;
        internal const int NvdaModeId = 17;
        // Same gestures as NVDA PC WeChat Enhancement 2.4.4.
        internal const string ReviewPreviousHotkey = "Ctrl+Alt+Up";
        internal const string ReviewNextHotkey = "Ctrl+Alt+Down";
        internal const string ReviewFirstHotkey = "Ctrl+Alt+Home";
        internal const string ReviewLastHotkey = "Ctrl+Alt+End";
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModNoRepeat = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private readonly IntPtr handle;
        private readonly List<int> registered = new List<int>();
        private readonly Dictionary<int, FallbackHotkey> fallback = new Dictionary<int, FallbackHotkey>();
        private readonly HashSet<int> fallbackPressed = new HashSet<int>();
        private bool wechatContextActive;
        private bool inputContextActive;

        internal HotkeyManager(IntPtr handle)
        {
            this.handle = handle;
        }

        internal void RegisterAll(AppConfig config, Action<string> onFailure)
        {
            UnregisterAll();
            Register(ReadLatestId, config.ReadLatestHotkey, onFailure);
            Register(ToggleNotifyId, config.ToggleNotifyHotkey, onFailure);
            Register(ToggleModeId, config.ToggleModeHotkey, onFailure);
            Register(OpenSettingsId, config.OpenSettingsHotkey, onFailure);
        }

        internal void UpdateWeChatContext(bool foreground, bool inputFocused, Action<string> onFailure)
        {
            if (foreground != wechatContextActive)
            {
                wechatContextActive = foreground;
                if (foreground)
                {
                    Register(FocusOfficialAccountsId, "Alt+P", onFailure);
                    Register(FocusContactsId, "Alt+T", onFailure);
                    Register(FocusVoipId, "Alt+V", onFailure);
                    Register(NvdaModeId, "F3", onFailure);
                }
                else
                {
                    Unregister(FocusOfficialAccountsId);
                    Unregister(FocusContactsId);
                    Unregister(FocusVoipId);
                    Unregister(NvdaModeId);
                }
            }
            bool shouldEnableInput = foreground && inputFocused;
            if (shouldEnableInput == inputContextActive) return;
            inputContextActive = shouldEnableInput;
            if (shouldEnableInput)
            {
                Register(ReviewPreviousId, ReviewPreviousHotkey, onFailure);
                Register(ReviewNextId, ReviewNextHotkey, onFailure);
                Register(ReviewFirstId, ReviewFirstHotkey, onFailure);
                Register(ReviewLastId, ReviewLastHotkey, onFailure);
            }
            else
            {
                Unregister(ReviewPreviousId);
                Unregister(ReviewNextId);
                Unregister(ReviewFirstId);
                Unregister(ReviewLastId);
            }
        }

        public void Dispose() { UnregisterAll(); }

        internal IList<int> PollFallback()
        {
            List<int> activated = new List<int>();
            foreach (KeyValuePair<int, FallbackHotkey> pair in fallback)
            {
                bool down = IsDown(pair.Value);
                if (down && fallbackPressed.Add(pair.Key)) activated.Add(pair.Key);
                else if (!down) fallbackPressed.Remove(pair.Key);
            }
            return activated;
        }

        private void Register(int id, string text, Action<string> onFailure)
        {
            if (registered.Contains(id) || fallback.ContainsKey(id)) return;
            uint modifiers;
            Keys key;
            if (!TryParse(text, out modifiers, out key))
            {
                if (onFailure != null) onFailure("快捷键注册失败：" + text);
                return;
            }
            if (!RegisterHotKey(handle, id, modifiers | ModNoRepeat, (uint)key))
            {
                if (id >= FocusOfficialAccountsId)
                {
                    fallback[id] = new FallbackHotkey { Modifiers = modifiers, Key = key };
                    if (onFailure != null) onFailure("快捷键系统注册冲突，已启用兼容检测：" + text);
                }
                else if (onFailure != null) onFailure("快捷键注册失败：" + text);
                return;
            }
            registered.Add(id);
        }

        private void Unregister(int id)
        {
            if (registered.Remove(id)) UnregisterHotKey(handle, id);
            fallback.Remove(id);
            fallbackPressed.Remove(id);
        }

        private void UnregisterAll()
        {
            foreach (int id in registered) UnregisterHotKey(handle, id);
            registered.Clear();
            fallback.Clear();
            fallbackPressed.Clear();
            wechatContextActive = false;
            inputContextActive = false;
        }

        private static bool IsDown(FallbackHotkey hotkey)
        {
            bool control = (GetAsyncKeyState((int)Keys.ControlKey) & 0x8000) != 0;
            bool alt = (GetAsyncKeyState((int)Keys.Menu) & 0x8000) != 0;
            bool shift = (GetAsyncKeyState((int)Keys.ShiftKey) & 0x8000) != 0;
            if (((hotkey.Modifiers & ModControl) != 0) != control) return false;
            if (((hotkey.Modifiers & ModAlt) != 0) != alt) return false;
            if (((hotkey.Modifiers & ModShift) != 0) != shift) return false;
            return (GetAsyncKeyState((int)hotkey.Key) & 0x8000) != 0;
        }

        private static bool TryParse(string text, out uint modifiers, out Keys key)
        {
            modifiers = 0;
            key = Keys.None;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split('+');
            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim();
                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase)) modifiers |= ModControl;
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= ModAlt;
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= ModShift;
                else
                {
                    try { key = (Keys)Enum.Parse(typeof(Keys), part, true); }
                    catch { return false; }
                }
            }
            return key != Keys.None;
        }
    }
}
