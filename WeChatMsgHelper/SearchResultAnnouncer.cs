using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace WeChatMsgHelper
{
    internal sealed class SearchResultAnnouncer : IDisposable
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maximumCount);

        private readonly Action<Action> dispatch;
        private readonly Action<string> speak;
        private readonly AutomationFocusChangedEventHandler handler;
        private bool subscribed;
        private string lastKey = string.Empty;

        internal SearchResultAnnouncer(Action<Action> dispatch, Action<string> speak)
        {
            this.dispatch = dispatch;
            this.speak = speak;
            handler = OnFocusChanged;
            try
            {
                Automation.AddAutomationFocusChangedEventHandler(handler);
                subscribed = true;
            }
            catch (Exception ex) { AppLog.Write("搜索结果 UIA 监听不可用：" + ex.Message); }
        }

        public void Dispose()
        {
            try { if (subscribed) Automation.RemoveAutomationFocusChangedEventHandler(handler); }
            catch { }
        }

        private void OnFocusChanged(object sender, AutomationFocusChangedEventArgs args)
        {
            AutomationElement element = sender as AutomationElement;
            if (element == null) return;
            try
            {
                AutomationElement top = element;
                TreeWalker walker = TreeWalker.RawViewWalker;
                AutomationElement parent;
                while ((parent = walker.GetParent(top)) != null)
                {
                    if (parent.Current.ProcessId != element.Current.ProcessId) break;
                    top = parent;
                }
                int nativeHandle = top.Current.NativeWindowHandle;
                if (nativeHandle == 0 || top.Current.ProcessId != element.Current.ProcessId) return;
                StringBuilder className = new StringBuilder(256);
                GetClassName(new IntPtr((long)(uint)nativeHandle), className, className.Capacity);
                if (className.ToString() != "Qt51514QWindowToolSaveBits") return;
                string text = element.Current.Name == null ? string.Empty : element.Current.Name.Trim();
                if (text.Length == 0) return;
                int[] runtimeId = element.GetRuntimeId();
                string key = runtimeId == null ? text : string.Join("-", Array.ConvertAll(runtimeId, delegate(int value) { return value.ToString(); })) + ":" + text;
                if (key == lastKey) return;
                lastKey = key;
                dispatch(delegate { speak(text); });
            }
            catch { }
        }
    }
}
