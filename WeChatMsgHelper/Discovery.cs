using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WeChatMsgHelper
{
    internal sealed class Discovery
    {
        private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        internal IList<AutomationElement> FindWechatRoots()
        {
            List<AutomationElement> roots = new List<AutomationElement>();
            EnumWindows(delegate(IntPtr handle, IntPtr parameter)
            {
                try
                {
                    AutomationElement element = AutomationElement.FromHandle(handle);
                    string className = element.Current.ClassName;
                    if ((className == "mmui::MainWindow" || className == "mmui::ChatSingleWindow") &&
                        MessageExtractor.FindMessageList(element) != null)
                        roots.Add(element);
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
