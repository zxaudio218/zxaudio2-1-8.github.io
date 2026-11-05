using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;

namespace WeChatMsgHelper.Channels
{
    internal enum BaoyiChannelMode
    {
        Independent,
        ScreenReader
    }

    // Uses the current byCtrl v2 ABI. The bundled rack-assistant DLL has a
    // different legacy ABI, so its five-argument BoyCtrlSpeak declaration
    // cannot be reused with the installed BoyPcReader DLL.
    internal sealed class BaoyiChannel : ISpeechChannel, IDisposable
    {
        private const string DllPath = @"C:\Program Files (x86)\BoyPcReader\byctrl-x64.dll";
        private const string WebApi = "http://127.0.0.1:26609/api";

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int InitializeDelegate([MarshalAs(UnmanagedType.LPWStr)] string logPath);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void UninitializeDelegate();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool IsRunningDelegate();
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int SpeakDelegate(
            [MarshalAs(UnmanagedType.LPWStr)] string text,
            [MarshalAs(UnmanagedType.I1)] bool withSlave,
            [MarshalAs(UnmanagedType.LPWStr)] string slaveName,
            [MarshalAs(UnmanagedType.I1)] bool append,
            [MarshalAs(UnmanagedType.I1)] bool allowBreak,
            IntPtr completion);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StopDelegate(
            [MarshalAs(UnmanagedType.I1)] bool withSlave,
            [MarshalAs(UnmanagedType.LPWStr)] string slaveName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        private readonly object syncRoot = new object();
        private readonly AppConfig config;
        private readonly BaoyiChannelMode mode;
        private IntPtr module;
        private UninitializeDelegate uninitialize;
        private IsRunningDelegate isRunning;
        private SpeakDelegate speak;
        private StopDelegate stop;
        private bool initialized;
        private bool ready;
        private bool useWeb;
        private bool disposed;

        internal BaoyiChannel(AppConfig config, BaoyiChannelMode mode)
        {
            this.config = config;
            this.mode = mode;
        }

        private bool UsesIndependentChannel { get { return mode == BaoyiChannelMode.Independent; } }
        private string IndependentChannelName
        {
            get
            {
                return string.IsNullOrWhiteSpace(config.BaoyiChannelName) ? "WeChatMsg" : config.BaoyiChannelName.Trim();
            }
        }

        public string Name
        {
            get
            {
                string name = UsesIndependentChannel ? "保益第三方独立通道 " + IndependentChannelName : "保益读屏通道";
                return name + (useWeb ? " Web" : string.Empty);
            }
        }

        public bool IsAvailable { get { return ready; } }

        public bool Initialize(out string error)
        {
            lock (syncRoot)
            {
                if (disposed) { error = "保益通道已释放。"; return false; }
                if (ready) { error = string.Empty; return true; }

                string dllError;
                if (TryInitializeDll(out dllError)) { error = string.Empty; return true; }
                string webError;
                if (TryInitializeWeb(out webError)) { error = string.Empty; return true; }
                error = "DLL：" + dllError + "；Web API：" + webError;
                return false;
            }
        }

        public bool Speak(string text, bool interrupt)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            lock (syncRoot)
            {
                string error;
                if (disposed || (!ready && !Initialize(out error))) return false;
                bool append = !interrupt;
                if (useWeb)
                {
                    if (TryWebAction("speech.speak", text, append, out error)) return true;
                    AppLog.Write("保益 Web API 朗读失败：" + error);
                    ready = false;
                    return false;
                }

                int result;
                string slaveName = UsesIndependentChannel ? IndependentChannelName : null;
                try { result = speak(text, UsesIndependentChannel, slaveName, append, true, IntPtr.Zero); }
                catch (Exception ex)
                {
                    AppLog.Write("保益 BoyCtrlSpeak 异常：" + ex.Message);
                    result = 1;
                }
                if (result == 0) return true;

                AppLog.Write("保益 BoyCtrlSpeak 返回：" + result + "，尝试 Web API。");
                ReleaseDll();
                if (!TryInitializeWeb(out error)) return false;
                return TryWebAction("speech.speak", text, append, out error);
            }
        }

