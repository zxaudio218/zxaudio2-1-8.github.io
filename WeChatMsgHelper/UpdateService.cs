using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace WeChatMsgHelper
{
    [DataContract]
    internal sealed class UpdateManifest
    {
        [DataMember(Name = "version")]
        public string VersionText { get; set; }

        [DataMember(Name = "downloadUrl")]
        public string DownloadUrl { get; set; }

        [DataMember(Name = "sha256")]
        public string Sha256 { get; set; }

        [DataMember(Name = "publishedAt")]
        public string PublishedAt { get; set; }

        [DataMember(Name = "releaseNotes")]
        public string ReleaseNotes { get; set; }

        internal Version Version
        {
            get
            {
                Version version;
                return System.Version.TryParse(VersionText, out version) ? version : null;
            }
        }
    }

    internal sealed class UpdateCheckResult
    {
        internal UpdateManifest Manifest;
        internal string Error;
        internal bool HasUpdate;
    }

    internal static class UpdateReleaseNotes
    {
        internal const string CurrentVersion = "1.8.2.1";
        internal const string Current =
            "微信消息助手 1.8.2.1 更新内容\r\n\r\n" +
            "1. 改进微信会话监听，减少打开会话后需要等待较长时间才开始朗读的问题。\r\n" +
            "2. 修复表情面板打开后，助手与屏幕阅读器重复朗读面板内容的问题。\r\n" +
            "3. 改进微信窗口和会话变化后的自动重新绑定。\r\n" +
            "4. 修复部分 UI 自动化事件监听未正确注销造成的资源泄漏。\r\n" +
            "5. 增加消息方向识别和监听过程的诊断日志，便于定位异常情况。\r\n" +
            "6. 托盘菜单新增“导出异常日志”，遇到问题时可导出日志并发送给开发者协助排查。\r\n" +
            "7. 日志改为按单次运行生成并限制文件大小；正常退出后自动清理，异常退出时保留最近一份日志供导出排查。\r\n" +
            "8. 微信最小化或不在前台时，检测到音视频来电后可按 Alt+V 跳转到接听浮窗；通话浮窗关闭后会自动释放该快捷键。\r\n" +
            "9. 改进在线更新对中文和空格路径的兼容性，修复程序存放在包含中文字符的目录中时可能更新失败的问题；更新完成后继续保留用户原有的程序文件名，例如“消息助手.exe”。\r\n" +
            "10. 降低消息监听对微信进程的影响，合并高频界面事件、限制连续扫描并在微信响应缓慢时自动退避，减少微信未响应或异常退出的风险。\r\n" +
            "11. 启动提示改为朗读完整四段版本号，例如 1.8.2.1。\r\n" +
            "12. 更新回滚文件改为存放在用户数据更新目录，更新成功后自动清理，不再在程序所在目录生成备份文件。\r\n" +
            "13. 优化退出流程，退出时优先解除全局输入监听，并避免同步调用外部语音接口，降低退出时系统卡顿或桌面无响应的风险。";
    }

    internal sealed class UpdateService
    {
        internal const string ManifestUrl = "https://zx.tingit.cn/updates/wechat-msg-helper/updates.json";
        private const int MaxManifestBytes = 256 * 1024;
        private const long MaxPackageBytes = 150L * 1024L * 1024L;

        internal async Task<UpdateCheckResult> CheckAsync()
        {
            UpdateCheckResult result = new UpdateCheckResult();
            try
            {
                byte[] data = await DownloadBytesAsync(new Uri(ManifestUrl), MaxManifestBytes).ConfigureAwait(false);
                UpdateManifest manifest;
                using (MemoryStream stream = new MemoryStream(data, false))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(UpdateManifest));
                    manifest = serializer.ReadObject(stream) as UpdateManifest;
                }
                ValidateManifest(manifest);
                result.Manifest = manifest;
                result.HasUpdate = manifest.Version.CompareTo(Assembly.GetExecutingAssembly().GetName().Version) > 0;
            }
            catch (Exception ex)
            {
                result.Error = FriendlyError(ex);
            }
            return result;
        }

        internal async Task<string> DownloadPackageAsync(UpdateManifest manifest)
        {
            ValidateManifest(manifest);
            string directory = UpdateInstaller.UpdateDirectory;
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "WeChatMsgHelper-" + manifest.Version + ".exe");
            if (File.Exists(path)) File.Delete(path);

            try
            {
                HttpWebRequest request = CreateRequest(new Uri(manifest.DownloadUrl));
                using (WebResponse response = await request.GetResponseAsync().ConfigureAwait(false))
                {
                    EnsureHttpsResponse(response);
                    if (response.ContentLength > MaxPackageBytes) throw new InvalidDataException("更新包超过允许的大小。");
                    using (Stream input = response.GetResponseStream())
                    using (FileStream output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        byte[] buffer = new byte[81920];
                        long total = 0;
                        while (true)
                        {
                            int read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                            if (read <= 0) break;
                            total += read;
                            if (total > MaxPackageBytes) throw new InvalidDataException("更新包超过允许的大小。");
                            await output.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                        }
                    }
                }

                string actual = ComputeSha256(path);
                if (!actual.Equals(NormalizeHash(manifest.Sha256), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包校验失败，文件可能不完整。");
                return path;
            }
            catch
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch { }
                throw;
            }
        }

        internal void BeginInstall(UpdateManifest manifest, string packagePath)
        {
            UpdateInstaller.WritePendingRelease(manifest.VersionText, manifest.ReleaseNotes ?? string.Empty);
            string target = Path.GetFullPath(System.Windows.Forms.Application.ExecutablePath);
            int processId;
            using (Process current = Process.GetCurrentProcess()) processId = current.Id;
            ProcessStartInfo info = new ProcessStartInfo(packagePath,
                "--apply-update-v2 " + processId + " " +
                UpdateInstaller.EncodePathArgument(packagePath) + " " +
                UpdateInstaller.EncodePathArgument(target));
            info.UseShellExecute = false;
            info.WorkingDirectory = Path.GetDirectoryName(packagePath);
            try
            {
                using (Process helper = Process.Start(info)) { }
            }
            catch
            {
                UpdateInstaller.DeletePendingRelease();
                throw;
            }
        }

        private static async Task<byte[]> DownloadBytesAsync(Uri uri, int maximumBytes)
        {
            HttpWebRequest request = CreateRequest(uri);
            using (WebResponse response = await request.GetResponseAsync().ConfigureAwait(false))
            {
                EnsureHttpsResponse(response);
                if (response.ContentLength > maximumBytes) throw new InvalidDataException("更新信息超过允许的大小。");
                using (Stream input = response.GetResponseStream())
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    while (true)
                    {
                        int read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                        if (read <= 0) break;
                        if (output.Length + read > maximumBytes) throw new InvalidDataException("更新信息超过允许的大小。");
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
            }
        }

        private static HttpWebRequest CreateRequest(Uri uri)
        {
            if (uri == null || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新地址必须使用 HTTPS。");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Timeout = 15000;
            request.ReadWriteTimeout = 30000;
            request.UserAgent = "WeChatMsgHelper/1.8";
            request.AllowAutoRedirect = true;
            return request;
        }

        private static void ValidateManifest(UpdateManifest manifest)
        {
            if (manifest == null || manifest.Version == null) throw new InvalidDataException("更新信息中的版本号无效。");
            Uri download;
            if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out download) ||
                !download.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包地址无效。");
            string hash = NormalizeHash(manifest.Sha256);
            if (hash.Length != 64) throw new InvalidDataException("更新包 SHA-256 无效。");
            for (int index = 0; index < hash.Length; index++)
                if (!Uri.IsHexDigit(hash[index])) throw new InvalidDataException("更新包 SHA-256 无效。");
        }

        private static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder text = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }

        private static string NormalizeHash(string value)
        {
            return (value ?? string.Empty).Replace(" ", string.Empty).Replace("-", string.Empty).Trim();
        }

        private static string FriendlyError(Exception exception)
        {
            WebException web = exception as WebException;
            if (web != null && web.Response is HttpWebResponse)
            {
                HttpWebResponse response = (HttpWebResponse)web.Response;
                string message = "服务器返回 HTTP " + (int)response.StatusCode + "。";
                response.Dispose();
                return message;
            }
            return exception.Message;
        }

        private static void EnsureHttpsResponse(WebResponse response)
        {
            if (response == null || response.ResponseUri == null ||
                !response.ResponseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新服务器重定向到了不安全的地址。");
        }

    }
}
