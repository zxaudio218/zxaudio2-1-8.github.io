using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WeChatMsgHelper.Channels
{
    internal sealed class AutoSpeechChannel : ISpeechChannel, IDisposable
    {
        private readonly object syncRoot = new object();
        private readonly AppConfig config;
        private ISpeechChannel active;
        private bool disposed;

        internal AutoSpeechChannel(AppConfig config) { this.config = config; }

        public string Name { get { return active == null ? "自动选择" : "自动：" + active.Name; } }
        public bool IsAvailable { get { return active != null && active.IsAvailable; } }

        public bool Initialize(out string error)
        {
            lock (syncRoot)
            {
                if (disposed) { error = "自动通道已释放。"; return false; }
                if (active != null && active.IsAvailable) { error = string.Empty; return true; }
                DisposeActive();

                List<string> errors = new List<string>();
                bool baoyiRunning = BaoyiChannel.IsReaderRunning();
                if (baoyiRunning && TryActivate(new BaoyiChannel(config, BaoyiChannelMode.Independent), errors))
                {
                    error = string.Empty;
                    return true;
                }
                if (TryActivate(new ZhengduChannel(config, ZhengduChannelMode.Independent), errors))
                {
                    error = string.Empty;
                    return true;
                }
                if (!baoyiRunning && TryActivate(new BaoyiChannel(config, BaoyiChannelMode.Independent), errors))
                {
                    error = string.Empty;
                    return true;
                }
                if (TryActivate(new Sapi5Channel(config), errors))
                {
                    error = string.Empty;
                    return true;
                }

                error = errors.Count == 0 ? "未找到可用语音通道。" : string.Join("；", errors.ToArray());
                return false;
            }
        }

        public bool Speak(string text, bool interrupt)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            ISpeechChannel current;
            lock (syncRoot)
            {
                if (disposed) return false;
                string error;
                if ((active == null || !active.IsAvailable) && !Initialize(out error))
                {
                    AppLog.Write("自动通道初始化失败：" + error);
                    return false;
                }
                current = active;
            }

            if (current.Speak(text, interrupt)) return true;
            if (current is Sapi5Channel) return false;

            lock (syncRoot)
            {
                if (disposed) return false;
                if (!object.ReferenceEquals(active, current))
                {
                    current = active;
                }
                else
                {
                    string failedName = current.Name;
                    AppLog.Write("自动通道：" + failedName + " 朗读失败，切换到 SAPI5。 ");
                    DisposeActive();
                    List<string> errors = new List<string>();
                    if (!TryActivate(new Sapi5Channel(config), errors)) return false;
                    current = active;
                }
            }

            if (current == null) return false;
            return current.Speak(text, interrupt);
        }

        public bool Submit(string text, bool interrupt)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            lock (syncRoot)
            {
                return !disposed && active != null && active.IsAvailable && active.Submit(text, interrupt);
            }
        }

        public bool Stop()
        {
            ISpeechChannel current;
            lock (syncRoot)
            {
                current = active;
            }
            try { if (current != null) current.Stop(); }
            catch { }
            return true;
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed) return;
                disposed = true;
                DisposeActive();
            }
        }

        private bool TryActivate(ISpeechChannel candidate, IList<string> errors)
        {
            string error;
            if (candidate.Initialize(out error))
            {
                active = candidate;
                AppLog.Write("自动通道已选择：" + candidate.Name);
                return true;
            }
            errors.Add(candidate.Name + "：" + error);
            AppLog.Write("自动通道候选不可用：" + candidate.Name + "，" + error);
            IDisposable disposable = candidate as IDisposable;
            if (disposable != null) disposable.Dispose();
            return false;
        }

        private void DisposeActive()
        {
            if (active == null) return;
            try { active.Stop(); }
            catch { }
            IDisposable disposable = active as IDisposable;
            if (disposable != null) disposable.Dispose();
            active = null;
        }

        private static bool IsProcessRunning(string prefix)
        {
            foreach (Process process in Process.GetProcesses())
            {
                try { if (process.ProcessName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
                finally { process.Dispose(); }
            }
            return false;
        }
    }
}
