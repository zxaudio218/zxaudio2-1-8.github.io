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
        internal const string CurrentVersion = "1.8.3";
        internal const string Current =
            "微信消息助手 1.8.3 更新内容\r\n\r\n" +
            "1. 新增独立的“快捷键设置”窗口，集中管理朗读最新消息、启动或停止监听、切换通知方式和打开设置四组快捷键。\r\n" +
            "2. 快捷键可直接按键录入，支持编辑键区和小键盘按键；新增“恢复默认快捷键”功能，恢复前会再次确认。\r\n" +
            "3. 修复托盘菜单或 Ctrl+Alt+S 有时无法打开设置的问题；设置窗口已打开或最小化时会恢复并切换到前台。\r\n" +
            "4. 新增快捷键冲突提示，可直接进入快捷键设置，并可选择“不再显示这组快捷键”；录入快捷键时会临时释放助手自身的全局快捷键。\r\n" +
            "5. 改进设置窗口、快捷键窗口和冲突提示的键盘焦点顺序及读屏名称，使用标准 Windows 控件，避免影响屏幕阅读器操作。\r\n" +
            "6. 改进超长消息朗读，长文本会按标点自动分段，减少约 3000 字消息触发 SAPI5 兜底后无法及时停止的问题；停止监听时会立即停止当前朗读并清空等待队列。\r\n" +
            "7. 优化自动语音通道的并发处理，避免在调用外部语音接口时长期占用内部锁，降低程序退出或语音通道异常时卡住的风险。\r\n" +
            "8. 改进微信表情工具窗口的焦点识别，只在相关窗口位于前台时处理表情焦点，减少与屏幕阅读器重复朗读或误触发。";
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
                ValidatePackageVersion(path, manifest.Version);
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

        private static void ValidatePackageVersion(string path, Version manifestVersion)
        {
            Version packageVersion;
            try
            {
                packageVersion = AssemblyName.GetAssemblyName(path).Version;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("更新包不是有效的微信消息助手程序。", ex);
            }

            if (!VersionsEqual(packageVersion, manifestVersion))
                throw new InvalidDataException("更新包内部版本 " + packageVersion +
                    " 与更新信息版本 " + manifestVersion + " 不一致，已取消更新。");
        }

        private static bool VersionsEqual(Version left, Version right)
        {
            if (left == null || right == null) return false;
            return left.Major == right.Major &&
                left.Minor == right.Minor &&
                NormalizeVersionPart(left.Build) == NormalizeVersionPart(right.Build) &&
                NormalizeVersionPart(left.Revision) == NormalizeVersionPart(right.Revision);
        }

        private static int NormalizeVersionPart(int value)
        {
            return value < 0 ? 0 : value;
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
