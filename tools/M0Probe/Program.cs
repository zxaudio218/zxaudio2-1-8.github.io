using System;
using System.Collections.Generic;
using System.Text;

namespace M0Probe
{
    internal static class Program
    {
        private static readonly IDictionary<string, Action> Commands =
            new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase)
            {
                { "windows", WindowProbe.PrintWechatWindows },
                { "tree", AutomationProbe.PrintTrees },
                { "latest", AutomationProbe.PrintLatestMessages },
                { "zhengdu-dll", ZhengduProbe.TestDll },
                { "zhengdu-web", ZhengduProbe.TestWebApi },
                { "baoyi-notification", BaoyiProbe.TestNotification },
                { "baoyi-live", BaoyiProbe.TestLiveRegion }
            };

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
            Console.Title = "WeChatMsgHelper M0Probe";
            return args.Length > 0 ? RunCommand(args[0]) : RunInteractive();
        }

        private static int RunInteractive()
        {
            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("WeChatMsgHelper M0Probe (.NET Framework 4.8 / x64)");
                Console.WriteLine("1. 枚举微信窗口");
                Console.WriteLine("2. 打印微信 UIA 树（深度 <= 8）");
                Console.WriteLine("3. 读取消息列表最后一条消息");
                Console.WriteLine("4. 测试争渡独立 DLL 通道");
                Console.WriteLine("5. 测试争渡 Web API");
                Console.WriteLine("6. 测试保益 UIA Notification");
                Console.WriteLine("7. 测试保益 UIA LiveRegion");
                Console.WriteLine("0. 退出");
                Console.Write("请选择：");

                string command;
                switch (Console.ReadLine())
                {
                    case "1": command = "windows"; break;
                    case "2": command = "tree"; break;
                    case "3": command = "latest"; break;
                    case "4": command = "zhengdu-dll"; break;
                    case "5": command = "zhengdu-web"; break;
                    case "6": command = "baoyi-notification"; break;
                    case "7": command = "baoyi-live"; break;
                    case "0": return 0;
                    default: Console.WriteLine("无效选择。"); continue;
                }

                RunCommand(command);
                Console.WriteLine();
                Console.Write("按 Enter 返回菜单...");
                Console.ReadLine();
                Console.Clear();
            }
        }

        private static int RunCommand(string command)
        {
            Action action;
            if (!Commands.TryGetValue(command, out action))
            {
                Console.WriteLine("用法：M0Probe.exe [windows|tree|latest|zhengdu-dll|zhengdu-web|baoyi-notification|baoyi-live]");
                return 2;
            }

            try
            {
                action();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("探测失败：{0}", ex.Message);
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }
    }
}
