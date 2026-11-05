using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

namespace M0Probe
{
    internal sealed class WechatWindowInfo
    {
        public IntPtr Handle;
        public string Title;
        public string ClassName;
        public string NativeClassName;
        public int ProcessId;
        public string ProcessName;
        public string ProcessPath;
        public string FileVersion;
    }

    internal static class WindowProbe
    {
        internal static IList<WechatWindowInfo> FindWechatWindows()
        {
            List<WechatWindowInfo> windows = new List<WechatWindowInfo>();
            NativeMethods.EnumWindows(delegate(IntPtr handle, IntPtr parameter)
            {
                try
                {
                    AutomationElement element = AutomationElement.FromHandle(handle);
                    string className = element.Current.ClassName;
                    if (className != "mmui::MainWindow" && className != "mmui::ChatSingleWindow") return true;

                    WechatWindowInfo info = new WechatWindowInfo();
                    info.Handle = handle;
                    info.Title = element.Current.Name;
                    info.ClassName = className;
                    info.NativeClassName = ReadClassName(info.Handle);
                    info.ProcessId = element.Current.ProcessId;
                    FillProcessDetails(info);
                    windows.Add(info);
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        internal static void PrintWechatWindows()
        {
            IList<WechatWindowInfo> windows = FindWechatWindows();
            Console.WriteLine("匹配到 {0} 个微信顶层窗口。", windows.Count);
            if (windows.Count == 0)
            {
                Console.WriteLine("请确认 PC 微信 4.x 已运行。");
                return;
            }

            foreach (WechatWindowInfo window in windows)
            {
                Console.WriteLine("句柄      : 0x{0:X}", window.Handle.ToInt64());
                Console.WriteLine("标题      : {0}", Mark(window.Title));
                Console.WriteLine("类名      : {0}", window.ClassName);
                Console.WriteLine("原生类名  : {0}", Mark(window.NativeClassName));
                Console.WriteLine("PID       : {0}", window.ProcessId);
                Console.WriteLine("进程名    : {0}", Mark(window.ProcessName));
                Console.WriteLine("进程路径  : {0}", Mark(window.ProcessPath));
                Console.WriteLine("文件版本  : {0}", Mark(window.FileVersion));
                Console.WriteLine();
            }
        }

        private static string ReadClassName(IntPtr handle)
        {
            StringBuilder value = new StringBuilder(512);
            NativeMethods.GetClassName(handle, value, value.Capacity);
            return value.ToString();
        }

        private static void FillProcessDetails(WechatWindowInfo info)
        {
            try
            {
                using (Process process = Process.GetProcessById(info.ProcessId))
                {
                    info.ProcessName = process.ProcessName;
                    try
                    {
                        info.ProcessPath = process.MainModule.FileName;
                        info.FileVersion = FileVersionInfo.GetVersionInfo(info.ProcessPath).FileVersion;
                    }
                    catch
                    {
                        info.ProcessPath = string.Empty;
                        info.FileVersion = string.Empty;
                    }
                }
            }
            catch
            {
                info.ProcessName = string.Empty;
                info.ProcessPath = string.Empty;
                info.FileVersion = string.Empty;
            }
        }

        private static string Mark(string value)
        {
            return string.IsNullOrEmpty(value) ? "<空>" : value;
        }
    }
}
