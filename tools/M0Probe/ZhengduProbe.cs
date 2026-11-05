using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace M0Probe
{
    internal static class ZhengduProbe
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int InitTtsDelegate(int type, [MarshalAs(UnmanagedType.LPWStr)] string channelName, [MarshalAs(UnmanagedType.Bool)] bool keyDownInterrupt);

        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int SpeakDelegate([MarshalAs(UnmanagedType.LPWStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool interrupt);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int GetSpeakStateDelegate();

        internal static void TestDll()
        {
            string path = FindDllPath();
            if (string.IsNullOrEmpty(path))
            {
                Console.WriteLine("未找到 ZDSRAPI_x64.dll。已检查默认安装目录和正在运行的 zdsr 进程目录。");
                return;
            }

            Console.WriteLine("DLL 路径：{0}", path);
            IntPtr module = NativeMethods.LoadLibrary(path);
            if (module == IntPtr.Zero)
            {
                Console.WriteLine("LoadLibrary 失败，Win32 错误码：{0}", Marshal.GetLastWin32Error());
                return;
            }

            try
            {
                InitTtsDelegate init = LoadDelegate<InitTtsDelegate>(module, "InitTTS");
                SpeakDelegate speak = LoadDelegate<SpeakDelegate>(module, "Speak");
                GetSpeakStateDelegate getSpeakState = LoadDelegate<GetSpeakStateDelegate>(module, "GetSpeakState");
                int initResult = init(1, "M0Probe", false);
                Console.WriteLine("InitTTS(1, \"M0Probe\", false) 返回：{0}", initResult);
                if (initResult != 0)
                {
                    Console.WriteLine("初始化未成功，不继续调用 Speak。");
                    return;
                }

                int state = 9;
                for (int attempt = 0; attempt < 30 && state == 9; attempt++)
                {
                    Thread.Sleep(100);
                    state = getSpeakState();
                }
                Console.WriteLine("GetSpeakState 返回：{0}", state);

                int speakResult = speak("测试消息", false);
                Console.WriteLine("Speak(\"测试消息\", false) 返回：{0}", speakResult);
                Console.WriteLine("请在微信输入框持续打字，确认测试消息是否完整朗读且不被按键回声打断。");
                Console.Write("确认后按 Enter 释放 DLL...");
                Console.ReadLine();
            }
            finally
            {
                NativeMethods.FreeLibrary(module);
            }
        }

        internal static void TestWebApi()
        {
            string url = "http://127.0.0.1:26516/api?action=speak&channel=1&text=" +
                Uri.EscapeDataString("测试消息") + "&interrupt=0";
            Console.WriteLine("请求：{0}", url);

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 2000;
            request.ReadWriteTimeout = 2000;
            request.Proxy = null;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    Console.WriteLine("HTTP 状态：{0} {1}", (int)response.StatusCode, response.StatusDescription);
                    Console.WriteLine("响应正文：{0}", reader.ReadToEnd().TrimStart('\uFEFF'));
                }
            }
            catch (WebException ex)
            {
                Console.WriteLine("Web API 请求失败：{0}", ex.Message);
                if (ex.Response != null)
                {
                    using (HttpWebResponse response = (HttpWebResponse)ex.Response)
                    {
                        Console.WriteLine("HTTP 状态：{0} {1}", (int)response.StatusCode, response.StatusDescription);
                    }
                }
            }
        }

        private static T LoadDelegate<T>(IntPtr module, string name) where T : class
        {
            IntPtr address = NativeMethods.GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        private static string FindDllPath()
        {
            List<string> candidates = new List<string>();
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "zdsr", "zdsr", "ZDSRAPI_x64.dll"));
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.ProcessName.StartsWith("zdsr", StringComparison.OrdinalIgnoreCase))
                    {
                        string directory = Path.GetDirectoryName(process.MainModule.FileName);
                        if (!string.IsNullOrEmpty(directory)) candidates.Add(Path.Combine(directory, "ZDSRAPI_x64.dll"));
                    }
                }
                catch { }
                finally { process.Dispose(); }
            }

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
