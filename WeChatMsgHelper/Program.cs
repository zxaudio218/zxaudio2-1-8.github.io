using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace WeChatMsgHelper
{
    internal static class Program
    {
        private const string MutexName = "Local\\WeChatMsgHelper-220021ea333fdbd86d9ef37f181002efb7b07f44";

        [STAThread]
        private static void Main(string[] args)
        {
            if (UpdateInstaller.TryRunApplyMode(args)) return;
            InitializeCrashHandling();
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                GC.KeepAlive(mutex);
            }
        }

        private static void InitializeCrashHandling()
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatMsgHelper");
                AppLog.Initialize(directory);
            }
            catch { }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args)
            {
                Exception exception = args == null ? null : args.Exception;
                AppLog.Write("UI 线程异常已隔离：" + FormatException(exception));
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args)
            {
                AppLog.Write("后台线程异常：" + FormatException(args == null ? null : args.ExceptionObject as Exception));
            };
        }

        private static string FormatException(Exception exception)
        {
            if (exception == null) return "未知异常";
            return exception.GetType().FullName + "，HRESULT=0x" + exception.HResult.ToString("X8") + "，" + exception.Message;
        }
    }
}
