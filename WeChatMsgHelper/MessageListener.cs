using System;
using System.Collections.Generic;
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

        private readonly MessageExtractor extractor;
        private readonly Action<MessageData> onMessage;
        private readonly Action<MessageData> onConversationOpened;
        private readonly Action<Action> dispatch;
        private readonly System.Windows.Forms.Timer pollTimer;
        private readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        private readonly object bindingLock = new object();
        private readonly AutomationPropertyChangedEventHandler propertyHandler;
        private readonly StructureChangedEventHandler structureHandler;
        private int extractRunning;
        private int extractAgain;
        private volatile bool suspended;
        private volatile bool disposed;

        internal MessageListener(MessageExtractor extractor, AppConfig config, Action<MessageData> onMessage,
            Action<MessageData> onConversationOpened, Action<Action> dispatch)
        {
            this.extractor = extractor;
            this.onMessage = onMessage;
            this.onConversationOpened = onConversationOpened;
            this.dispatch = dispatch;
            propertyHandler = OnPropertyChanged;
            structureHandler = OnStructureChanged;
            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = config.PollIntervalMs;
            pollTimer.Tick += delegate { QueueExtract(); };
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

        internal void Suspend()
        {
            suspended = true;
            pollTimer.Stop();
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
                Automation.AddStructureChangedEventHandler(binding.MessageList, TreeScope.Subtree, structureHandler);
                binding.MessageListSubscribed = true;
                SubscribeInput(binding);
                lock (bindingLock) bindings[binding.Key] = binding;
                AppLog.Write("会话监听已加入：" + binding.Key);
                QueueExtract();
            }
            catch (Exception ex)
            {
                Unsubscribe(binding);
                AppLog.Write("订阅会话失败：" + ex.Message);
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
            if (input != null) NoteInputText(input);
        }

        private void OnStructureChanged(object sender, StructureChangedEventArgs args)
        {
            if (!suspended && !disposed) QueueExtract();
        }

        private void QueueExtract()
        {
            if (suspended || disposed) return;
            if (Interlocked.CompareExchange(ref extractRunning, 1, 0) != 0)
            {
                Interlocked.Exchange(ref extractAgain, 1);
                return;
            }
            Thread worker = new Thread(ExtractWorker);
            worker.IsBackground = true;
            worker.Name = "WeChatMsgHelper Message Extractor";
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        private void ExtractWorker()
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref extractAgain, 0);
                    if (suspended || disposed) break;
                    List<Binding> snapshot;
                    lock (bindingLock) snapshot = new List<Binding>(bindings.Values);
                    ExtractAndEmit(snapshot);
                }
                while (Interlocked.Exchange(ref extractAgain, 0) != 0 && !suspended && !disposed);
            }
            finally
            {
                Interlocked.Exchange(ref extractRunning, 0);
                if (Interlocked.Exchange(ref extractAgain, 0) != 0 && !suspended && !disposed) QueueExtract();
            }
        }

        private void ExtractAndEmit(IList<Binding> snapshot)
        {
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
            dispatch(delegate { RemoveBinding(binding.Key); });
        }

        private void SampleInputText(Binding binding)
        {
            if (Discovery.IsAvailable(binding.Input)) NoteInputText(binding.Input);
        }

        private void NoteInputText(AutomationElement input)
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
                extractor.NoteInputText(text);
            }
            catch { }
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
