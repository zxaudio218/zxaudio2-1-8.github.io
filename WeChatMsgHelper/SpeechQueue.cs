using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using WeChatMsgHelper.Channels;

namespace WeChatMsgHelper
{
    internal sealed class SpeechQueue : IDisposable
    {
        private const int MaxSpeechChunkLength = 1000;
        private const int PreferredSplitStart = 600;

        private sealed class SpeechRequest
        {
            internal string Text;
            internal int StopVersion;
        }

        private readonly ConcurrentQueue<SpeechRequest> queue = new ConcurrentQueue<SpeechRequest>();
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private readonly Thread worker;
        private readonly ISpeechChannel channel;
        private readonly Action<bool, string> onInitialized;
        private volatile bool stopping;
        private bool disposed;
        private int queuedCount;
        private int stopVersion;

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
            queue.Enqueue(new SpeechRequest
            {
                Text = text,
                StopVersion = Thread.VolatileRead(ref stopVersion)
            });
            int count = Interlocked.Increment(ref queuedCount);
            while (count > 200)
            {
                SpeechRequest ignored;
                if (!queue.TryDequeue(out ignored)) break;
                count = Interlocked.Decrement(ref queuedCount);
            }
            signal.Set();
        }

        internal void StopAndClear()
        {
            Interlocked.Increment(ref stopVersion);
            SpeechRequest ignored;
            while (queue.TryDequeue(out ignored)) Interlocked.Decrement(ref queuedCount);
            try { channel.Stop(); }
            catch (Exception ex) { AppLog.Write("停止当前朗读失败：" + ex.Message); }
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

        internal void AnnounceProcessExit(string announcement)
        {
            if (disposed || string.IsNullOrWhiteSpace(announcement)) return;
            stopping = true;
            try { channel.Stop(); }
            catch { }
            signal.Set();
            SpeakLifecycleAnnouncement(announcement, true);
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
                SpeechRequest request;
                if (!queue.TryDequeue(out request))
                {
                    signal.WaitOne(1000);
                    continue;
                }
                Interlocked.Decrement(ref queuedCount);
                if (request.StopVersion != Thread.VolatileRead(ref stopVersion)) continue;
                try
                {
                    IList<string> chunks = SplitText(request.Text);
                    bool submitted = true;
                    for (int index = 0; index < chunks.Count; index++)
                    {
                        if (stopping || request.StopVersion != Thread.VolatileRead(ref stopVersion))
                        {
                            submitted = false;
                            break;
                        }
                        if (!channel.Speak(chunks[index], false))
                        {
                            AppLog.Write("语音通道朗读失败：" + channel.Name + "，分段=" + (index + 1) + "/" + chunks.Count);
                            submitted = false;
                            break;
                        }
                        if (index + 1 < chunks.Count) Thread.Sleep(50);
                    }
                    if (submitted)
                        AppLog.Write("语音请求已提交，通道=" + channel.Name + "，长度=" + request.Text.Length + "，分段=" + chunks.Count);
                }
                catch (Exception ex)
                {
                    AppLog.Write("语音工作线程异常：" + ex.Message);
                }
                Thread.Sleep(50);
            }
        }

        private static IList<string> SplitText(string text)
        {
            List<string> chunks = new List<string>();
            int offset = 0;
            while (text.Length - offset > MaxSpeechChunkLength)
            {
                int length = FindSplitLength(text, offset);
                if (offset + length < text.Length && length > 0 &&
                    char.IsHighSurrogate(text[offset + length - 1]) && char.IsLowSurrogate(text[offset + length]))
                    length--;
                AddChunk(chunks, text.Substring(offset, length));
                offset += length;
            }
            AddChunk(chunks, text.Substring(offset));
            return chunks;
        }

        private static int FindSplitLength(string text, int offset)
        {
            int maximum = Math.Min(MaxSpeechChunkLength, text.Length - offset);
            for (int index = maximum - 1; index >= PreferredSplitStart; index--)
            {
                char value = text[offset + index];
                if (char.IsWhiteSpace(value) || value == '。' || value == '！' || value == '？' ||
                    value == '；' || value == '，' || value == '.' || value == '!' ||
                    value == '?' || value == ';' || value == ',') return index + 1;
            }
            return maximum;
        }

        private static void AddChunk(ICollection<string> chunks, string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) chunks.Add(text);
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
            return "微信消息助手 " + UpdateReleaseNotes.CurrentVersion;
        }
    }
}
