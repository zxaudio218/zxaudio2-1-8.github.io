using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation.Provider;

namespace M0Probe
{
    internal static class NativeMethods
    {
        internal const int WmGetObject = 0x003D;
        internal const int WmQuit = 0x0012;
        internal delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maximumCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr LoadLibrary(string fileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        internal static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr module);

        [DllImport("UIAutomationCore.dll", CharSet = CharSet.Unicode)]
        internal static extern int UiaRaiseNotificationEvent(
            [MarshalAs(UnmanagedType.Interface)] IRawElementProviderSimple provider,
            int notificationKind,
            int notificationProcessing,
            string displayString,
            string activityId);
    }
}
