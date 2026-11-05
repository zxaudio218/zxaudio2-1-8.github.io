using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace WeChatMsgHelper
{
    internal sealed class DiscoveredWechatRoot
    {
        internal AutomationElement Root;
        internal AutomationElement MessageList;
        internal AutomationElement Input;
        internal string Key;
        internal int Handle;
    }

    internal sealed class Discovery
    {
        private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maximumCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

        internal IList<DiscoveredWechatRoot> FindWechatRoots()
        {
            List<DiscoveredWechatRoot> roots = new List<DiscoveredWechatRoot>();
            EnumWindows(delegate(IntPtr handle, IntPtr parameter)
            {
                if (!IsWechatWindow(handle)) return true;
                try
                {
                    AutomationElement element = AutomationElement.FromHandle(handle);
                    string automationClassName = element.Current.ClassName;
                    if (!string.Equals(automationClassName, "mmui::MainWindow", StringComparison.Ordinal) &&
                        !string.Equals(automationClassName, "mmui::ChatSingleWindow", StringComparison.Ordinal)) return true;
                    AutomationElement list = MessageExtractor.FindMessageList(element);
                    if (list == null) return true;
                    string runtimeId = MessageExtractor.GetElementRuntimeId(list);
                    if (runtimeId.Length == 0) return true;
                    AutomationElement input = null;
                    try
                    {
                        input = element.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, "chat_input_field"));
                    }
                    catch { }
                    int nativeHandle = unchecked((int)handle.ToInt64());
                    roots.Add(new DiscoveredWechatRoot
                    {
                        Root = element,
                        MessageList = list,
                        Input = input,
                        Handle = nativeHandle,
                        Key = nativeHandle + ":" + runtimeId
                    });
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                return true;
            }, IntPtr.Zero);
            return roots;
        }

        private static bool IsWechatWindow(IntPtr handle)
        {
            StringBuilder className = new StringBuilder(64);
            if (GetClassName(handle, className, className.Capacity) == 0) return false;
            string value = className.ToString();
            if (string.Equals(value, "mmui::MainWindow", StringComparison.Ordinal) ||
                string.Equals(value, "mmui::ChatSingleWindow", StringComparison.Ordinal)) return true;
            return value.EndsWith("QWindowIcon", StringComparison.Ordinal) && IsWeixinWindow(handle);
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

        internal static bool IsAvailable(AutomationElement element)
        {
            if (element == null) return false;
            try { return element.Current.ProcessId > 0; }
            catch { return false; }
        }
    }
}
