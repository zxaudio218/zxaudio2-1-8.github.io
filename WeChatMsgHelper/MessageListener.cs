using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace WeChatMsgHelper
{
    internal sealed class MessageListener : IDisposable
    {
        private sealed class Binding
        {
            internal string Key;
            internal AutomationElement Root;
            internal AutomationElement MessageList;
            internal AutomationElement Input;
            internal int Handle;
            internal bool Ready;
            internal bool AnnounceWhenReady;
            internal bool MessageListSubscribed;
            internal bool InputSubscribed;
            internal bool Removed;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr windowHandle, uint command);

        private const uint GwOwner = 4;
        private const int StructureEventDelayMs = 120;
        private const int MinimumExtractSpacingMs = 100;
        private const int SlowExtractThresholdMs = 750;
        private const int SlowExtractBackoffMs = 2000;
        private static readonly long SlowExtractLogIntervalTicks = TimeSpan.FromSeconds(30).Ticks;

        private readonly MessageExtractor extractor;
        private readonly Action<MessageData> onMessage;
        private readonly Action<MessageData> onConversationOpened;
        private readonly Action<Action> dispatch;
        private readonly Action requestRebind;
        private readonly System.Windows.Forms.Timer pollTimer;
        private readonly System.Threading.Timer extractDelayTimer;
        private readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        private readonly object bindingLock = new object();
        private readonly AutomationPropertyChangedEventHandler propertyHandler;
        private readonly StructureChangedEventHandler structureHandler;
        private int extractRunning;
        private int extractAgain;
        private int extractScheduled;
        private long nextExtractAllowedTicks;
        private long lastSlowExtractLogTicks;
        private string lastForegroundBindingKey = string.Empty;
        private volatile bool suspended;
        private volatile bool disposed;

        internal MessageListener(MessageExtractor extractor, AppConfig config, Action<MessageData> onMessage,
            Action<MessageData> onConversationOpened, Action<Action> dispatch, Action requestRebind)
        {
            this.extractor = extractor;
            this.onMessage = onMessage;
            this.onConversationOpened = onConversationOpened;
            this.dispatch = dispatch;
            this.requestRebind = requestRebind;
            propertyHandler = OnPropertyChanged;
            structureHandler = OnStructureChanged;
            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = config.PollIntervalMs;
            pollTimer.Tick += delegate { QueueExtract(); };
            extractDelayTimer = new System.Threading.Timer(delegate
            {
                Interlocked.Exchange(ref extractScheduled, 0);
                QueueExtract();
            }, null, Timeout.Infinite, Timeout.Infinite);
            PollEnabled = config.PollFallback;
        }

        internal bool PollEnabled { get; set; }
        internal bool IsBound { get { lock (bindingLock) return bindings.Count > 0; } }
        internal int BoundCount { get { lock (bindingLock) return bindings.Count; } }

        internal void Synchronize(IList<DiscoveredWechatRoot> roots, bool announceOpenedChats)
        {
            Dictionary<string, DiscoveredWechatRoot> candidates = new Dictionary<string, DiscoveredWechatRoot>(StringComparer.Ordinal);
            foreach (DiscoveredWechatRoot root in roots)
                if (root != null && !string.IsNullOrEmpty(root.Key)) candidates[root.Key] = root;

            List<string> staleKeys = new List<string>();
            lock (bindingLock)
                foreach (KeyValuePair<string, Binding> pair in bindings)
                    if (!candidates.ContainsKey(pair.Key)) staleKeys.Add(pair.Key);
            foreach (string key in staleKeys) RemoveBinding(key);

            foreach (KeyValuePair<string, DiscoveredWechatRoot> pair in candidates)
            {
                Binding existing;
                lock (bindingLock) bindings.TryGetValue(pair.Key, out existing);
                if (existing == null)
                {
                    AddBinding(pair.Value, announceOpenedChats);
                    continue;
                }

                existing.Root = pair.Value.Root;
                existing.Handle = pair.Value.Handle;
                if (!Discovery.IsAvailable(existing.MessageList))
                {
                    RemoveBinding(pair.Key);
                    AddBinding(pair.Value, announceOpenedChats);
                    continue;
                }
                if (Discovery.IsAvailable(pair.Value.Input) &&
                    (!existing.InputSubscribed || !Discovery.IsAvailable(existing.Input) || !SameElement(existing.Input, pair.Value.Input)))
                    ReplaceInput(existing, pair.Value.Input);
            }

            if (PollEnabled && IsBound) pollTimer.Start();
            else pollTimer.Stop();
        }

        internal void UpdatePolling(bool enabled, int interval)
        {
            PollEnabled = enabled;
            pollTimer.Interval = Math.Max(200, Math.Min(5000, interval));
            if (IsBound && PollEnabled) pollTimer.Start();
            else pollTimer.Stop();
        }

        internal void NoteEmojiSent(string name)
        {
            string scope = FindActiveBindingKey();
            if (scope.Length == 0)
            {
                AppLog.Write("检测到表情激活动作，但未找到当前微信会话");
                return;
            }
            extractor.NoteEmojiSent(scope, name);
            AppLog.Write("已记录表情发送动作，会话=" + scope + "，名称=" + (name ?? string.Empty));
            QueueExtract();
        }

        internal void Suspend()
        {
            suspended = true;
            pollTimer.Stop();
            CancelScheduledExtract();
            Interlocked.Exchange(ref extractAgain, 0);
        }

        internal void Resume()
        {
            suspended = false;
            if (IsBound && PollEnabled) pollTimer.Start();
            QueueExtract();
        }

        internal void Stop()
        {
            suspended = true;
            pollTimer.Stop();
            CancelScheduledExtract();
            List<string> keys;
            lock (bindingLock) keys = new List<string>(bindings.Keys);
            foreach (string key in keys) RemoveBinding(key);
            Interlocked.Exchange(ref extractAgain, 0);
        }

        internal bool ReadLatest(out MessageData data)
        {
            data = null;
            int foreground = unchecked((int)GetForegroundWindow().ToInt64());
            foreach (Binding binding in OrderedForManualRead(foreground))
            {
                try
                {
                    if (extractor.TryGetLatestFromList(binding.MessageList, false, binding.Key, out data) && data != null) return true;
                }
                catch { }
            }
            return false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stop();
            pollTimer.Dispose();
            extractDelayTimer.Dispose();
        }

        private void AddBinding(DiscoveredWechatRoot discovered, bool announce)
        {
            if (discovered == null || discovered.MessageList == null) return;
            Binding binding = new Binding
            {
                Key = discovered.Key,
                Root = discovered.Root,
                MessageList = discovered.MessageList,
                Input = discovered.Input,
                Handle = discovered.Handle,
                AnnounceWhenReady = announce
            };
            try
            {
                Automation.AddStructureChangedEventHandler(binding.MessageList, TreeScope.Children, structureHandler);
                binding.MessageListSubscribed = true;
                SubscribeInput(binding);
                lock (bindingLock) bindings[binding.Key] = binding;
                AppLog.Write("会话监听已加入：" + binding.Key);
                QueueExtract();
            }
            catch (Exception ex)
            {
                Unsubscribe(binding);
                AppLog.Write("订阅会话失败：" + ex.GetType().Name + "，HRESULT=0x" + ex.HResult.ToString("X8") + "，" + ex.Message);
            }
        }

        private void RemoveBinding(string key)
        {
            Binding binding;
            lock (bindingLock)
            {
                if (!bindings.TryGetValue(key, out binding)) return;
                bindings.Remove(key);
                binding.Removed = true;
            }
            Unsubscribe(binding);
            AppLog.Write("会话监听已移除：" + key);
        }

        private void SubscribeInput(Binding binding)
        {
            if (!Discovery.IsAvailable(binding.Input)) return;
            Automation.AddAutomationPropertyChangedEventHandler(binding.Input, TreeScope.Element, propertyHandler,
                ValuePattern.ValueProperty, AutomationElement.NameProperty);
            binding.InputSubscribed = true;
        }

        private void ReplaceInput(Binding binding, AutomationElement input)
        {
            if (binding.InputSubscribed)
            {
                try { Automation.RemoveAutomationPropertyChangedEventHandler(binding.Input, propertyHandler); }
                catch { }
            }
            binding.InputSubscribed = false;
            binding.Input = input;
            try { SubscribeInput(binding); }
            catch (Exception ex) { AppLog.Write("重新订阅输入框失败：" + ex.Message); }
        }

        private void Unsubscribe(Binding binding)
        {
            if (binding == null) return;
            if (binding.InputSubscribed)
            {
                try { Automation.RemoveAutomationPropertyChangedEventHandler(binding.Input, propertyHandler); }
                catch { }
                binding.InputSubscribed = false;
            }
            if (binding.MessageListSubscribed)
            {
                try { Automation.RemoveStructureChangedEventHandler(binding.MessageList, structureHandler); }
                catch { }
                binding.MessageListSubscribed = false;
            }
        }

        private IEnumerable<Binding> OrderedForManualRead(int foreground)
        {
            List<Binding> snapshot;
            lock (bindingLock) snapshot = new List<Binding>(bindings.Values);
            foreach (Binding binding in snapshot) if (!binding.Removed && binding.Handle == foreground) yield return binding;
            foreach (Binding binding in snapshot) if (!binding.Removed && binding.Handle != foreground) yield return binding;
        }

        private void OnPropertyChanged(object sender, AutomationPropertyChangedEventArgs args)
        {
            if (suspended || disposed) return;
            AutomationElement input = sender as AutomationElement;
            if (input == null) return;
            Binding binding = FindBindingForInput(input);
            if (binding == null) return;
            if (args.Property == ValuePattern.ValueProperty && args.NewValue is string)
                extractor.NoteInputText(binding.Key, (string)args.NewValue);
            else
                NoteInputText(binding, input);
        }

        private void OnStructureChanged(object sender, StructureChangedEventArgs args)
        {
            if (!suspended && !disposed) ScheduleExtract(StructureEventDelayMs);
        }

        private void QueueExtract()
        {
            if (suspended || disposed) return;
            long nowTicks = DateTime.UtcNow.Ticks;
            long allowedTicks = Interlocked.Read(ref nextExtractAllowedTicks);
            if (allowedTicks > nowTicks)
            {
                ScheduleExtract(ToDelayMilliseconds(allowedTicks - nowTicks));
                return;
            }
            if (Interlocked.CompareExchange(ref extractRunning, 1, 0) != 0)
            {
                Interlocked.Exchange(ref extractAgain, 1);
                return;
            }
            if (!ThreadPool.QueueUserWorkItem(delegate { ExtractWorker(); }))
                Interlocked.Exchange(ref extractRunning, 0);
        }

        private void ExtractWorker()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                Interlocked.Exchange(ref extractAgain, 0);
                if (suspended || disposed) return;
                List<Binding> snapshot;
                lock (bindingLock) snapshot = new List<Binding>(bindings.Values);
                ExtractAndEmit(snapshot);
            }
            finally
            {
                stopwatch.Stop();
                int spacing = stopwatch.ElapsedMilliseconds >= SlowExtractThresholdMs
                    ? SlowExtractBackoffMs
                    : MinimumExtractSpacingMs;
                Interlocked.Exchange(ref nextExtractAllowedTicks, DateTime.UtcNow.AddMilliseconds(spacing).Ticks);
                if (stopwatch.ElapsedMilliseconds >= SlowExtractThresholdMs) LogSlowExtract(stopwatch.ElapsedMilliseconds);
                Interlocked.Exchange(ref extractRunning, 0);
                if (Interlocked.Exchange(ref extractAgain, 0) != 0 && !suspended && !disposed)
                    ScheduleExtract(spacing);
            }
        }

        private void ScheduleExtract(int delayMilliseconds)
        {
            if (suspended || disposed) return;
            if (Interlocked.CompareExchange(ref extractScheduled, 1, 0) != 0) return;
            try
            {
                extractDelayTimer.Change(Math.Max(1, delayMilliseconds), Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                Interlocked.Exchange(ref extractScheduled, 0);
            }
        }

        private void CancelScheduledExtract()
        {
            Interlocked.Exchange(ref extractScheduled, 0);
            try { extractDelayTimer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        private void LogSlowExtract(long elapsedMilliseconds)
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            long previousTicks = Interlocked.Read(ref lastSlowExtractLogTicks);
            if (previousTicks != 0 && nowTicks - previousTicks < SlowExtractLogIntervalTicks) return;
            if (Interlocked.CompareExchange(ref lastSlowExtractLogTicks, nowTicks, previousTicks) != previousTicks) return;
            AppLog.Write("微信消息扫描耗时=" + elapsedMilliseconds + "ms，已暂缓扫描以降低对微信进程的影响");
        }

        private static int ToDelayMilliseconds(long ticks)
        {
            double milliseconds = TimeSpan.FromTicks(Math.Max(0, ticks)).TotalMilliseconds;
            return (int)Math.Max(1, Math.Min(SlowExtractBackoffMs, Math.Ceiling(milliseconds)));
        }

        private void ExtractAndEmit(IList<Binding> snapshot)
        {
            RememberForegroundBinding(snapshot);
            foreach (Binding binding in snapshot)
            {
                if (binding.Removed || suspended || disposed) continue;
                try
                {
                    SampleInputText(binding);
                    if (!binding.Ready)
                    {
                        binding.Ready = extractor.PrimeList(binding.MessageList, binding.Key);
                        if (!binding.Ready) continue;
                        if (binding.AnnounceWhenReady)
                        {
                            binding.AnnounceWhenReady = false;
                            MessageData latest;
                            if (extractor.TryGetLatestFromList(binding.MessageList, false, binding.Key, out latest) && latest != null)
                                DispatchConversationOpened(binding, latest);
                        }
                        continue;
                    }
                    foreach (MessageData message in extractor.GetNewMessagesFromList(binding.MessageList, binding.Key))
                        DispatchMessage(binding, message);
                }
                catch (ElementNotAvailableException ex) { Invalidate(binding, ex.Message); }
                catch (InvalidOperationException ex) { Invalidate(binding, ex.Message); }
                catch (Exception ex) { AppLog.Write("提取会话消息失败：" + ex.Message); }
            }
        }

        private void DispatchMessage(Binding binding, MessageData message)
        {
            dispatch(delegate
            {
                if (!binding.Removed && !suspended && !disposed) onMessage(message);
            });
        }

        private void DispatchConversationOpened(Binding binding, MessageData message)
        {
            dispatch(delegate
            {
                if (!binding.Removed && !suspended && !disposed) onConversationOpened(message);
            });
        }

        private void Invalidate(Binding binding, string reason)
        {
            if (binding.Removed) return;
            AppLog.Write("微信消息列表元素失效，准备重绑：" + reason);
            dispatch(delegate
            {
                RemoveBinding(binding.Key);
                if (requestRebind != null) requestRebind();
            });
        }

        private void SampleInputText(Binding binding)
        {
            if (Discovery.IsAvailable(binding.Input)) NoteInputText(binding, binding.Input);
        }

        private void NoteInputText(Binding binding, AutomationElement input)
        {
            try
            {
                string text = input.Current.Name;
                try
                {
                    ValuePattern valuePattern = input.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
                    if (valuePattern != null) text = valuePattern.Current.Value;
                }
                catch { }
                extractor.NoteInputText(binding.Key, text);
            }
            catch { }
        }

        private Binding FindBindingForInput(AutomationElement input)
        {
            List<Binding> snapshot;
            lock (bindingLock) snapshot = new List<Binding>(bindings.Values);
            foreach (Binding binding in snapshot)
                if (!binding.Removed && binding.InputSubscribed && SameElement(binding.Input, input)) return binding;
            return null;
        }

        private string FindActiveBindingKey()
        {
            List<Binding> snapshot;
            lock (bindingLock) snapshot = new List<Binding>(bindings.Values);
            int handle = unchecked((int)GetForegroundWindow().ToInt64());
            for (int depth = 0; depth < 8 && handle != 0; depth++)
            {
                foreach (Binding binding in snapshot)
                {
                    if (binding.Removed || binding.Handle != handle) continue;
                    lastForegroundBindingKey = binding.Key;
                    return binding.Key;
                }
                handle = unchecked((int)GetWindow(new IntPtr(handle), GwOwner).ToInt64());
            }

            foreach (Binding binding in snapshot)
                if (!binding.Removed && string.Equals(binding.Key, lastForegroundBindingKey, StringComparison.Ordinal))
                    return binding.Key;
            return string.Empty;
        }

        private void RememberForegroundBinding(IList<Binding> snapshot)
        {
            int foreground = unchecked((int)GetForegroundWindow().ToInt64());
            foreach (Binding binding in snapshot)
            {
                if (binding.Removed || binding.Handle != foreground) continue;
                lastForegroundBindingKey = binding.Key;
                return;
            }
        }

        private static bool SameElement(AutomationElement left, AutomationElement right)
        {
            if (left == null || right == null) return false;
            string leftId = MessageExtractor.GetElementRuntimeId(left);
            string rightId = MessageExtractor.GetElementRuntimeId(right);
            return leftId.Length > 0 && string.Equals(leftId, rightId, StringComparison.Ordinal);
        }
    }
}
