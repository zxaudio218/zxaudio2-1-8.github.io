using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using WeChatMsgHelper.Channels;

namespace WeChatMsgHelper
{
    internal sealed class SpeechQueue : IDisposable
    {
        private readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private readonly Thread worker;
        private readonly ISpeechChannel channel;
        private readonly Action<bool, string> onInitialized;
        private volatile bool stopping;
        private bool disposed;
        private int queuedCount;

        internal SpeechQueue(ISpeechChannel channel, Action<bool, string> onInitialized)
        {
            this.channel = channel;
            this.onInitialized = onInitialized;
            worker = new Thread(WorkerMain);
            worker.IsBackground = true;
            worker.Name = "WeChatMsgHelper Speech";
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        internal string ChannelName { get { return channel.Name; } }
        internal bool IsAvailable { get { return channel.IsAvailable; } }

        internal void Enqueue(string text)
        {
            if (stopping || string.IsNullOrWhiteSpace(text)) return;
            queue.Enqueue(text);
            int count = Interlocked.Increment(ref queuedCount);
            while (count > 200)
            {
                string ignored;
                if (!queue.TryDequeue(out ignored)) break;
                count = Interlocked.Decrement(ref queuedCount);
            }
            signal.Set();
        }

        public void Dispose()
        {
            Shutdown(null);
        }

        internal void PrepareForProcessExit()
        {
            if (disposed) return;
            disposed = true;
            stopping = true;
            signal.Set();
        }

        private void Shutdown(string announcement)
        {
            if (disposed) return;
            disposed = true;
            stopping = true;
            try { channel.Stop(); }
            catch { }
            signal.Set();
            // Submit the exit prompt before waiting for the worker. This keeps the
            // user-visible behavior immediate, matching the rack assistant.
            if (!string.IsNullOrWhiteSpace(announcement)) SpeakLifecycleAnnouncement(announcement, true);
            if (worker.IsAlive && !worker.Join(1500))
            {
                AppLog.Write("语音工作线程未能在 1.5 秒内退出：" + channel.Name);
            }
            IDisposable disposable = channel as IDisposable;
            if (disposable != null) disposable.Dispose();
            signal.Dispose();
        }

        private void WorkerMain()
        {
            string initializeError;
            bool initialized = channel.Initialize(out initializeError);
            AppLog.Write(initialized ? "语音通道初始化成功：" + channel.Name : "语音通道初始化失败：" + initializeError);
            if (onInitialized != null) onInitialized(initialized, initializeError);
            if (initialized && !stopping) SpeakLifecycleAnnouncement(BuildStartupAnnouncement(), false);
            while (!stopping)
            {
                string text;
                if (!queue.TryDequeue(out text))
                {
                    signal.WaitOne(1000);
                    continue;
                }
                Interlocked.Decrement(ref queuedCount);
                try
                {
                    if (!channel.Speak(text, false)) AppLog.Write("语音通道朗读失败：" + channel.Name);
                    else AppLog.Write("语音请求已提交，通道=" + channel.Name + "，长度=" + text.Length);
                }
                catch (Exception ex)
                {
                    AppLog.Write("语音工作线程异常：" + ex.Message);
                }
                Thread.Sleep(50);
            }
        }

        private void SpeakLifecycleAnnouncement(string text, bool interrupt)
        {
            try
            {
                if (!channel.Speak(text, interrupt))
                {
                    AppLog.Write("生命周期提示朗读失败：" + text + "，通道=" + channel.Name);
                    return;
                }
                AppLog.Write("生命周期提示已提交：" + text + "，通道=" + channel.Name);
                // Screen-reader APIs usually acknowledge before audio playback completes.
                // Keep the process alive long enough for these short lifecycle prompts.
                Thread.Sleep(interrupt ? 500 : 0);
            }
            catch (Exception ex)
            {
                AppLog.Write("生命周期提示异常：" + ex.Message);
            }
        }

        private static string BuildStartupAnnouncement()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            string value = version == null
                ? "未知版本"
                : version.Major + "." + version.Minor + "." + Math.Max(0, version.Build) + "." + Math.Max(0, version.Revision);
            return "微信消息助手 " + value;
        }
    }
}
