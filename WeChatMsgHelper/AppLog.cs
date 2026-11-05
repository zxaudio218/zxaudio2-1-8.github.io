using System;
using System.IO;
using System.Text;

namespace WeChatMsgHelper
{
    internal static class AppLog
    {
        private static readonly object SyncRoot = new object();
        private static string path;

        internal static void Initialize(string directory)
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "log.txt");
        }

        internal static void Write(string message)
        {
            try
            {
                lock (SyncRoot)
                {
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
