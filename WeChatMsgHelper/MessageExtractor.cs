using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
            internal string Text;
            internal DateTime At;
        }
        private readonly Queue<InputSample> recentInputs = new Queue<InputSample>();

        internal MessageExtractor(bool readNonTextMessages)
        {
            this.readNonTextMessages = readNonTextMessages;
        }

        internal void NoteInputText(string text)
        {
            string cleaned = Clean(text);
            if (cleaned.Length == 0) return;
            lock (syncRoot)
            {
                recentInputs.Enqueue(new InputSample { Text = NormalizeForMatch(cleaned), At = DateTime.UtcNow });
                while (recentInputs.Count > 16) recentInputs.Dequeue();
            }
        }

        internal bool Prime(AutomationElement root)
        {
            return Prime(root, string.Empty);
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
                messages.Add(new MessageData { Key = key, Text = text, Direction = GuessDirection(list, item) });
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
                    messages.Add(new MessageData { Key = key, Text = text, Direction = GuessDirection(list, item) });
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
            data = new MessageData { Key = key, Text = text, Direction = GuessDirection(list, item) };
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

        private MessageDirection GuessDirection(AutomationElement list, AutomationElement item)
        {
            try
            {
                string messageText = Clean(ReadName(item));
                if (MatchesRecentInput(messageText)) return MessageDirection.Outgoing;
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
                AutomationElementCollection descendants = item.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                for (int index = 0; index < descendants.Count; index++)
                {
                    AutomationElement element = descendants[index];
                    System.Windows.Rect bounds = element.Current.BoundingRectangle;
                    if (bounds.Width <= 1 || bounds.Height <= 1 || bounds.Width >= listRect.Width * 0.88) continue;
                    if (bounds.Right < listRect.Left || bounds.Left > listRect.Right) continue;

                    string name = Clean(element.Current.Name);
                    string normalizedName = NormalizeForMatch(name);
                    string hints = ((element.Current.AutomationId ?? string.Empty) + " " +
                        (element.Current.ClassName ?? string.Empty)).ToLowerInvariant();
                    int score = 0;
                    if (normalizedMessage.Length > 0 && normalizedName == normalizedMessage) score += 100;
                    else if (normalizedName.Length > 0 && normalizedMessage.Length > 0 &&
                        (normalizedName.Contains(normalizedMessage) || normalizedMessage.Contains(normalizedName))) score += 45;
                    if (hints.Contains("bubble") || hints.Contains("avatar") || hints.Contains("message") ||
                        hints.Contains("content") || hints.Contains("text") || hints.Contains("voice") || hints.Contains("image")) score += 20;
                    ControlType type = element.Current.ControlType;
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
            }
            catch { }
            return best;
        }

        private static string InferNonText(AutomationElement item)
        {
            try
            {
                AutomationElementCollection descendants = item.FindAll(TreeScope.Descendants, Condition.TrueCondition);
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

        private bool MatchesRecentInput(string text)
        {
            string normalized = NormalizeForMatch(text);
            if (normalized.Length == 0) return false;
            DateTime now = DateTime.UtcNow;
            lock (syncRoot)
            {
                foreach (InputSample sample in recentInputs)
                    if (now - sample.At < TimeSpan.FromSeconds(15) && sample.Text == normalized) return true;
            }
            return false;
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
