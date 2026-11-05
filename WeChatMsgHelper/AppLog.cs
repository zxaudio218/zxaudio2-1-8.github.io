using System;
using System.IO;
using System.Text;

namespace WeChatMsgHelper
{
    internal static class AppLog
    {
        private const long MaxLogBytes = 2L * 1024L * 1024L;
        private const int RetainedCharacters = 300000;
        private static readonly object SyncRoot = new object();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static string path;
        private static string previousCrashPath;
        private static bool initialized;

        internal static void Initialize(string directory)
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(directory);
                string target = Path.Combine(directory, "log.txt");
                if (!initialized || !string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                {
                    string previous = Path.Combine(directory, "previous-crash-log.txt");
                    if (File.Exists(target) && new FileInfo(target).Length > 0)
                    {
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(target, previous);
                    }
                    File.WriteAllText(target, string.Empty, Utf8);
                    previousCrashPath = previous;
                }
                path = target;
                initialized = true;
            }
        }

        internal static void Write(string message)
        {
            try
            {
                lock (SyncRoot)
                {
                    TrimIfNeeded();
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, Utf8);
                }
            }
            catch { }
        }

        internal static void DeleteCurrent()
        {
            try
            {
                lock (SyncRoot)
                {
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
                    if (!string.IsNullOrWhiteSpace(previousCrashPath) && File.Exists(previousCrashPath))
                        File.Delete(previousCrashPath);
                }
            }
            catch { }
        }

        internal static bool TryExport(string targetPath, out string error)
        {
            error = string.Empty;
            try
            {
                lock (SyncRoot)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        error = "日志尚未初始化。";
                        return false;
                    }
                    string fullTargetPath = Path.GetFullPath(targetPath);
                    if (string.Equals(Path.GetFullPath(path), fullTargetPath, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(previousCrashPath) &&
                         string.Equals(Path.GetFullPath(previousCrashPath), fullTargetPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        error = "导出位置不能与原日志文件相同。";
                        return false;
                    }
                    if (!string.IsNullOrWhiteSpace(previousCrashPath) && File.Exists(previousCrashPath))
                    {
                        using (StreamWriter writer = new StreamWriter(targetPath, false, Utf8))
                        {
                            writer.WriteLine("===== 上次异常退出前的日志 =====");
                            writer.Write(File.ReadAllText(previousCrashPath, Encoding.UTF8));
                            writer.WriteLine();
                            writer.WriteLine("===== 本次运行日志 =====");
                            writer.Write(File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty);
                        }
                    }
                    else File.Copy(path, targetPath, true);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void TrimIfNeeded()
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || new FileInfo(path).Length < MaxLogBytes) return;
            string content = File.ReadAllText(path, Encoding.UTF8);
            int start = Math.Max(0, content.Length - RetainedCharacters);
            int nextLine = content.IndexOf('\n', start);
            if (nextLine >= 0 && nextLine + 1 < content.Length) start = nextLine + 1;
            File.WriteAllText(path, "日志超过大小限制，已保留最近记录。" + Environment.NewLine + content.Substring(start), Utf8);
        }
    }
}
