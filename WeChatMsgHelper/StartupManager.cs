using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WeChatMsgHelper
{
    internal static class StartupManager
    {
        private const string ShortcutName = "WeChatMsgHelper.lnk";

        internal static bool IsEnabled
        {
            get { return File.Exists(GetShortcutPath()); }
        }

        internal static bool SetEnabled(bool enabled, out string error)
        {
            error = string.Empty;
            try
            {
                string shortcutPath = GetShortcutPath();
                if (!enabled)
                {
                    if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
                    return true;
                }

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) throw new InvalidOperationException("系统不支持 WScript.Shell。");
                object shell = Activator.CreateInstance(shellType);
                object shortcut = null;
                try
                {
                    shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                    Type shortcutType = shortcut.GetType();
                    string exePath = ResolveStartupTarget();
                    shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
                    shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exePath) });
                    shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "微信消息助手" });
                    shortcutType.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, shortcut, new object[] { 7 });
                    shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                }
                finally
                {
                    if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                    if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string GetShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);
        }

        private static string ResolveStartupTarget()
        {
            string current = Path.GetFullPath(System.Windows.Forms.Application.ExecutablePath);
            DirectoryInfo directory = new FileInfo(current).Directory;
            while (directory != null)
            {
                if (directory.Name.Equals("WeChatMsgHelper", StringComparison.OrdinalIgnoreCase) && directory.Parent != null)
                {
                    string dist = Path.Combine(directory.Parent.FullName, "dist", "WeChatMsgHelper.exe");
                    if (File.Exists(dist)) return dist;
                    break;
                }
                directory = directory.Parent;
            }
            return current;
        }
    }
}
