using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WeChatMsgHelper
{
    internal static class SpeechChannelNames
    {
        internal const string Auto = "auto";
        internal const string ZhengduIndependent = "zhengdu-independent";
        internal const string ZhengduScreenReader = "zhengdu-screen-reader";
        internal const string Baoyi = "baoyi-independent";
        internal const string BaoyiIndependent = "baoyi-independent";
        internal const string BaoyiScreenReader = "baoyi-screen-reader";
        internal const string Sapi5 = "sapi5";

        internal static string Normalize(string value)
        {
            if (string.Equals(value, Auto, StringComparison.OrdinalIgnoreCase)) return Auto;
            if (string.Equals(value, ZhengduScreenReader, StringComparison.OrdinalIgnoreCase)) return ZhengduScreenReader;
            if (string.Equals(value, "baoyi", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, BaoyiIndependent, StringComparison.OrdinalIgnoreCase)) return BaoyiIndependent;
            if (string.Equals(value, BaoyiScreenReader, StringComparison.OrdinalIgnoreCase)) return BaoyiScreenReader;
            if (string.Equals(value, Sapi5, StringComparison.OrdinalIgnoreCase)) return Sapi5;
            return ZhengduIndependent;
        }
    }

    internal enum NotificationMode
    {
        Off = 0,
        SoundOnly = 1,
        SpeechOnly = 2,
        SpeechAndSound = 3
    }

    internal sealed class AppConfig
    {
        public string FilePath;
        public NotificationMode NotificationMode = NotificationMode.SpeechAndSound;
        public bool ListeningEnabled = true;
        public bool SoundEnabled = true;
        public bool PollFallback = true;
        public int PollIntervalMs = 500;
        public bool ReadNonTextMessages = true;
        public bool IgnoreSystemMessages;
        public bool AutoStart;
        public string SpeechChannel = SpeechChannelNames.ZhengduIndependent;
        public string ReadLatestHotkey = "Ctrl+Alt+F11";
        public string ToggleNotifyHotkey = "Ctrl+Alt+Shift+F11";
        public string ToggleModeHotkey = "Ctrl+Alt+P";
        public string OpenSettingsHotkey = "Ctrl+Alt+S";
        public bool ZhengduKeyDownInterrupt;
        public string BaoyiChannelName = "WeChatMsg";
        public string Sapi5Voice = string.Empty;
        public int Sapi5Rate;
        public int Sapi5Volume = 100;

        public static AppConfig Load(string path)
        {
            AppConfig config = new AppConfig();
            config.FilePath = path;
            if (!File.Exists(path))
            {
                config.Save();
                return config;
            }

            Dictionary<string, string> values = Parse(path);
            config.NotificationMode = (NotificationMode)Clamp(GetInt(values, "General.NotificationMode", (int)config.NotificationMode), 0, 3);
            config.ListeningEnabled = GetBool(values, "General.ListeningEnabled", config.ListeningEnabled);
            config.SoundEnabled = GetBool(values, "General.SoundEnabled", config.SoundEnabled);
            config.PollFallback = GetBool(values, "General.PollFallback", config.PollFallback);
            config.PollIntervalMs = Clamp(GetInt(values, "General.PollIntervalMs", config.PollIntervalMs), 200, 5000);
            config.ReadNonTextMessages = GetBool(values, "General.ReadNonTextMessages", config.ReadNonTextMessages);
            config.IgnoreSystemMessages = GetBool(values, "General.IgnoreSystemMessages", config.IgnoreSystemMessages);
            config.AutoStart = GetBool(values, "General.AutoStart", config.AutoStart);
            config.SpeechChannel = SpeechChannelNames.Normalize(Get(values, "General.SpeechChannel", config.SpeechChannel));
            config.ReadLatestHotkey = Get(values, "Hotkeys.ReadLatest", config.ReadLatestHotkey);
            config.ToggleNotifyHotkey = Get(values, "Hotkeys.ToggleNotify", config.ToggleNotifyHotkey);
            config.ToggleModeHotkey = Get(values, "Hotkeys.ToggleMode", config.ToggleModeHotkey);
            config.OpenSettingsHotkey = Get(values, "Hotkeys.OpenSettings", config.OpenSettingsHotkey);
            config.ZhengduKeyDownInterrupt = GetBool(values, "Zhengdu.KeyDownInterrupt", config.ZhengduKeyDownInterrupt);
            config.BaoyiChannelName = NormalizeBaoyiChannelName(Get(values, "Baoyi.ChannelName", config.BaoyiChannelName));
            config.Sapi5Voice = Get(values, "Sapi5.Voice", config.Sapi5Voice);
            config.Sapi5Rate = Clamp(GetInt(values, "Sapi5.Rate", config.Sapi5Rate), -10, 10);
            config.Sapi5Volume = Clamp(GetInt(values, "Sapi5.Volume", config.Sapi5Volume), 0, 100);
            config.Save();
            return config;
        }

        public void Save()
        {
            string[] lines =
            {
                "[General]",
                "NotificationMode=" + ((int)NotificationMode).ToString(CultureInfo.InvariantCulture),
                "ListeningEnabled=" + Bool(ListeningEnabled),
                "SoundEnabled=" + Bool(SoundEnabled),
                "PollFallback=" + Bool(PollFallback),
                "PollIntervalMs=" + PollIntervalMs.ToString(CultureInfo.InvariantCulture),
                "ReadNonTextMessages=" + Bool(ReadNonTextMessages),
                "IgnoreSystemMessages=" + Bool(IgnoreSystemMessages),
                "AutoStart=" + Bool(AutoStart),
                "SpeechChannel=" + SpeechChannelNames.Normalize(SpeechChannel),
                string.Empty,
                "[Hotkeys]",
                "ReadLatest=" + ReadLatestHotkey,
                "ToggleNotify=" + ToggleNotifyHotkey,
                "ToggleMode=" + ToggleModeHotkey,
                "OpenSettings=" + OpenSettingsHotkey,
                string.Empty,
                "[Zhengdu]",
                "KeyDownInterrupt=" + Bool(ZhengduKeyDownInterrupt),
                string.Empty,
                "[Baoyi]",
                "ChannelName=" + NormalizeBaoyiChannelName(BaoyiChannelName),
                string.Empty,
                "[Sapi5]",
                "Voice=" + (Sapi5Voice ?? string.Empty),
                "Rate=" + Sapi5Rate.ToString(CultureInfo.InvariantCulture),
                "Volume=" + Sapi5Volume.ToString(CultureInfo.InvariantCulture)
            };
            File.WriteAllLines(FilePath, lines, new UTF8Encoding(false));
        }

        private static Dictionary<string, string> Parse(string path)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string section = string.Empty;
            foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string value = line.Substring(equals + 1);
                int comment = value.IndexOf(';');
                if (comment >= 0) value = value.Substring(0, comment);
                result[section + "." + line.Substring(0, equals).Trim()] = value.Trim();
            }
            return result;
        }

        private static string Get(IDictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : fallback;
        }

        private static bool GetBool(IDictionary<string, string> values, string key, bool fallback)
        {
            bool value;
            return bool.TryParse(Get(values, key, string.Empty), out value) ? value : fallback;
        }

        private static int GetInt(IDictionary<string, string> values, string key, int fallback)
        {
            int value;
            return int.TryParse(Get(values, key, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static string Bool(bool value) { return value ? "true" : "false"; }

        private static string NormalizeBaoyiChannelName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "WeChatMsg" : value.Trim();
        }
    }
}
