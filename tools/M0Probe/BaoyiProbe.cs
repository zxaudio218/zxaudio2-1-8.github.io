using System;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Forms;

namespace M0Probe
{
    internal static class BaoyiProbe
    {
        internal static void TestNotification()
        {
            using (ProviderHost host = new ProviderHost())
            {
                host.Start();
                Console.WriteLine("UIA 客户端正在监听：{0}", AutomationInteropProvider.ClientsAreListening);
                int result = NativeMethods.UiaRaiseNotificationEvent(
                    host.Provider,
                    0,
                    0,
                    "M0Probe 保益通知测试",
                    "WeChatMsgHelper.M0Probe.Notification");
                Console.WriteLine("UiaRaiseNotificationEvent HRESULT：0x{0:X8}", result);
                Console.WriteLine("请确认保益是否通过通知/副语音通道朗读测试文本。");
                Console.Write("确认后按 Enter 结束测试...");
                Console.ReadLine();
            }
        }

        internal static void TestLiveRegion()
        {
            using (ProviderHost host = new ProviderHost())
            {
                host.Start();
                string oldName = host.Provider.Name;
                string newName = "M0Probe 保益 LiveRegion 测试";
                host.Provider.Name = newName;
                AutomationInteropProvider.RaiseAutomationPropertyChangedEvent(
                    host.Provider,
                    new AutomationPropertyChangedEventArgs(AutomationElement.NameProperty, oldName, newName));
                AutomationInteropProvider.RaiseAutomationEvent(
                    AutomationElementIdentifiers.LiveRegionChangedEvent,
                    host.Provider,
                    new AutomationEventArgs(AutomationElementIdentifiers.LiveRegionChangedEvent));
                Console.WriteLine("已更新 Assertive LiveRegion 并触发 LiveRegionChangedEvent。");
                Console.WriteLine("请确认保益是否朗读测试文本。");
                Console.Write("确认后按 Enter 结束测试...");
                Console.ReadLine();
            }
        }
    }

    internal sealed class ProviderHost : IDisposable
    {
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private Thread thread;
        private uint nativeThreadId;
        private Exception startError;
        private ProbeWindow window;
        internal ProbeProvider Provider { get; private set; }

        internal void Start()
        {
            thread = new Thread(ThreadMain);
            thread.IsBackground = true;
            thread.Name = "M0Probe UIA Provider";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!ready.WaitOne(5000)) throw new TimeoutException("创建 UIA Provider 宿主超时。");
            if (startError != null) throw new InvalidOperationException("创建 UIA Provider 宿主失败。", startError);
        }

        public void Dispose()
        {
            if (nativeThreadId != 0) NativeMethods.PostThreadMessage(nativeThreadId, NativeMethods.WmQuit, IntPtr.Zero, IntPtr.Zero);
            if (thread != null && thread.IsAlive) thread.Join(3000);
            ready.Dispose();
        }

        private void ThreadMain()
        {
            nativeThreadId = NativeMethods.GetCurrentThreadId();
            try
            {
                Provider = new ProbeProvider();
                window = new ProbeWindow(Provider);
                Provider.Attach(window);
                ready.Set();
                Application.Run();
            }
            catch (Exception ex)
            {
                startError = ex;
                ready.Set();
            }
            finally
            {
                if (window != null) window.Destroy();
            }
        }
    }

    internal sealed class ProbeWindow : NativeWindow
    {
        private readonly ProbeProvider provider;

        internal ProbeWindow(ProbeProvider provider)
        {
            this.provider = provider;
            CreateParams parameters = new CreateParams();
            parameters.Caption = "M0Probe UIA Provider";
            parameters.X = -32000;
            parameters.Y = -32000;
            parameters.Width = 1;
            parameters.Height = 1;
            parameters.Style = unchecked((int)0x80000000);
            parameters.ExStyle = 0x00000080 | 0x08000000;
            CreateHandle(parameters);
        }

        internal void Destroy()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WmGetObject)
            {
                message.Result = AutomationInteropProvider.ReturnRawElementProvider(Handle, message.WParam, message.LParam, provider);
                return;
            }
            base.WndProc(ref message);
        }
    }

    internal sealed class ProbeProvider : IRawElementProviderSimple
    {
        private ProbeWindow owner;
        private string name = "M0Probe UIA 通知宿主";

        internal string Name
        {
            get { return name; }
            set { name = value ?? string.Empty; }
        }

        internal void Attach(ProbeWindow window) { owner = window; }

        public ProviderOptions ProviderOptions
        {
            get { return ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading; }
        }

        public IRawElementProviderSimple HostRawElementProvider
        {
            get { return owner == null ? null : AutomationInteropProvider.HostProviderFromHandle(owner.Handle); }
        }

        public object GetPatternProvider(int patternId) { return null; }

        public object GetPropertyValue(int propertyId)
        {
            if (propertyId == AutomationElementIdentifiers.NameProperty.Id) return Name;
            if (propertyId == AutomationElementIdentifiers.AutomationIdProperty.Id) return "M0ProbeLiveRegion";
            if (propertyId == AutomationElementIdentifiers.ClassNameProperty.Id) return "M0ProbeLiveRegion";
            if (propertyId == AutomationElementIdentifiers.FrameworkIdProperty.Id) return "Win32";
            if (propertyId == AutomationElementIdentifiers.ControlTypeProperty.Id) return ControlType.Text.Id;
            if (propertyId == AutomationElementIdentifiers.IsControlElementProperty.Id ||
                propertyId == AutomationElementIdentifiers.IsContentElementProperty.Id ||
                propertyId == AutomationElementIdentifiers.IsEnabledProperty.Id) return true;
            if (propertyId == AutomationElementIdentifiers.IsOffscreenProperty.Id) return false;
            if (propertyId == AutomationElementIdentifiers.LiveSettingProperty.Id) return 2;
            if (propertyId == AutomationElementIdentifiers.NativeWindowHandleProperty.Id && owner != null)
                return unchecked((int)owner.Handle.ToInt64());
            return null;
        }
    }
}
