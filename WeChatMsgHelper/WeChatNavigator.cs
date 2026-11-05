using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace WeChatMsgHelper
{
    internal sealed class WeChatNavigator
    {
        private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maximumCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr windowHandle);

        private const int SwRestore = 9;
        private const int ProcessCacheSeconds = 10;
        private const string VoipWindowClassName = "mmui::VOIPTrayWindow";
        private const string VoipWindowAutomationId = "VOIPTrayWindow";
        private readonly object voipWindowSync = new object();
        private readonly object processCacheSync = new object();
        private readonly Dictionary<IntPtr, bool> knownVoipCandidates = new Dictionary<IntPtr, bool>();
        private readonly Dictionary<uint, bool> weixinProcessCache = new Dictionary<uint, bool>();
        private IntPtr cachedVoipWindowHandle;
        private DateTime processCacheExpiresAt;

        internal bool IsWechatForeground()
        {
            IntPtr handle = GetForegroundWindow();
            if (handle == IntPtr.Zero) return false;
            StringBuilder className = new StringBuilder(64);
            if (GetClassName(handle, className, className.Capacity) != 0)
            {
                string value = className.ToString();
                if (value == "mmui::MainWindow" || value == "mmui::ChatSingleWindow") return true;
                if (value.EndsWith("QWindowIcon", StringComparison.Ordinal)) return IsWeixinWindow(handle);
            }
            return IsWeixinWindow(handle);
        }

        internal bool IsMessageInputFocused(bool wechatForeground)
        {
            if (!wechatForeground) return false;
            try
            {
                AutomationElement focused = AutomationElement.FocusedElement;
                return focused != null && focused.Current.AutomationId == "chat_input_field";
            }
            catch { return false; }
        }

        internal AutomationElement GetForegroundChatRoot()
        {
            IntPtr handle = GetForegroundWindow();
            if (handle == IntPtr.Zero) return null;
            try
            {
                AutomationElement root = AutomationElement.FromHandle(handle);
                string className = root.Current.ClassName;
                if ((className == "mmui::MainWindow" || className == "mmui::ChatSingleWindow") && MessageExtractor.FindMessageList(root) != null)
                    return root;
            }
            catch { }
            return null;
        }

        internal bool FocusOfficialAccountList()
        {
            AutomationElement main = FindTopLevel("mmui::MainWindow", null, null);
            if (main == null) return false;
            List<AutomationElement> candidates = FindDescendants(main, "session_list", "mmui::XTableView", ControlType.List);
            foreach (AutomationElement candidate in candidates)
            {
                System.Windows.Rect bounds = candidate.Current.BoundingRectangle;
                foreach (AutomationElement reference in candidates)
                {
                    if (Automation.Compare(candidate, reference)) continue;
                    System.Windows.Rect other = reference.Current.BoundingRectangle;
                    if (other.Right <= bounds.Left && other.Top < bounds.Bottom && bounds.Top < other.Bottom)
                        return SetFocus(candidate);
                }
            }
            return false;
        }

        internal bool FocusGlobalSearch()
        {
            AutomationElement main = FindTopLevel("mmui::MainWindow", null, null);
            if (main == null) return false;
            string[] knownIds =
            {
                "global_search", "global_search_input", "search_box", "search_input", "search_bar", "top_search"
            };
            foreach (string id in knownIds)
            {
                try
                {
                    AutomationElement candidate = main.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, id));
                    if (candidate != null && IsFocusable(candidate) && SetFocus(candidate)) return true;
                }
                catch { }
            }

            try
            {
                AutomationElementCollection elements = main.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                for (int index = 0; index < elements.Count; index++)
                {
                    AutomationElement element = elements[index];
                    string id = element.Current.AutomationId ?? string.Empty;
                    string name = element.Current.Name ?? string.Empty;
                    string className = element.Current.ClassName ?? string.Empty;
                    bool searchHint = id.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("搜索", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("查找", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        className.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!searchHint || !IsFocusable(element)) continue;
                    if (element.Current.ControlType == ControlType.Edit && SetFocus(element)) return true;
                }
            }
            catch { }
            return false;
        }

        internal bool FocusContactList()
        {
            AutomationElement main = FindTopLevel("mmui::MainWindow", null, null);
            if (main == null) return false;
            List<AutomationElement> matches = FindDescendants(main, "primary_table_.contact_list", "mmui::StickyHeaderRecyclerListView", ControlType.List);
            return matches.Count > 0 && SetFocus(matches[0]);
        }

        internal bool FocusVoipTrayWindow()
        {
            IntPtr handle = FindVoipTrayWindowHandle();
            if (handle == IntPtr.Zero) return false;
            bool activated = false;
            try
            {
                ShowWindowAsync(handle, SwRestore);
                activated = SetForegroundWindow(handle);
            }
            catch { }
            try
            {
                AutomationElement window = AutomationElement.FromHandle(handle);
                return SetFocus(window) || activated;
            }
            catch { return activated; }
        }

        internal bool HasVoipTrayWindow()
        {
            return FindVoipTrayWindowHandle() != IntPtr.Zero;
        }

        internal string GetChatIdentity(AutomationElement root)
        {
            try
            {
                string automationId = root.Current.AutomationId;
                string className = root.Current.ClassName;
                if (!string.IsNullOrEmpty(automationId)) return className + ":" + automationId;
                AutomationElement input = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "chat_input_field"));
                string name = input == null ? root.Current.Name : input.Current.Name;
                return className + ":" + root.Current.NativeWindowHandle + ":" + name;
            }
            catch { return null; }
        }

        private static bool SetFocus(AutomationElement element)
        {
            try { element.SetFocus(); return true; }
            catch { return false; }
        }

        private static bool IsFocusable(AutomationElement element)
        {
            try { return !element.Current.IsOffscreen && element.Current.IsKeyboardFocusable; }
            catch { return false; }
        }

        private static List<AutomationElement> FindDescendants(AutomationElement root, string automationId, string className, ControlType controlType)
        {
            List<AutomationElement> result = new List<AutomationElement>();
            Condition condition = new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId),
                new PropertyCondition(AutomationElement.ClassNameProperty, className),
                new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));
            try
            {
                AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, condition);
                for (int index = elements.Count - 1; index >= 0; index--)
                {
                    AutomationElement element = elements[index];
                    if (!element.Current.IsOffscreen && element.Current.IsKeyboardFocusable) result.Add(element);
                }
            }
            catch { }
            return result;
        }

        private AutomationElement FindTopLevel(string className, string automationId, ControlType controlType)
        {
            AutomationElement found = null;
            EnumWindows(delegate(IntPtr handle, IntPtr parameter)
            {
                if (!IsWeixinTopLevelWindow(handle)) return true;
                try
                {
                    AutomationElement element = AutomationElement.FromHandle(handle);
                    if (element.Current.ClassName != className) return true;
                    if (automationId != null && element.Current.AutomationId != automationId) return true;
                    if (controlType != null && element.Current.ControlType != controlType) return true;
                    found = element;
                    return false;
                }
                catch { return true; }
            }, IntPtr.Zero);
            return found;
        }

        private IntPtr FindVoipTrayWindowHandle()
        {
            lock (voipWindowSync)
            {
                Dictionary<IntPtr, bool> currentCandidates = new Dictionary<IntPtr, bool>();
                EnumWindows(delegate(IntPtr handle, IntPtr parameter)
                {
                    StringBuilder className = new StringBuilder(64);
                    if (GetClassName(handle, className, className.Capacity) == 0) return true;
                    string value = className.ToString();
                    if (!value.Equals(VoipWindowClassName, StringComparison.Ordinal) &&
                        !value.EndsWith("QWindowIcon", StringComparison.Ordinal)) return true;
                    if (IsWeixinWindow(handle)) currentCandidates[handle] = IsWindowVisible(handle);
                    return true;
                }, IntPtr.Zero);

                bool candidatesChanged = currentCandidates.Count != knownVoipCandidates.Count;
                if (!candidatesChanged)
                {
                    foreach (KeyValuePair<IntPtr, bool> candidate in currentCandidates)
                    {
                        bool knownVisibility;
                        if (!knownVoipCandidates.TryGetValue(candidate.Key, out knownVisibility) ||
                            knownVisibility != candidate.Value)
                        {
                            candidatesChanged = true;
                            break;
                        }
                    }
                }
                if (!candidatesChanged)
                    return currentCandidates.ContainsKey(cachedVoipWindowHandle) ? cachedVoipWindowHandle : IntPtr.Zero;

                knownVoipCandidates.Clear();
                foreach (KeyValuePair<IntPtr, bool> candidate in currentCandidates)
                    knownVoipCandidates[candidate.Key] = candidate.Value;
                cachedVoipWindowHandle = IntPtr.Zero;

                foreach (KeyValuePair<IntPtr, bool> candidate in currentCandidates)
                {
                    StringBuilder className = new StringBuilder(64);
                    if (GetClassName(candidate.Key, className, className.Capacity) == 0) continue;
                    if (className.ToString().Equals(VoipWindowClassName, StringComparison.Ordinal))
                    {
                        cachedVoipWindowHandle = candidate.Key;
                        break;
                    }
                    if (!candidate.Value) continue;
                    try
                    {
                        AutomationElement element = AutomationElement.FromHandle(candidate.Key);
                        if (element.Current.AutomationId.Equals(VoipWindowAutomationId, StringComparison.Ordinal))
                        {
                            cachedVoipWindowHandle = candidate.Key;
                            break;
                        }
                    }
                    catch { }
                }
                return cachedVoipWindowHandle;
            }
        }

        private bool IsWeixinTopLevelWindow(IntPtr handle)
        {
            StringBuilder className = new StringBuilder(64);
            if (GetClassName(handle, className, className.Capacity) == 0) return false;
            string value = className.ToString();
            if (!value.StartsWith("mmui::", StringComparison.Ordinal) &&
                !value.EndsWith("QWindowIcon", StringComparison.Ordinal)) return false;
            return IsWeixinWindow(handle);
        }

        private bool IsWeixinWindow(IntPtr handle)
        {
            uint processId;
            GetWindowThreadProcessId(handle, out processId);
            if (processId == 0) return false;
            lock (processCacheSync)
            {
                DateTime now = DateTime.UtcNow;
                if (now >= processCacheExpiresAt)
                {
                    weixinProcessCache.Clear();
                    processCacheExpiresAt = now.AddSeconds(ProcessCacheSeconds);
                }
                bool isWeixin;
                if (weixinProcessCache.TryGetValue(processId, out isWeixin)) return isWeixin;
                try
                {
                    using (Process process = Process.GetProcessById(unchecked((int)processId)))
                        isWeixin = process.ProcessName.Equals("Weixin", StringComparison.OrdinalIgnoreCase);
                }
                catch { isWeixin = false; }
                weixinProcessCache[processId] = isWeixin;
                return isWeixin;
            }
        }
    }
}
