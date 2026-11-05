using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WeChatMsgHelper
{
    internal sealed class MessageReviewManager : IDisposable
    {
        private sealed class ReviewState
        {
            internal string Identity;
            internal AutomationElement Root;
            internal List<MessageData> Messages = new List<MessageData>();
            internal int CurrentIndex = -1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point { internal int X; internal int Y; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extraInfo);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        private const uint MouseEventWheel = 0x0800;
        private const uint KeyEventExtended = 0x0001;
        private const uint KeyEventKeyUp = 0x0002;
        private const byte LeftControl = 0xA2;
        private const byte RightControl = 0xA3;
        private const byte LeftAlt = 0xA4;
        private const byte RightAlt = 0xA5;
        private const int WheelDelta = 120;
        private const int MaximumMessages = 500;
        private readonly Dictionary<string, ReviewState> states = new Dictionary<string, ReviewState>(StringComparer.Ordinal);
        private readonly WeChatNavigator navigator;
        private readonly MessageExtractor extractor;
        private readonly Action<string> speak;
        private readonly Action suppressNotifications;
        private System.Windows.Forms.Timer scrollTimer;
        private bool scrollPending;

        internal MessageReviewManager(WeChatNavigator navigator, MessageExtractor extractor, Action<string> speak, Action suppressNotifications)
        {
            this.navigator = navigator;
            this.extractor = extractor;
            this.speak = speak;
            this.suppressNotifications = suppressNotifications;
        }

        internal void ReadPrevious()
        {
            suppressNotifications();
            ReviewState state = Refresh();
            if (state == null) { speak("当前聊天没有可浏览消息"); return; }
            int target = state.CurrentIndex - 1;
            if (target < 0) { BeginHistoryScroll(state, 0); return; }
            SpeakAt(state, target);
        }

        internal void ReadNext()
        {
            suppressNotifications();
            ReviewState state = Refresh();
            if (state == null) { speak("当前聊天没有可浏览消息"); return; }
            int target = state.CurrentIndex + 1;
            if (target >= state.Messages.Count) target = state.Messages.Count - 1;
            SpeakAt(state, target);
        }

        internal void ReadFirst()
        {
            suppressNotifications();
            ReviewState state = Refresh();
            if (state == null) { speak("当前聊天没有可浏览消息"); return; }
            SpeakAt(state, 0);
        }

        internal void ReadLast()
        {
            suppressNotifications();
            ReviewState state = Refresh();
            if (state == null) { speak("当前聊天没有可浏览消息"); return; }
            SpeakAt(state, state.Messages.Count - 1);
        }

        public void Dispose()
        {
            if (scrollTimer != null)
            {
                scrollTimer.Stop();
                scrollTimer.Dispose();
                scrollTimer = null;
            }
        }

        private ReviewState Refresh()
        {
            AutomationElement root = navigator.GetForegroundChatRoot();
            if (root == null) return null;
            string identity = navigator.GetChatIdentity(root);
            if (string.IsNullOrEmpty(identity)) return null;
            IList<MessageData> visible = extractor.CollectVisibleMessages(root, true);
            if (visible.Count == 0) return null;

            ReviewState state;
            if (!states.TryGetValue(identity, out state))
            {
                state = new ReviewState();
                state.Identity = identity;
                states[identity] = state;
            }
            state.Root = root;
            Merge(state, visible);
            return state.Messages.Count == 0 ? null : state;
        }

        private static void Merge(ReviewState state, IList<MessageData> visible)
        {
            List<MessageData> incoming = new List<MessageData>(visible);
            if (state.Messages.Count == 0)
            {
                state.Messages = incoming;
                state.CurrentIndex = incoming.Count - 1;
                Trim(state);
                return;
            }
            if (FindSubList(state.Messages, incoming) >= 0) return;

            int oldIndex = state.CurrentIndex;
            bool wasAtLast = oldIndex >= state.Messages.Count - 1;
            int oldInsideNew = FindSubList(incoming, state.Messages);
            if (oldInsideNew >= 0)
            {
                state.Messages = incoming;
                state.CurrentIndex = wasAtLast ? incoming.Count - 1 : Math.Min(oldIndex + oldInsideNew, incoming.Count - 1);
                Trim(state);
                return;
            }

            int appendOverlap = SuffixPrefixOverlap(state.Messages, incoming);
            int prependOverlap = SuffixPrefixOverlap(incoming, state.Messages);
            if (appendOverlap >= prependOverlap && appendOverlap > 0)
            {
                state.Messages.AddRange(incoming.GetRange(appendOverlap, incoming.Count - appendOverlap));
                if (wasAtLast) state.CurrentIndex = state.Messages.Count - 1;
            }
            else if (prependOverlap > 0)
            {
                int prependCount = incoming.Count - prependOverlap;
                state.Messages.InsertRange(0, incoming.GetRange(0, prependCount));
                state.CurrentIndex = Math.Min(oldIndex + prependCount, state.Messages.Count - 1);
            }
            else
            {
                state.Messages = incoming;
                state.CurrentIndex = incoming.Count - 1;
            }
            Trim(state);
        }

        private void BeginHistoryScroll(ReviewState state, int attempt)
        {
            if (scrollPending) return;
            if (attempt > 4) { state.CurrentIndex = 0; return; }
            AutomationElement list = MessageExtractor.FindMessageList(state.Root);
            if (list == null) return;
            List<MessageData> oldMessages = new List<MessageData>(state.Messages);
            int oldIndex = state.CurrentIndex;
            scrollPending = true;
            if (scrollTimer != null) { scrollTimer.Stop(); scrollTimer.Dispose(); }
            if (!ScrollUp(list, attempt))
            {
                scrollPending = false;
                BeginHistoryScroll(state, attempt + 1);
                return;
            }
            scrollTimer = new System.Windows.Forms.Timer();
            scrollTimer.Interval = 25;
            scrollTimer.Tick += delegate
            {
                scrollTimer.Stop();
                scrollTimer.Dispose();
                scrollTimer = null;
                scrollPending = false;
                ReviewState refreshed = Refresh();
                if (refreshed == null) return;
                int oldStart = FindSubList(refreshed.Messages, oldMessages);
                if (oldStart > 0) { SpeakAt(refreshed, oldStart - 1); return; }
                if (oldStart >= 0) refreshed.CurrentIndex = Math.Min(oldStart + oldIndex, refreshed.Messages.Count - 1);
                BeginHistoryScroll(refreshed, attempt + 1);
            };
            scrollTimer.Start();
        }

        private static bool ScrollUp(AutomationElement list, int attempt)
        {
            Point oldPoint;
            GetCursorPos(out oldPoint);
            List<byte> pressedModifiers = GetPressedModifiers();
            try
            {
                System.Windows.Rect bounds = list.Current.BoundingRectangle;
                if (bounds.Width <= 0 || bounds.Height <= 0) return false;
                int x = (int)(bounds.Left + bounds.Width / 2);
                int y = (int)(bounds.Top + bounds.Height / 2);
                SetModifierState(pressedModifiers, true);
                SetCursorPos(x, y);
                int units = attempt >= 2 ? 8 : (attempt == 1 ? 6 : 4);
                mouse_event(MouseEventWheel, 0, 0, WheelDelta * units, UIntPtr.Zero);
                return true;
            }
            catch { return false; }
            finally
            {
                SetCursorPos(oldPoint.X, oldPoint.Y);
                SetModifierState(pressedModifiers, false);
            }
        }

        private static List<byte> GetPressedModifiers()
        {
            List<byte> result = new List<byte>();
            byte[] keys = { LeftControl, RightControl, LeftAlt, RightAlt };
            foreach (byte key in keys)
                if ((GetAsyncKeyState(key) & 0x8000) != 0) result.Add(key);
            return result;
        }

        private static void SetModifierState(IList<byte> keys, bool keyUp)
        {
            foreach (byte key in keys)
            {
                uint flags = key == RightControl || key == RightAlt ? KeyEventExtended : 0;
                if (keyUp) flags |= KeyEventKeyUp;
                keybd_event(key, 0, flags, UIntPtr.Zero);
            }
        }

        private void SpeakAt(ReviewState state, int index)
        {
            if (index < 0 || index >= state.Messages.Count) return;
            state.CurrentIndex = index;
            speak(state.Messages[index].Text);
        }

        private static int FindSubList(IList<MessageData> source, IList<MessageData> target)
        {
            if (target.Count == 0 || target.Count > source.Count) return -1;
            for (int index = 0; index <= source.Count - target.Count; index++)
            {
                bool match = true;
                for (int offset = 0; offset < target.Count; offset++)
                {
                    if (!SameMessage(source[index + offset], target[offset])) { match = false; break; }
                }
                if (match) return index;
            }
            return -1;
        }

        private static int SuffixPrefixOverlap(IList<MessageData> left, IList<MessageData> right)
        {
            int maximum = Math.Min(left.Count, right.Count);
            for (int count = maximum; count > 0; count--)
            {
                bool match = true;
                for (int index = 0; index < count; index++)
                {
                    if (!SameMessage(left[left.Count - count + index], right[index])) { match = false; break; }
                }
                if (match) return count;
            }
            return 0;
        }

        private static void Trim(ReviewState state)
        {
            int overflow = state.Messages.Count - MaximumMessages;
            if (overflow <= 0) return;
            state.Messages.RemoveRange(0, overflow);
            state.CurrentIndex = Math.Max(0, state.CurrentIndex - overflow);
        }

        private static bool SameMessage(MessageData left, MessageData right)
        {
            return left != null && right != null && left.Key == right.Key && left.Text == right.Text;
        }
    }
}
