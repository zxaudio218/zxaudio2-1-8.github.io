using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

        internal IList<DiscoveredWechatRoot> FindWechatRoots()
        {
            List<DiscoveredWechatRoot> roots = new List<DiscoveredWechatRoot>();
            EnumWindows(delegate(IntPtr handle, IntPtr parameter)
            {
                try
                {
                    AutomationElement element = AutomationElement.FromHandle(handle);
                    string className = element.Current.ClassName;
                    if (className == "mmui::MainWindow" || className == "mmui::ChatSingleWindow")
                    {
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
                        int nativeHandle = element.Current.NativeWindowHandle;
                        roots.Add(new DiscoveredWechatRoot
                        {
                            Root = element,
                            MessageList = list,
                            Input = input,
                            Handle = nativeHandle,
                            Key = nativeHandle + ":" + runtimeId
                        });
                    }
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                return true;
            }, IntPtr.Zero);
            return roots;
        }

        internal static bool IsAvailable(AutomationElement element)
        {
            if (element == null) return false;
            try { return element.Current.ProcessId > 0; }
            catch { return false; }
        }
    }
}
