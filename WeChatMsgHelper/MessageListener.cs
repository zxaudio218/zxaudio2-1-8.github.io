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
            internal AutomationElement Input;
            internal int Handle;
            internal DateTime WarmupUntil;
            internal bool Ready;
            internal bool AnnounceWhenReady;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private readonly MessageExtractor extractor;
        private readonly Action<MessageData> onMessage;
        private readonly Action<MessageData> onConversationOpened;
        private readonly Action<Action> dispatch;
        private readonly System.Windows.Forms.Timer pollTimer;
        private readonly System.Windows.Forms.Timer eventTimer;
        private readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        private readonly AutomationPropertyChangedEventHandler propertyHandler;
        private readonly StructureChangedEventHandler structureHandler;
        private int extractPending;
        private bool suspended;

        internal MessageListener(
            MessageExtractor extractor,
            AppConfig config,
            Action<MessageData> onMessage,
            Action<MessageData> onConversationOpened,
            Action<Action> dispatch)
        {
            this.extractor = extractor;
            this.onMessage = onMessage;
            this.onConversationOpened = onConversationOpened;
            this.dispatch = dispatch;
            propertyHandler = OnPropertyChanged;
            structureHandler = OnStructureChanged;
            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = config.PollIntervalMs;
            pollTimer.Tick += delegate { ExtractAndEmit(); };
            eventTimer = new System.Windows.Forms.Timer();
            eventTimer.Interval = 60;
            eventTimer.Tick += delegate
            {
                eventTimer.Stop();
                Interlocked.Exchange(ref extractPending, 0);
                ExtractAndEmit();
            };
            PollEnabled = config.PollFallback;
        }

        internal bool PollEnabled { get; set; }
        internal bool IsBound { get { return bindings.Count > 0; } }
        internal int BoundCount { get { return bindings.Count; } }

        internal void Synchronize(IList<AutomationElement> roots, bool announceOpenedChats)
        {
            Dictionary<string, AutomationElement> candidates = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
            foreach (AutomationElement root in roots)
            {
                string key = GetBindingKey(root);
                if (key.Length > 0) candidates[key] = root;
            }

            List<string> staleKeys = new List<string>();
            foreach (KeyValuePair<string, Binding> pair in bindings)
                if (!candidates.ContainsKey(pair.Key)) staleKeys.Add(pair.Key);
            foreach (string key in staleKeys) RemoveBinding(key);

            foreach (KeyValuePair<string, AutomationElement> pair in candidates)
            {
                Binding existing;
                if (bindings.TryGetValue(pair.Key, out existing))
                {
                    existing.Root = pair.Value;
                    if (!Discovery.IsAvailable(existing.Input)) existing.Input = FindInputField(pair.Value);
                    continue;
                }
                AddBinding(pair.Key, pair.Value, announceOpenedChats);
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
            eventTimer.Stop();
            Interlocked.Exchange(ref extractPending, 0);
        }

        internal void Resume()
        {
            suspended = false;
            if (IsBound && PollEnabled) pollTimer.Start();
        }

        internal void Stop()
        {
            pollTimer.Stop();
            eventTimer.Stop();
            foreach (string key in new List<string>(bindings.Keys)) RemoveBinding(key);
            Interlocked.Exchange(ref extractPending, 0);
        }

        internal bool ReadLatest(out MessageData data)
        {
            data = null;
            int foreground = unchecked((int)GetForegroundWindow().ToInt64());
            foreach (Binding binding in OrderedForManualRead(foreground))
            {
                try
                {
                    if (extractor.TryGetLatest(binding.Root, false, out data) && data != null) return true;
                }
                catch { }
            }
            return false;
        }

        public void Dispose()
        {
            Stop();
            pollTimer.Dispose();
            eventTimer.Dispose();
        }

        private void AddBinding(string key, AutomationElement root, bool announce)
        {
            try
            {
                Binding binding = new Binding();
                binding.Key = key;
                binding.Root = root;
                binding.Input = FindInputField(root);
                binding.Handle = root.Current.NativeWindowHandle;
                Automation.AddAutomationPropertyChangedEventHandler(
                    root,
                    TreeScope.Descendants,
                    propertyHandler,
                    ValuePattern.ValueProperty,
                    RangeValuePattern.ValueProperty);
                Automation.AddStructureChangedEventHandler(root, TreeScope.Descendants, structureHandler);
                binding.WarmupUntil = DateTime.UtcNow.AddMilliseconds(750);
                binding.AnnounceWhenReady = announce;
                bindings[key] = binding;
                AppLog.Write("会话监听已加入：" + key);
            }
            catch (Exception ex) { AppLog.Write("订阅会话失败：" + ex.Message); }
        }

        private void RemoveBinding(string key)
        {
            Binding binding;
            if (!bindings.TryGetValue(key, out binding)) return;
            try { Automation.RemoveAutomationPropertyChangedEventHandler(binding.Root, propertyHandler); }
            catch { }
            try { Automation.RemoveStructureChangedEventHandler(binding.Root, structureHandler); }
            catch { }
            bindings.Remove(key);
            AppLog.Write("会话监听已移除：" + key);
        }

        private IEnumerable<Binding> OrderedForManualRead(int foreground)
        {
            foreach (Binding binding in bindings.Values) if (binding.Handle == foreground) yield return binding;
            foreach (Binding binding in bindings.Values) if (binding.Handle != foreground) yield return binding;
        }

        private void OnPropertyChanged(object sender, AutomationPropertyChangedEventArgs args)
        {
            if (suspended) return;
            AutomationElement element = sender as AutomationElement;
            if (element == null) return;
            AutomationElement input = GetInputField(element);
            if (input != null)
            {
                NoteInputText(input);
                return;
            }
            if (!IsInsideMessageList(element)) return;
            ScheduleExtract();
        }

        private void OnStructureChanged(object sender, StructureChangedEventArgs args)
        {
            if (suspended) return;
            AutomationElement element = sender as AutomationElement;
            if (element == null || !IsInsideMessageList(element)) return;
            ScheduleExtract();
        }

        private static bool IsInsideMessageList(AutomationElement element)
        {
            try
            {
                TreeWalker walker = TreeWalker.RawViewWalker;
                AutomationElement current = element;
                for (int depth = 0; depth < 12 && current != null; depth++)
                {
                    if (current.Current.AutomationId == "chat_message_list") return true;
                    current = walker.GetParent(current);
                }
            }
            catch { }
            return false;
        }

        private static AutomationElement GetInputField(AutomationElement element)
        {
            try
            {
                TreeWalker walker = TreeWalker.RawViewWalker;
                AutomationElement current = element;
                for (int depth = 0; depth < 5 && current != null; depth++)
                {
                    if (current.Current.AutomationId == "chat_input_field") return current;
                    current = walker.GetParent(current);
                }
            }
            catch { }
            return null;
        }

        private void ScheduleExtract()
        {
            if (suspended) return;
            if (Interlocked.Exchange(ref extractPending, 1) != 0) return;
            dispatch(delegate
            {
                if (suspended) { Interlocked.Exchange(ref extractPending, 0); return; }
                eventTimer.Stop();
                eventTimer.Start();
            });
        }

        private void ExtractAndEmit()
        {
            if (suspended) return;
            foreach (Binding binding in new List<Binding>(bindings.Values))
            {
                try
                {
                    SampleInputText(binding);
                    if (!binding.Ready)
                    {
                        if (DateTime.UtcNow < binding.WarmupUntil) continue;
                        binding.Ready = extractor.Prime(binding.Root, binding.Key);
                        if (!binding.Ready) continue;
                        if (binding.AnnounceWhenReady)
                        {
                            binding.AnnounceWhenReady = false;
                            MessageData latest;
                            if (extractor.TryGetLatest(binding.Root, false, out latest) && latest != null) onConversationOpened(latest);
                        }
                        continue;
                    }
                    foreach (MessageData data in extractor.GetNewMessages(binding.Root, binding.Key)) onMessage(data);
                }
                catch (ElementNotAvailableException ex)
                {
                    AppLog.Write("微信消息列表元素失效，准备重绑：" + ex.Message);
                    Rebind(binding, false);
                }
                catch (InvalidOperationException ex)
                {
                    AppLog.Write("微信消息列表访问失败，准备重绑：" + ex.Message);
                    Rebind(binding, false);
                }
                catch (Exception ex) { AppLog.Write("提取会话消息失败：" + ex.Message); }
            }
        }

        private void SampleInputText(Binding binding)
        {
            try
            {
                if (!Discovery.IsAvailable(binding.Input)) binding.Input = FindInputField(binding.Root);
                if (binding.Input != null) NoteInputText(binding.Input);
            }
            catch { binding.Input = null; }
        }

        private static AutomationElement FindInputField(AutomationElement root)
        {
            try
            {
                return root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "chat_input_field"));
            }
            catch { return null; }
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

        private void Rebind(Binding binding, bool announce)
        {
            if (binding == null || !bindings.ContainsKey(binding.Key)) return;
            string key = binding.Key;
            AutomationElement root = null;
            try
            {
                AutomationElement candidate = AutomationElement.FromHandle(new IntPtr(binding.Handle));
                if (GetBindingKey(candidate) == key) root = candidate;
            }
            catch { }
            RemoveBinding(key);
            if (root != null) AddBinding(key, root, announce);
        }

        private static string GetBindingKey(AutomationElement root)
        {
            try
            {
                AutomationElement list = MessageExtractor.FindMessageList(root);
                if (list == null) return string.Empty;
                string listRuntimeId = MessageExtractor.GetElementRuntimeId(list);
                if (listRuntimeId.Length == 0) return string.Empty;
                return root.Current.NativeWindowHandle + ":" + listRuntimeId;
            }
            catch { return string.Empty; }
        }
    }
}
