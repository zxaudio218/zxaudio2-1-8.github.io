using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace WeChatMsgHelper.Channels
{
    internal enum ZhengduChannelMode
    {
        Independent,
        ScreenReader
    }

    internal sealed class ZhengduChannel : ISpeechChannel, IDisposable
    {
        private const string FixedChannelName = "WeChatMsg";

        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int InitTtsDelegate(int type, [MarshalAs(UnmanagedType.LPWStr)] string channelName, [MarshalAs(UnmanagedType.Bool)] bool keyDownInterrupt);
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int SpeakDelegate([MarshalAs(UnmanagedType.LPWStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool interrupt);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GetSpeakStateDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void StopSpeakDelegate();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string fileName);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        private readonly object syncRoot = new object();
        private readonly AppConfig config;
        private readonly ZhengduChannelMode mode;
        private IntPtr module;
        private InitTtsDelegate initTts;
        private SpeakDelegate speak;
        private GetSpeakStateDelegate getSpeakState;
        private StopSpeakDelegate stopSpeak;
        private bool ready;
        private bool disposed;

        internal ZhengduChannel(AppConfig config, ZhengduChannelMode mode)
        {
            this.config = config;
            this.mode = mode;
        }

        public string Name { get { return mode == ZhengduChannelMode.Independent ? "争渡独立通道 " + FixedChannelName : "争渡读屏通道"; } }
        public bool IsAvailable { get { return ready; } }

        public bool Initialize(out string error)
        {
            lock (syncRoot) { return TryInitialize(out error); }
        }

        public bool Speak(string text, bool interrupt)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            lock (syncRoot)
            {
                if (disposed) return false;
                string error;
                if (!ready && !TryInitialize(out error))
                {
                    AppLog.Write(Name + "初始化失败：" + error);
                    return false;
                }
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    int result;
                    try { result = speak(text, interrupt); }
                    catch (Exception ex) { AppLog.Write("争渡 Speak 异常：" + ex.Message); result = 8; }
                    if (result == 0)
                    {
                        int state = 4;
                        bool observedSpeaking = false;
                        for (int sample = 0; sample < 8; sample++)
                        {
                            Thread.Sleep(50);
                            state = getSpeakState();
                            if (state == 3) { observedSpeaking = true; break; }
                        }
                        AppLog.Write("争渡 Speak=0，状态=" + state + "，朗读中=" + observedSpeaking);
                        if (state == 3 || state == 4) return true;
                        result = state;
                    }
                    AppLog.Write("争渡 Speak 返回：" + result);
                    if (result != 2 && result != 8 && result != 9) return false;
                    ready = false;
                    Thread.Sleep(150);
                    if (!TryInitialize(out error)) return false;
                }
                return false;
            }
        }

        public bool Submit(string text, bool interrupt)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            lock (syncRoot)
            {
                if (disposed || !ready) return false;
                try { return speak(text, interrupt) == 0; }
                catch (Exception ex)
                {
                    AppLog.Write("争渡退出提示提交异常：" + ex.Message);
                    return false;
                }
            }
        }

        public bool Stop()
        {
            StopSpeakDelegate stopper;
            lock (syncRoot)
            {
                stopper = ready ? stopSpeak : null;
            }
            try { if (stopper != null) stopper(); }
            catch { }
            return true;
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed) return;
                Stop();
                disposed = true;
                if (module != IntPtr.Zero) FreeLibrary(module);
                module = IntPtr.Zero;
            }
        }

        private bool TryInitialize(out string error)
        {
            error = string.Empty;
            if (disposed) { error = "通道已释放。"; return false; }
            if (!IsZhengduRunning()) { error = "争渡读屏未运行。"; ready = false; return false; }
            if (!LoadNative(out error)) { ready = false; return false; }
            try
            {
                int state = 9;
                for (int initializeAttempt = 0; initializeAttempt < 2; initializeAttempt++)
                {
                    int type = mode == ZhengduChannelMode.Independent ? 1 : 0;
                    string channelName = mode == ZhengduChannelMode.Independent ? FixedChannelName : null;
                    int result = initTts(type, channelName, config.ZhengduKeyDownInterrupt);
                    if (result != 0) { error = "InitTTS 返回 " + result; ready = false; return false; }
                    state = 9;
                    for (int stateAttempt = 0; stateAttempt < 30 && state == 9; stateAttempt++)
                    {
                        Thread.Sleep(100);
                        state = getSpeakState();
                    }
                    if (state == 3 || state == 4) break;
                    Thread.Sleep(200);
                }
                ready = state == 3 || state == 4;
                if (!ready) error = "GetSpeakState 返回 " + state;
                return ready;
            }
            catch (Exception ex)
            {
                ready = false;
                error = ex.Message;
                return false;
            }
        }

        private bool LoadNative(out string error)
        {
            error = string.Empty;
            if (module != IntPtr.Zero) return true;
            string path = FindDllPath();
            if (path == null) { error = "未找到 ZDSRAPI_x64.dll。"; return false; }
            module = LoadLibrary(path);
            if (module == IntPtr.Zero) { error = "LoadLibrary 失败：" + Marshal.GetLastWin32Error(); return false; }
            try
            {
                initTts = LoadDelegate<InitTtsDelegate>("InitTTS");
                speak = LoadDelegate<SpeakDelegate>("Speak");
                getSpeakState = LoadDelegate<GetSpeakStateDelegate>("GetSpeakState");
                stopSpeak = LoadDelegate<StopSpeakDelegate>("StopSpeak");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                FreeLibrary(module);
                module = IntPtr.Zero;
                return false;
            }
        }

        private T LoadDelegate<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        private static string FindDllPath()
        {
            List<string> paths = new List<string>();
            paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "zdsr", "zdsr", "ZDSRAPI_x64.dll"));
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.ProcessName.StartsWith("zdsr", StringComparison.OrdinalIgnoreCase))
                        paths.Add(Path.Combine(Path.GetDirectoryName(process.MainModule.FileName), "ZDSRAPI_x64.dll"));
                }
                catch { }
                finally { process.Dispose(); }
            }
            foreach (string path in paths) if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            return null;
        }

        private static bool IsZhengduRunning()
        {
            foreach (Process process in Process.GetProcesses())
            {
                try { if (process.ProcessName.StartsWith("zdsr", StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
                finally { process.Dispose(); }
            }
            return false;
        }
    }
}