        public bool Submit(string text, bool interrupt)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            lock (syncRoot)
            {
                if (disposed || !ready) return false;
                bool append = !interrupt;
                if (useWeb)
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        string ignored;
                        TryWebAction("speech.speak", text, append, out ignored);
                    });
                    return true;
                }

                try
                {
                    return speak(text, UsesIndependentChannel, UsesIndependentChannel ? IndependentChannelName : null,
                        append, true, IntPtr.Zero) == 0;
                }
                catch (Exception ex)
                {
                    AppLog.Write("保益退出提示提交异常：" + ex.Message);
                    return false;
                }
            }
        }

        public bool Stop()
        {
            StopDelegate stopDelegate;
            bool independent;
            bool web;
            lock (syncRoot)
            {
                if (!ready) return true;
                stopDelegate = stop;
                independent = UsesIndependentChannel;
                web = useWeb;
            }
            try
            {
                if (web)
                {
                    string error;
                    return TryWebAction("speech.stop", null, false, out error);
                }
                return stopDelegate == null || stopDelegate(independent, independent ? IndependentChannelName : null) == 0;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed) return;
                try { Stop(); } catch { }
                disposed = true;
                ReleaseDll();
            }
        }

        internal static bool IsReaderRunning()
        {
            Process[] processes = Process.GetProcessesByName("BoyPcReader");
            bool result = processes.Length > 0;
            foreach (Process process in processes) process.Dispose();
            return result;
        }

        private bool TryInitializeDll(out string error)
        {
            ReleaseDll();
            if (!File.Exists(DllPath))
            {
                error = "未找到 " + DllPath;
                return false;
            }
            module = LoadLibrary(DllPath);
            if (module == IntPtr.Zero)
            {
                error = "加载 byctrl-x64.dll 失败，Win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            try
            {
                InitializeDelegate initialize = Resolve<InitializeDelegate>("BoyCtrlInitialize");
                uninitialize = Resolve<UninitializeDelegate>("BoyCtrlUninitialize");
                isRunning = Resolve<IsRunningDelegate>("BoyCtrlIsReaderRunning");
                speak = Resolve<SpeakDelegate>("BoyCtrlSpeak2");
                stop = Resolve<StopDelegate>("BoyCtrlStopSpeaking2");
                int result = initialize(null);
                if (result != 0)
                {
                    error = "BoyCtrlInitialize 返回 " + result;
                    ReleaseDll();
                    return false;
                }
                initialized = true;
                if (!isRunning())
                {
                    error = "保益悦听未运行";
                    ReleaseDll();
                    return false;
                }
                useWeb = false;
                ready = true;
                AppLog.Write("保益 BoyCtrlSpeak2 通道初始化成功：" + Name + "，useSlave=" + UsesIndependentChannel);
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                ReleaseDll();
                return false;
            }
        }

        private bool TryInitializeWeb(out string error)
        {
            string response;
            if (!SendWebRequest(BuildUrl("speech.status", null, false), out response, out error)) return false;
            if (response.StartsWith("status:", StringComparison.OrdinalIgnoreCase))
                response = response.Substring("status:".Length).Trim();
            int status;
            if (!int.TryParse(response, out status) || (status != 0 && status != 1))
            {
                error = "状态返回无效：" + response;
                return false;
            }
            useWeb = true;
            ready = true;
            AppLog.Write("保益 Web API 通道初始化成功：" + Name + "，use_slave=" + UsesIndependentChannel);
            return true;
        }

        private bool TryWebAction(string action, string text, bool append, out string error)
        {
            string response;
            if (!SendWebRequest(BuildUrl(action, text, append), out response, out error)) return false;
            if (string.Equals(response, "OK", StringComparison.OrdinalIgnoreCase)) return true;
            error = "返回：" + response;
            return false;
        }

        private string BuildUrl(string action, string text, bool append)
        {
            string url = WebApi + "?action=" + Uri.EscapeDataString(action) +
                "&use_slave=" + (UsesIndependentChannel ? "true" : "false");
            if (UsesIndependentChannel) url += "&slave_name=" + Uri.EscapeDataString(IndependentChannelName);
            if (text != null) url += "&text=" + Uri.EscapeDataString(text) + "&append=" + (append ? "true" : "false");
            return url;
        }

        private static bool SendWebRequest(string url, out string responseText, out string error)
        {
            responseText = string.Empty;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Proxy = null;
                request.KeepAlive = false;
                request.Timeout = 800;
                request.ReadWriteTimeout = 800;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    responseText = reader.ReadToEnd().Trim();
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        error = string.Empty;
                        return true;
                    }
                    error = "HTTP " + (int)response.StatusCode;
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private T Resolve<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        private void ReleaseDll()
        {
            ready = false;
            if (initialized && uninitialize != null)
            {
                try { uninitialize(); } catch { }
            }
            initialized = false;
            uninitialize = null;
            isRunning = null;
            speak = null;
            stop = null;
            if (module != IntPtr.Zero)
            {
                FreeLibrary(module);
                module = IntPtr.Zero;
            }
        }
    }
}
