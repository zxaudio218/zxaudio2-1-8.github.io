using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace WeChatMsgHelper
{
    internal enum MessageDirection
    {
        Unknown,
        Incoming,
        Outgoing
    }

    internal sealed class MessageData
    {
        internal string Key;
        internal string Text;
        internal MessageDirection Direction;
    }

    internal sealed class MessageExtractor
    {
        private readonly object syncRoot = new object();
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> order = new Queue<string>();
        private readonly bool readNonTextMessages;
        private sealed class InputSample
        {
            internal string Scope;
            internal string Text;
            internal DateTime At;
            internal bool Consumed;
        }
        private sealed class EmojiSendSample
        {
            internal string Scope;
            internal string Name;
            internal DateTime At;
        }
        private readonly Dictionary<string, InputSample> currentInputs = new Dictionary<string, InputSample>(StringComparer.Ordinal);
        private readonly List<InputSample> recentlySentInputs = new List<InputSample>();
        private readonly List<EmojiSendSample> recentlySentEmojis = new List<EmojiSendSample>();
        private int unknownDirectionDiagnosticsRemaining = 8;
        private int inputMatchDiagnosticsRemaining = 8;

        internal MessageExtractor(bool readNonTextMessages)
        {
            this.readNonTextMessages = readNonTextMessages;
        }

        internal void NoteInputText(string scope, string text)
        {
            string cleaned = Clean(text);
            string normalizedScope = scope ?? string.Empty;
            string normalizedText = NormalizeForMatch(cleaned);
            DateTime now = DateTime.UtcNow;
            lock (syncRoot)
            {
                RemoveExpiredInputSamples(now);
                InputSample current;
                currentInputs.TryGetValue(normalizedScope, out current);
                if (normalizedText.Length > 0)
                {
                    if (current == null)
                    {
                        current = new InputSample { Scope = normalizedScope };
                        currentInputs[normalizedScope] = current;
                    }
                    if (!string.Equals(current.Text, normalizedText, StringComparison.Ordinal)) current.Consumed = false;
                    current.Text = normalizedText;
                    current.At = now;
                    return;
                }

                if (current != null)
                {
                    currentInputs.Remove(normalizedScope);
                    if (!current.Consumed && !string.IsNullOrEmpty(current.Text))
                    {
                        current.At = now;
                        recentlySentInputs.Add(current);
                        while (recentlySentInputs.Count > 32) recentlySentInputs.RemoveAt(0);
                    }
                }
            }
        }

        internal bool Prime(AutomationElement root)
        {
            return Prime(root, string.Empty);
        }

        internal void NoteEmojiSent(string scope, string name)
        {
            DateTime now = DateTime.UtcNow;
            lock (syncRoot)
            {
                RemoveExpiredInputSamples(now);
                recentlySentEmojis.Add(new EmojiSendSample
                {
                    Scope = scope ?? string.Empty,
                    Name = name ?? string.Empty,
                    At = now
                });
                while (recentlySentEmojis.Count > 8) recentlySentEmojis.RemoveAt(0);
            }
        }

        internal bool Prime(AutomationElement root, string scope)
        {
            AutomationElement list = FindMessageList(root);
            return PrimeList(list, scope);
        }

        internal bool PrimeList(AutomationElement list, string scope)
        {
            if (list == null) return false;
            foreach (AutomationElement item in GetMessageItems(list))
            {
                string key = GetRuntimeId(item);
                if (key.Length == 0) continue;
                Remember(CreateSignature(scope, key, ReadMessageText(item)));
            }
            return true;
        }

        internal IList<MessageData> GetNewMessages(AutomationElement root)
        {
            return GetNewMessages(root, string.Empty);
        }

        internal IList<MessageData> GetNewMessages(AutomationElement root, string scope)
        {
            AutomationElement list = FindMessageList(root);
            return GetNewMessagesFromList(list, scope);
        }

        internal IList<MessageData> GetNewMessagesFromList(AutomationElement list, string scope)
        {
            List<MessageData> messages = new List<MessageData>();
            if (list == null) return messages;
            foreach (AutomationElement item in GetMessageItems(list))
            {
                string key = GetRuntimeId(item);
                if (key.Length == 0) continue;
                string text = ReadMessageText(item);
                // Qt can raise StructureChanged before the bubble name is populated.
                // Do not consume the RuntimeId until the message has readable content.
                if (text.Length == 0) continue;
                string signature = CreateSignature(scope, key, text);
                if (IsSeen(signature)) continue;
                Remember(signature);
                MessageDirection direction = GuessDirection(list, item, scope);
                if (direction == MessageDirection.Unknown) LogUnknownDirection(list, item, text);
                messages.Add(new MessageData { Key = key, Text = text, Direction = direction });
            }
            return messages;
        }

        internal IList<MessageData> CollectVisibleMessages(AutomationElement root, bool includeTimeSeparators)
        {
            List<MessageData> messages = new List<MessageData>();
            AutomationElement list = FindMessageList(root);
            if (list == null) return messages;
            try
            {
                AutomationElementCollection items = list.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                for (int index = 0; index < items.Count; index++)
                {
                    AutomationElement item = items[index];
                    string automationId = item.Current.AutomationId;
                    string className = item.Current.ClassName;
                    bool isMessage = automationId == "chat_message_list.qt_scrollarea_viewport.chat_bubble_item_view";
                    bool isTime = includeTimeSeparators && className == "mmui::ChatItemView";
                    if (!isMessage && !isTime) continue;
                    string key = GetRuntimeId(item);
                    string text = ReadMessageText(item);
                    if (key.Length == 0 || text.Length == 0) continue;
                    messages.Add(new MessageData { Key = key, Text = text, Direction = GuessDirection(list, item, string.Empty) });
                }
            }
            catch { }
            return messages;
        }

        internal bool TryGetLatest(AutomationElement root, bool deduplicate, out MessageData data)
        {
            return TryGetLatest(root, deduplicate, string.Empty, out data);
        }

        internal bool TryGetLatest(AutomationElement root, bool deduplicate, string scope, out MessageData data)
        {
            AutomationElement list = FindMessageList(root);
            return TryGetLatestFromList(list, deduplicate, scope, out data);
        }

        internal bool TryGetLatestFromList(AutomationElement list, bool deduplicate, string scope, out MessageData data)
        {
            data = null;
            if (list == null) return false;
            AutomationElement item = GetLatestItem(list);
            if (item == null) return false;

            string key = GetRuntimeId(item);
            if (key.Length == 0) return false;
            string text = ReadMessageText(item);
            string signature = CreateSignature(scope, key, text);
            if (deduplicate && IsSeen(signature)) return false;

            if (deduplicate) Remember(signature);
            data = new MessageData { Key = key, Text = text, Direction = GuessDirection(list, item, scope) };
            return true;
        }

        internal static AutomationElement FindMessageList(AutomationElement root)
        {
            if (root == null) return null;
            try
            {
                AutomationElement exact = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "chat_message_list"));
                if (exact != null) return exact;
                return root.FindFirst(TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "消息"),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List)));
            }
            catch { return null; }
        }

        internal static string GetElementRuntimeId(AutomationElement element)
        {
            return GetRuntimeId(element);
        }

        private static AutomationElement GetLatestItem(AutomationElement list)
        {
            IList<AutomationElement> items = GetMessageItems(list);
            return items.Count == 0 ? null : items[items.Count - 1];
        }

        private static IList<AutomationElement> GetMessageItems(AutomationElement list)
        {
            List<AutomationElement> result = new List<AutomationElement>();
            try
            {
                AutomationElementCollection items = list.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                for (int index = 0; index < items.Count; index++)
                {
                    AutomationElement item = items[index];
                    if (item.Current.AutomationId == "chat_message_list.qt_scrollarea_viewport.chat_bubble_item_view") result.Add(item);
                }
            }
            catch { }
            return result;
        }

        private static string ReadName(AutomationElement item)
        {
            try { return item.Current.Name ?? string.Empty; }
            catch { return string.Empty; }
        }

        private string ReadMessageText(AutomationElement item)
        {
            string text = Clean(ReadName(item));
            if (text.Length > 0) return text;
            if (!readNonTextMessages) return string.Empty;
            return InferNonText(item);
        }

        private static string CreateSignature(string scope, string key, string text)
        {
            // Qt reuses visible bubble elements, so RuntimeId alone cannot identify a new message.
            return (scope ?? string.Empty) + "\u001e" + key + "\u001f" + (text ?? string.Empty);
        }

        private MessageDirection GuessDirection(AutomationElement list, AutomationElement item, string scope)
        {
            try
            {
                string messageText = Clean(ReadName(item));
                if (MatchesRecentInput(scope, messageText)) return MessageDirection.Outgoing;
                if (MatchesRecentEmojiSend(scope, messageText)) return MessageDirection.Outgoing;
                string hints = ((item.Current.AutomationId ?? string.Empty) + " " + (item.Current.ClassName ?? string.Empty)).ToLowerInvariant();
                if (hints.Contains("send") || hints.Contains("out") || hints.Contains("right") || hints.Contains("self"))
                    return MessageDirection.Outgoing;
                if (hints.Contains("receive") || hints.Contains("incoming") || hints.Contains("left"))
                    return MessageDirection.Incoming;

                System.Windows.Rect listRect = list.Current.BoundingRectangle;
                System.Windows.Rect contentRect = FindMessageContentRectangle(listRect, item, messageText);
                if (listRect.Width <= 1 || contentRect.Width <= 1) return MessageDirection.Unknown;
                double midpoint = listRect.Left + listRect.Width * 0.5;
                double contentMidpoint = contentRect.Left + contentRect.Width * 0.5;
                double deadZone = listRect.Width * 0.06;
                if (contentMidpoint > midpoint + deadZone)
                    return MessageDirection.Outgoing;
                if (contentMidpoint < midpoint - deadZone)
                    return MessageDirection.Incoming;
            }
            catch { }
            return MessageDirection.Unknown;
        }

        private static System.Windows.Rect FindMessageContentRectangle(System.Windows.Rect listRect, AutomationElement item, string messageText)
        {
            System.Windows.Rect fallback = item.Current.BoundingRectangle;
            System.Windows.Rect best = fallback;
            int bestScore = fallback.Width < listRect.Width * 0.85 ? 1 : 0;
            double bestArea = fallback.Width * fallback.Height;
            string normalizedMessage = NormalizeForMatch(messageText);
            try
            {
                // Keep this traversal bounded. Unbounded UIA descendant scans can overload WeChat's provider.
                foreach (AutomationElement element in GetRawDescendants(item, 48))
                    ScoreContentCandidate(listRect, element, normalizedMessage, ref best, ref bestScore, ref bestArea);
            }
            catch { }
            return best;
        }

        private static void ScoreContentCandidate(System.Windows.Rect listRect, AutomationElement element,
            string normalizedMessage, ref System.Windows.Rect best, ref int bestScore, ref double bestArea)
        {
            AutomationElement.AutomationElementInformation current = element.Current;
            System.Windows.Rect bounds = current.BoundingRectangle;
            if (bounds.Width <= 1 || bounds.Height <= 1 || bounds.Width >= listRect.Width * 0.88) return;
            if (bounds.Right < listRect.Left || bounds.Left > listRect.Right) return;

            string name = Clean(current.Name);
            string normalizedName = NormalizeForMatch(name);
            string hints = ((current.AutomationId ?? string.Empty) + " " +
                (current.ClassName ?? string.Empty)).ToLowerInvariant();
            int score = 0;
            if (normalizedMessage.Length > 0 && normalizedName == normalizedMessage) score += 100;
            else if (normalizedName.Length > 0 && normalizedMessage.Length > 0 &&
                (normalizedName.Contains(normalizedMessage) || normalizedMessage.Contains(normalizedName))) score += 45;
            if (hints.Contains("bubble") || hints.Contains("avatar") || hints.Contains("message") ||
                hints.Contains("content") || hints.Contains("text") || hints.Contains("voice") || hints.Contains("image")) score += 20;
            ControlType type = current.ControlType;
            if (type == ControlType.Text || type == ControlType.Image || type == ControlType.Button || type == ControlType.Custom) score += 5;
            if (name.Length > 0) score += 5;

            double area = bounds.Width * bounds.Height;
            if (score > bestScore || (score == bestScore && area > bestArea))
            {
                best = bounds;
                bestScore = score;
                bestArea = area;
            }
        }

        private static IList<AutomationElement> GetRawDescendants(AutomationElement root, int maximumCount)
        {
            List<AutomationElement> result = new List<AutomationElement>();
            if (root == null || maximumCount <= 0) return result;
            try
            {
                TreeWalker walker = TreeWalker.RawViewWalker;
                Stack<AutomationElement> pending = new Stack<AutomationElement>();
                AutomationElement child = walker.GetFirstChild(root);
                while (child != null)
                {
                    pending.Push(child);
                    child = walker.GetNextSibling(child);
                }
                while (pending.Count > 0 && result.Count < maximumCount)
                {
                    AutomationElement element = pending.Pop();
                    result.Add(element);
                    child = walker.GetFirstChild(element);
                    while (child != null)
                    {
                        pending.Push(child);
                        child = walker.GetNextSibling(child);
                    }
                }
            }
            catch { }
            return result;
        }

        private void LogUnknownDirection(AutomationElement list, AutomationElement item, string messageText)
        {
            if (Interlocked.Decrement(ref unknownDirectionDiagnosticsRemaining) < 0) return;
            try
            {
                System.Windows.Rect listRect = list.Current.BoundingRectangle;
                System.Windows.Rect itemRect = item.Current.BoundingRectangle;
                string automationId = item.Current.AutomationId ?? string.Empty;
                string className = item.Current.ClassName ?? string.Empty;
                AppLog.Write("未知方向诊断：列表=" + FormatRectangle(listRect) + "，消息项=" + FormatRectangle(itemRect) +
                    "，消息长度=" + (messageText ?? string.Empty).Length +
                    "，AutomationId=" + automationId + "，ClassName=" + className);
            }
            catch (Exception ex)
            {
                AppLog.Write("未知方向诊断失败：" + ex.GetType().Name + "，HRESULT=0x" + ex.HResult.ToString("X8"));
            }
        }

        private static string FormatRectangle(System.Windows.Rect rectangle)
        {
            return Math.Round(rectangle.Left) + "," + Math.Round(rectangle.Top) + "," +
                Math.Round(rectangle.Width) + "x" + Math.Round(rectangle.Height);
        }

        private static string InferNonText(AutomationElement item)
        {
            try
            {
                IList<AutomationElement> descendants = GetRawDescendants(item, 24);
                for (int index = 0; index < descendants.Count; index++)
                {
                    string name = descendants[index].Current.Name ?? string.Empty;
                    if (name.IndexOf("语音", StringComparison.OrdinalIgnoreCase) >= 0) return "语音消息";
                    if (name.IndexOf("表情", StringComparison.OrdinalIgnoreCase) >= 0) return "表情";
                    if (name.IndexOf("图片", StringComparison.OrdinalIgnoreCase) >= 0) return "图片";
                }
            }
            catch { }
            return "非文本消息";
        }

        private static string GetRuntimeId(AutomationElement item)
        {
            try
            {
                int[] values = item.GetRuntimeId();
                if (values == null || values.Length == 0) return string.Empty;
                return string.Join("-", Array.ConvertAll(values, delegate(int value) { return value.ToString(); }));
            }
            catch { return string.Empty; }
        }

        private static string Clean(string value)
        {
            string text = (value ?? string.Empty).Replace('\u2028', '\n').Replace('\u2029', '\n').Trim();
            return Regex.Replace(text, "(?:\\r?\\n\\s*){3,}", Environment.NewLine + Environment.NewLine);
        }

        private bool MatchesRecentInput(string scope, string text)
        {
            string normalized = NormalizeForMatch(text);
            if (normalized.Length == 0) return false;
            DateTime now = DateTime.UtcNow;
            string normalizedScope = scope ?? string.Empty;
            lock (syncRoot)
            {
                RemoveExpiredInputSamples(now);
                InputSample current;
                if (currentInputs.TryGetValue(normalizedScope, out current) &&
                    now - current.At < TimeSpan.FromSeconds(30) &&
                    MatchesSentText(current.Text, normalized, now - current.At))
                {
                    current.Consumed = true;
                    if (!string.Equals(current.Text, normalized, StringComparison.Ordinal))
                        AppLog.Write("发送文本已按引用/@规则匹配，会话=" + normalizedScope);
                    return true;
                }

                bool hadCandidate = false;
                int candidateLength = 0;
                for (int index = recentlySentInputs.Count - 1; index >= 0; index--)
                {
                    InputSample sample = recentlySentInputs[index];
                    if (!string.Equals(sample.Scope, normalizedScope, StringComparison.Ordinal)) continue;
                    hadCandidate = true;
                    candidateLength = sample.Text == null ? 0 : sample.Text.Length;
                    if (!MatchesSentText(sample.Text, normalized, now - sample.At)) continue;
                    recentlySentInputs.RemoveAt(index);
                    if (!string.Equals(sample.Text, normalized, StringComparison.Ordinal))
                        AppLog.Write("发送文本已按引用/@规则匹配，会话=" + normalizedScope);
                    return true;
                }
                if (hadCandidate && inputMatchDiagnosticsRemaining > 0)
                {
                    inputMatchDiagnosticsRemaining--;
                    AppLog.Write("最近发送文本未匹配，会话=" + normalizedScope +
                        "，输入长度=" + candidateLength + "，消息长度=" + normalized.Length);
                }
            }
            return false;
        }

        private static bool MatchesSentText(string sentText, string messageText, TimeSpan age)
        {
            string sent = sentText ?? string.Empty;
            string message = messageText ?? string.Empty;
            if (sent.Length == 0 || message.Length == 0) return false;
            if (string.Equals(sent, message, StringComparison.Ordinal)) return true;
            if (age >= TimeSpan.FromSeconds(15)) return false;

            if (message.StartsWith(sent, StringComparison.Ordinal) ||
                message.EndsWith(sent, StringComparison.Ordinal)) return true;
            if (sent.StartsWith(message, StringComparison.Ordinal) ||
                sent.EndsWith(message, StringComparison.Ordinal))
                return message.Length >= 2 || age < TimeSpan.FromSeconds(3);

            int shorterLength = Math.Min(sent.Length, message.Length);
            return shorterLength >= 2 &&
                (message.IndexOf(sent, StringComparison.Ordinal) >= 0 ||
                 sent.IndexOf(message, StringComparison.Ordinal) >= 0);
        }

        private bool MatchesRecentEmojiSend(string scope, string text)
        {
            DateTime now = DateTime.UtcNow;
            string normalizedScope = scope ?? string.Empty;
            bool genericEmojiMessage = IsEmojiMessage(text);
            lock (syncRoot)
            {
                RemoveExpiredInputSamples(now);
                for (int index = 0; index < recentlySentEmojis.Count; index++)
                {
                    EmojiSendSample sample = recentlySentEmojis[index];
                    if (!string.Equals(sample.Scope, normalizedScope, StringComparison.Ordinal)) continue;
                    if (!genericEmojiMessage && !MatchesEmojiName(sample.Name, text)) continue;
                    recentlySentEmojis.RemoveAt(index);
                    AppLog.Write("表情发送动作已匹配到新消息，会话=" + normalizedScope);
                    return true;
                }
            }
            return false;
        }

        private static bool IsEmojiMessage(string text)
        {
            string value = Clean(text);
            return value.IndexOf("表情", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("emoji", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("emoticon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("sticker", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MatchesEmojiName(string invokedName, string messageText)
        {
            string name = NormalizeEmojiName(invokedName);
            string message = NormalizeEmojiName(messageText);
            if (name.Length == 0 || message.Length == 0) return false;
            if (string.Equals(name, message, StringComparison.OrdinalIgnoreCase)) return true;
            return name.Length >= 2 && message.Length >= 2 &&
                (name.EndsWith(message, StringComparison.OrdinalIgnoreCase) ||
                 message.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeEmojiName(string value)
        {
            string normalized = NormalizeForMatch(value).Trim('[', ']', '【', '】', '(', ')', '（', '）');
            if (normalized.StartsWith("表情：", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(3);
            if (normalized.StartsWith("表情:", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(3);
            return normalized;
        }

        private void RemoveExpiredInputSamples(DateTime now)
        {
            for (int index = recentlySentInputs.Count - 1; index >= 0; index--)
                if (now - recentlySentInputs[index].At >= TimeSpan.FromSeconds(15)) recentlySentInputs.RemoveAt(index);
            for (int index = recentlySentEmojis.Count - 1; index >= 0; index--)
                if (now - recentlySentEmojis[index].At >= TimeSpan.FromSeconds(8)) recentlySentEmojis.RemoveAt(index);

            List<string> expiredScopes = null;
            foreach (KeyValuePair<string, InputSample> pair in currentInputs)
            {
                if (now - pair.Value.At < TimeSpan.FromMinutes(2)) continue;
                if (expiredScopes == null) expiredScopes = new List<string>();
                expiredScopes.Add(pair.Key);
            }
            if (expiredScopes == null) return;
            foreach (string expiredScope in expiredScopes) currentInputs.Remove(expiredScope);
        }

        private static string NormalizeForMatch(string text)
        {
            return Regex.Replace(Clean(text), "\\s+", string.Empty);
        }

        private bool IsSeen(string key)
        {
            lock (syncRoot) { return seen.Contains(key); }
        }

        private void Remember(string key)
        {
            lock (syncRoot)
            {
                if (!seen.Add(key)) return;
                order.Enqueue(key);
                while (order.Count > 1000) seen.Remove(order.Dequeue());
            }
        }
    }
}
