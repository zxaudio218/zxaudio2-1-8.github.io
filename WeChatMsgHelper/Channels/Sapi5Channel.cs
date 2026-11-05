using System;
using System.Collections.Generic;
using System.Speech.Synthesis;
using System.Threading;

namespace WeChatMsgHelper.Channels
{
    internal sealed class Sapi5Channel : ISpeechChannel, IDisposable
    {
        private readonly object syncRoot = new object();
        private readonly AutoResetEvent completed = new AutoResetEvent(false);
        private readonly AppConfig config;
        private SpeechSynthesizer synthesizer;
        private bool ready;
        private bool disposed;

        internal Sapi5Channel(AppConfig config) { this.config = config; }

        public string Name
        {
            get { return string.IsNullOrWhiteSpace(config.Sapi5Voice) ? "SAPI5（系统默认语音）" : "SAPI5（" + config.Sapi5Voice + "）"; }
        }

        public bool IsAvailable { get { return ready; } }

        public bool Initialize(out string error)
        {
            lock (syncRoot)
            {
                error = string.Empty;
                if (disposed) { error = "通道已释放。"; return false; }
                if (ready) return true;
                try
                {
                    synthesizer = new SpeechSynthesizer();
                    synthesizer.SetOutputToDefaultAudioDevice();
                    if (!string.IsNullOrWhiteSpace(config.Sapi5Voice)) synthesizer.SelectVoice(config.Sapi5Voice);
                    synthesizer.Rate = Math.Max(-10, Math.Min(10, config.Sapi5Rate));
                    synthesizer.Volume = Math.Max(0, Math.Min(100, config.Sapi5Volume));
                    synthesizer.SpeakCompleted += OnSpeakCompleted;
                    ready = true;
                    return true;
                }
                catch (Exception ex)
                {
                    if (synthesizer != null) synthesizer.Dispose();
                    synthesizer = null;
                    ready = false;
                    error = ex.Message;
                    return false;
                }
            }
        }

        public bool Speak(string text, bool interrupt)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            SpeechSynthesizer current;
            lock (syncRoot)
            {
                if (disposed) return false;
                string error;
                if (!ready && !Initialize(out error))
                {
                    AppLog.Write("SAPI5 初始化失败：" + error);
                    return false;
                }
                current = synthesizer;
                completed.Reset();
            }
            try
            {
                if (interrupt) current.SpeakAsyncCancelAll();
                current.SpeakAsync(text);
                while (!disposed && !completed.WaitOne(100)) { }
                return !disposed;
            }
            catch (Exception ex)
            {
                AppLog.Write("SAPI5 朗读异常：" + ex.Message);
                return false;
            }
        }

        public bool Submit(string text, bool interrupt)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            lock (syncRoot)
            {
                if (disposed || !ready) return false;
                try
                {
                    if (interrupt) synthesizer.SpeakAsyncCancelAll();
                    synthesizer.SpeakAsync(text);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Write("SAPI5 退出提示提交异常：" + ex.Message);
                    return false;
                }
            }
        }

        public bool Stop()
        {
            SpeechSynthesizer current;
            lock (syncRoot)
            {
                current = synthesizer;
            }
            try { if (current != null) current.SpeakAsyncCancelAll(); }
            catch { }
            completed.Set();
            return true;
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed) return;
                disposed = true;
                try
                {
                    if (synthesizer != null)
                    {
                        synthesizer.SpeakAsyncCancelAll();
                        synthesizer.SpeakCompleted -= OnSpeakCompleted;
                        synthesizer.Dispose();
                    }
                }
                catch { }
                synthesizer = null;
                ready = false;
                completed.Set();
            }
            completed.Dispose();
        }

        internal static IList<string> GetInstalledVoiceNames()
        {
            List<string> names = new List<string>();
            try
            {
                using (SpeechSynthesizer probe = new SpeechSynthesizer())
                {
                    foreach (InstalledVoice voice in probe.GetInstalledVoices())
                    {
                        try { if (voice.Enabled) names.Add(voice.VoiceInfo.Name); }
                        catch { }
                    }
                }
            }
            catch { }
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
            return names;
        }

        private void OnSpeakCompleted(object sender, SpeakCompletedEventArgs e)
        {
            completed.Set();
        }
    }
}
