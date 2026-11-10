using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WeChatMsgHelper
{
    [DataContract]
    internal sealed class InstalledRelease
    {
        [DataMember(Name = "version")]
        public string Version;

        [DataMember(Name = "releaseNotes")]
        public string ReleaseNotes;
    }

    internal static class UpdateInstaller
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static string UpdateDirectory
        {
            get { return Path.Combine(GetDataDirectory(), "updates"); }
        }

        private static string PendingReleasePath { get { return Path.Combine(GetDataDirectory(), "pending-release.json"); } }
        private static string InstalledReleasePath { get { return Path.Combine(GetDataDirectory(), "installed-release.json"); } }

        internal static bool TryRunApplyMode(string[] args)
        {
            if (!IsApplyMode(args)) return false;
            try
            {
                int processId;
                string source;
                string target;
                ParseApplyArguments(args, out processId, out source, out target);
                string running = Path.GetFullPath(Application.ExecutablePath);
                if (!source.Equals(running, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新源文件无效。");
                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || source.Equals(target, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新目标文件无效。");

                WaitForExit(processId);
                ApplyWithRollback(source, target);
                CommitPendingRelease();
                ProcessStartInfo info = new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target) };
                using (Process launched = Process.Start(info)) { }
            }
            catch (Exception ex)
            {
                MessageBox.Show("安装更新失败：" + ex.Message, "微信消息助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return true;
        }

        internal static string EncodePathArgument(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("更新路径不能为空。", "path");
            return Convert.ToBase64String(StrictUtf8.GetBytes(Path.GetFullPath(path)));
        }

        internal static void ParseApplyArguments(string[] args, out int processId, out string source, out string target)
        {
            if (args == null || args.Length != 4) throw new InvalidDataException("更新参数数量无效。");
            if (!int.TryParse(args[1], out processId)) throw new InvalidDataException("旧进程编号无效。");

            bool encoded = string.Equals(args[0], "--apply-update-v2", StringComparison.Ordinal);
            if (!encoded && !string.Equals(args[0], "--apply-update", StringComparison.Ordinal))
                throw new InvalidDataException("更新参数格式无效。");

            source = Path.GetFullPath(encoded ? DecodePathArgument(args[2]) : args[2]);
            target = Path.GetFullPath(encoded ? DecodePathArgument(args[3]) : args[3]);
        }

        private static bool IsApplyMode(string[] args)
        {
            return args != null && args.Length > 0 &&
                (string.Equals(args[0], "--apply-update-v2", StringComparison.Ordinal) ||
                 string.Equals(args[0], "--apply-update", StringComparison.Ordinal));
        }

        private static string DecodePathArgument(string value)
        {
            try
            {
                string path = StrictUtf8.GetString(Convert.FromBase64String(value ?? string.Empty));
                if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("更新路径不能为空。");
                return path;
            }
            catch (Exception ex)
            {
                if (ex is InvalidDataException) throw;
                throw new InvalidDataException("更新路径参数无效。", ex);
            }
        }

        internal static void WritePendingRelease(string version, string releaseNotes)
        {
            Directory.CreateDirectory(GetDataDirectory());
            WriteRelease(PendingReleasePath, new InstalledRelease { Version = version, ReleaseNotes = releaseNotes });
        }

        internal static InstalledRelease ReadInstalledRelease()
        {
            try
            {
                if (!File.Exists(InstalledReleasePath)) return null;
                using (FileStream stream = File.OpenRead(InstalledReleasePath))
                    return new DataContractJsonSerializer(typeof(InstalledRelease)).ReadObject(stream) as InstalledRelease;
            }
            catch (Exception ex)
            {
                AppLog.Write("读取更新完成信息失败：" + ex.Message);
                return null;
            }
        }

        internal static void DeleteInstalledRelease()
        {
            try { if (File.Exists(InstalledReleasePath)) File.Delete(InstalledReleasePath); }
            catch (Exception ex) { AppLog.Write("清理更新完成信息失败：" + ex.Message); }
        }

        internal static void DeletePendingRelease()
        {
            try { if (File.Exists(PendingReleasePath)) File.Delete(PendingReleasePath); }
            catch (Exception ex) { AppLog.Write("清理待安装更新信息失败：" + ex.Message); }
        }

        internal static void CleanupOldFiles()
        {
            try
            {
                string directory = UpdateDirectory;
                if (Directory.Exists(directory))
                    foreach (string file in Directory.GetFiles(directory, "*.exe"))
                        if (!Path.GetFullPath(file).Equals(Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase)) File.Delete(file);
                string backup = Application.ExecutablePath + ".update-backup";
                if (File.Exists(backup)) File.Delete(backup);
            }
            catch (Exception ex) { AppLog.Write("清理旧更新文件失败：" + ex.Message); }
        }

        private static void WaitForExit(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                    if (!process.WaitForExit(30000)) throw new TimeoutException("等待旧版本退出超时。");
            }
            catch (ArgumentException) { }
        }

        private static void ApplyWithRollback(string source, string target)
        {
            Directory.CreateDirectory(UpdateDirectory);
            string backup = Path.Combine(UpdateDirectory, "rollback-" + Guid.NewGuid().ToString("N") + ".exe");
            Exception last = null;
            bool preserveBackup = false;
            try
            {
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    try
                    {
                        if (File.Exists(backup)) File.Delete(backup);
                        File.Copy(target, backup, true);
                        File.Copy(source, target, true);
                        return;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        try { if (File.Exists(backup)) File.Copy(backup, target, true); }
                        catch (Exception rollbackError)
                        {
                            preserveBackup = true;
                            last = new IOException("恢复旧版本失败，回滚文件保存在：" + backup, rollbackError);
                            break;
                        }
                        Thread.Sleep(250);
                    }
                }
                throw new IOException(preserveBackup ? last.Message : "无法覆盖原程序文件。", last);
            }
            finally
            {
                if (!preserveBackup)
                {
                    try { if (File.Exists(backup)) File.Delete(backup); }
                    catch { }
                }
            }
        }

        private static void CommitPendingRelease()
        {
            if (!File.Exists(PendingReleasePath)) return;
            if (File.Exists(InstalledReleasePath)) File.Delete(InstalledReleasePath);
            File.Move(PendingReleasePath, InstalledReleasePath);
        }

        private static void WriteRelease(string path, InstalledRelease release)
        {
            string temporary = path + ".tmp";
            using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                new DataContractJsonSerializer(typeof(InstalledRelease)).WriteObject(stream, release);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        private static string GetDataDirectory()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
            return Path.Combine(root, "WeChatMsgHelper");
        }
    }
}
