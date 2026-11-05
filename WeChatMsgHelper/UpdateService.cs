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
        internal const string CurrentVersion = "1.8.0.0";
        internal const string Current =
            "微信消息助手 1.8.0 更新内容\r\n\r\n" +
            "1. 新增在线更新，可从托盘检查更新并查看更新日志。\r\n" +
            "2. 更新程序会校验 SHA-256，并保留用户当前的程序文件名。\r\n" +
            "3. 缩小微信 UI 自动化事件监听范围，改善打开会话后迟迟不朗读的问题。\r\n" +
            "4. 修复 UI 自动化事件未按原订阅对象注销造成的资源泄漏。";
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
                "--apply-update " + processId + " " + Quote(packagePath) + " " + Quote(target));
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

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
