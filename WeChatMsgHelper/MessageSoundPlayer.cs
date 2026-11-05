using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WeChatMsgHelper
{
    internal sealed class MessageSoundPlayer : IDisposable
    {
        private const string SentAlias = "WeChatMsgHelperSent";
        private const string ReceivedAlias = "WeChatMsgHelperReceived";

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, string returnValue, int returnLength, IntPtr callback);

        private readonly object syncRoot = new object();
        private string directory;
        private string sentPath;
        private string receivedPath;
        private bool initialized;
        private bool disposed;

        internal void Play(MessageDirection direction)
        {
            lock (syncRoot)
            {
                if (disposed || !EnsureInitialized()) return;
                string alias = direction == MessageDirection.Outgoing ? SentAlias : ReceivedAlias;
                AppLog.Write("播放消息音效，方向=" + direction + "，资源=" + alias);
                mciSendString("stop " + alias, null, 0, IntPtr.Zero);
                mciSendString("seek " + alias + " to start", null, 0, IntPtr.Zero);
                int error = mciSendString("play " + alias, null, 0, IntPtr.Zero);
                if (error != 0) AppLog.Write("消息音效播放失败：" + error);
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed) return;
                disposed = true;
                CloseAlias(SentAlias);
                CloseAlias(ReceivedAlias);
                DeleteFile(sentPath);
                DeleteFile(receivedPath);
                try { if (directory != null && Directory.Exists(directory)) Directory.Delete(directory, true); }
                catch { }
            }
        }

        private bool EnsureInitialized()
        {
            if (initialized) return true;
            try
            {
                string root = Path.Combine(Path.GetTempPath(), "WeChatMsgHelper");
                directory = Path.Combine(root, Process.GetCurrentProcess().Id.ToString());
                CleanupStaleDirectories(root, directory);
                Directory.CreateDirectory(directory);
                sentPath = ExtractResource("WeChatMsgHelper.Audio.SentMessage.mp3", "SentMessage.mp3");
                receivedPath = ExtractResource("WeChatMsgHelper.Audio.SIMToolkitSMS.mp3", "SIMToolkitSMS.mp3");
                if (!OpenAlias(SentAlias, sentPath) || !OpenAlias(ReceivedAlias, receivedPath))
                {
                    CleanupExtractedFiles();
                    return false;
                }
                initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("消息音效初始化失败：" + ex.Message);
                CleanupExtractedFiles();
                return false;
            }
        }

        private string ExtractResource(string resourceName, string fileName)
        {
            string path = Path.Combine(directory, fileName);
            using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (input == null) throw new FileNotFoundException("未找到内嵌音效资源：" + resourceName);
                using (FileStream output = File.Create(path)) input.CopyTo(output);
            }
            return path;
        }

        private static bool OpenAlias(string alias, string path)
        {
            char quote = (char)34;
            string command = "open " + quote + path + quote + " type mpegvideo alias " + alias;
            return mciSendString(command, null, 0, IntPtr.Zero) == 0;
        }

        private static void CloseAlias(string alias)
        {
            mciSendString("stop " + alias, null, 0, IntPtr.Zero);
            mciSendString("close " + alias, null, 0, IntPtr.Zero);
        }

        private void CleanupExtractedFiles()
        {
            CloseAlias(SentAlias);
            CloseAlias(ReceivedAlias);
            DeleteFile(sentPath);
            DeleteFile(receivedPath);
            try { if (directory != null && Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch { }
            sentPath = null;
            receivedPath = null;
            initialized = false;
        }

        private static void DeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static void CleanupStaleDirectories(string root, string current)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (string path in Directory.GetDirectories(root))
                {
                    if (string.Equals(path, current, StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.Delete(path, true); }
                    catch { }
                }
            }
            catch { }
        }
    }
}
