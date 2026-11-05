using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

        internal bool IsWechatForeground()
        {
            IntPtr handle = GetForegroundWindow();
            if (handle == IntPtr.Zero) return false;
            try
            {
                AutomationElement element = AutomationElement.FromHandle(handle);
                string className = element.Current.ClassName;
                return className == "mmui::MainWindow" || className == "mmui::ChatSingleWindow";
            }
            catch { return IsWeixinWindow(handle); }
        }

        internal bool IsMessageInputFocused()
        {
            if (!IsWechatForeground()) return false;
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
            AutomationElement window = FindTopLevel("mmui::VOIPTrayWindow", "VOIPTrayWindow", ControlType.Window);
            return window != null && SetFocus(window);
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

        private static AutomationElement FindTopLevel(string className, string automationId, ControlType controlType)
        {
            AutomationElement found = null;
            EnumWindows(delegate(IntPtr handle, IntPtr parameter)
            {
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

        private static bool IsWeixinWindow(IntPtr handle)
        {
            uint processId;
            GetWindowThreadProcessId(handle, out processId);
            try
            {
                using (Process process = Process.GetProcessById(unchecked((int)processId)))
                    return process.ProcessName.Equals("Weixin", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
