using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WeChatMsgHelper
{
    internal static class AppDataPaths
    {
        private static readonly List<string> Warnings = new List<string>();

        internal static string DataDirectory { get; private set; }
        internal static string ConfigPath { get { return Path.Combine(DataDirectory, "config.ini"); } }

        internal static IList<string> Initialize(string executableDirectory)
        {
            Warnings.Clear();
            DataDirectory = CreateDataDirectory();
            MigrateConfig(executableDirectory);
            MigrateLog(executableDirectory);
            return Warnings.AsReadOnly();
        }

        private static string CreateDataDirectory()
        {
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Path.GetTempPath()
            };
            Exception lastError = null;
            foreach (string root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                string directory = Path.Combine(root, "WeChatMsgHelper");
                try
                {
                    Directory.CreateDirectory(directory);
                    return directory;
                }
                catch (Exception ex) { lastError = ex; }
            }
            throw new IOException("无法创建程序数据目录。", lastError);
        }

        private static void MigrateConfig(string executableDirectory)
        {
            string legacy = Path.Combine(executableDirectory, "config.ini");
            string target = ConfigPath;
            if (SamePath(legacy, target) || !File.Exists(legacy)) return;
            try
            {
                if (!File.Exists(target) || File.GetLastWriteTimeUtc(legacy) > File.GetLastWriteTimeUtc(target))
                    File.Copy(legacy, target, true);
                File.Delete(legacy);
            }
            catch (Exception ex) { Warnings.Add("迁移旧配置失败：" + ex.Message); }
        }

        private static void MigrateLog(string executableDirectory)
        {
            string legacy = Path.Combine(executableDirectory, "log.txt");
            string target = Path.Combine(DataDirectory, "log.txt");
            if (SamePath(legacy, target) || !File.Exists(legacy)) return;
            try
            {
                if (File.Exists(target))
                {
                    File.AppendAllText(target, File.ReadAllText(legacy, Encoding.UTF8), new UTF8Encoding(false));
                    File.Delete(legacy);
                }
                else
                {
                    File.Move(legacy, target);
                }
            }
            catch (Exception ex) { Warnings.Add("迁移旧日志失败：" + ex.Message); }
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
