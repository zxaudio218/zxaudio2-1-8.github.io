using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace WeChatMsgHelper
{
    internal sealed class SearchResultAnnouncer : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WhMouseLl = 14;
        private const int HcAction = 0;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyUp = 0x0105;
        private const int WmLButtonUp = 0x0202;
        private const int VkReturn = 0x0D;
        private const int VkSpace = 0x20;
        private const uint GaRoot = 2;

        private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseHookData
        {
            internal NativePoint Point;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maximumCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure procedure, IntPtr module, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);

        private readonly Action<Action> dispatch;
        private readonly Action<string> speak;
        private readonly Action<string> emojiInvoked;
        private readonly AutomationFocusChangedEventHandler focusHandler;
        private readonly AutomationEventHandler invokeHandler;
        private readonly HookProcedure keyboardHookProcedure;
        private readonly HookProcedure mouseHookProcedure;
        private readonly object emojiStateLock = new object();
        private bool focusSubscribed;
        private bool invokeSubscribed;
        private IntPtr keyboardHook;
        private IntPtr mouseHook;
        private string focusedEmojiName = string.Empty;
        private DateTime lastEmojiActivationAt = DateTime.MinValue;
        private string lastEmojiActivationName = string.Empty;
        private int inputDiagnosticsRemaining = 8;
        private string lastKey = string.Empty;

        internal SearchResultAnnouncer(Action<Action> dispatch, Action<string> speak, Action<string> emojiInvoked)
        {
            this.dispatch = dispatch;
            this.speak = speak;
            this.emojiInvoked = emojiInvoked;
            focusHandler = OnFocusChanged;
            invokeHandler = OnInvoked;
            keyboardHookProcedure = OnKeyboardHook;
            mouseHookProcedure = OnMouseHook;
            try
            {
                Automation.AddAutomationFocusChangedEventHandler(focusHandler);
                focusSubscribed = true;
            }
            catch (Exception ex) { AppLog.Write("搜索结果 UIA 监听不可用：" + ex.Message); }
            try
            {
                Automation.AddAutomationEventHandler(InvokePattern.InvokedEvent, AutomationElement.RootElement,
                    TreeScope.Descendants, invokeHandler);
                invokeSubscribed = true;
            }
            catch (Exception ex) { AppLog.Write("表情激活 UIA 监听不可用：" + ex.Message); }
            InstallInputHooks();
        }

        public void Dispose()
        {
            StopInputHooks();
            try { if (focusSubscribed) Automation.RemoveAutomationFocusChangedEventHandler(focusHandler); }
            catch { }
            focusSubscribed = false;
            try
            {
                if (invokeSubscribed)
                    Automation.RemoveAutomationEventHandler(InvokePattern.InvokedEvent, AutomationElement.RootElement, invokeHandler);
            }
            catch { }
            invokeSubscribed = false;
        }

        internal void StopInputHooks()
        {
            if (keyboardHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(keyboardHook); }
                catch { }
                keyboardHook = IntPtr.Zero;
            }
            if (mouseHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(mouseHook); }
                catch { }
                mouseHook = IntPtr.Zero;
            }
        }

        private void OnFocusChanged(object sender, AutomationFocusChangedEventArgs args)
        {
            if (!IsWechatToolPopupForeground())
            {
                ClearFocusedEmoji();
                return;
            }

            AutomationElement element = sender as AutomationElement;
            if (element == null) return;
            try
            {
                AutomationElement top = GetTopLevelElement(element);
                if (!IsWechatToolPopup(element, top))
                {
                    ClearFocusedEmoji();
                    return;
                }

                string emojiName;
                if (TryGetEmojiChoice(element, top, out emojiName))
                {
                    lock (emojiStateLock) focusedEmojiName = emojiName;
                    return;
                }

                ClearFocusedEmoji();
                if (HasEmojiContext(element, top)) return;
                if (!HasSearchContext(element, top)) return;
                string text = element.Current.Name == null ? string.Empty : element.Current.Name.Trim();
                if (text.Length == 0) return;
                int[] runtimeId = element.GetRuntimeId();
                string key = runtimeId == null ? text : string.Join("-", Array.ConvertAll(runtimeId, delegate(int value) { return value.ToString(); })) + ":" + text;
                if (key == lastKey) return;
                lastKey = key;
                dispatch(delegate { speak(text); });
            }
            catch { }
        }

        private void OnInvoked(object sender, AutomationEventArgs args)
        {
            if (!IsWechatToolPopupForeground()) return;

            AutomationElement element = sender as AutomationElement;
            if (element == null || emojiInvoked == null) return;
            try
            {
                AutomationElement top = GetTopLevelElement(element);
                string name;
                if (!IsWechatToolPopup(element, top) || !TryGetEmojiChoice(element, top, out name)) return;
                RecordEmojiActivation(name, "UIA Invoke");
            }
            catch { }
        }

        private void InstallInputHooks()
        {
            IntPtr module = GetModuleHandle(null);
            keyboardHook = SetWindowsHookEx(WhKeyboardLl, keyboardHookProcedure, module, 0);
            if (keyboardHook == IntPtr.Zero)
                AppLog.Write("表情键盘监听不可用，错误码=" + Marshal.GetLastWin32Error());
            mouseHook = SetWindowsHookEx(WhMouseLl, mouseHookProcedure, module, 0);
            if (mouseHook == IntPtr.Zero)
                AppLog.Write("表情鼠标监听不可用，错误码=" + Marshal.GetLastWin32Error());
        }

        private IntPtr OnKeyboardHook(int code, IntPtr message, IntPtr data)
        {
            try
            {
                int messageId = message.ToInt32();
                if (code == HcAction && (messageId == WmKeyUp || messageId == WmSysKeyUp))
                {
                    int virtualKey = Marshal.ReadInt32(data);
                    if ((virtualKey == VkReturn || virtualKey == VkSpace) && IsWechatToolPopupForeground())
                        QueueInputWork(HandleKeyboardEmojiActivation);
                }
            }
            catch { }
            return CallNextHookEx(keyboardHook, code, message, data);
        }

        private IntPtr OnMouseHook(int code, IntPtr message, IntPtr data)
        {
            try
            {
                if (code == HcAction && message.ToInt32() == WmLButtonUp)
                {
                    MouseHookData mouseData = (MouseHookData)Marshal.PtrToStructure(data, typeof(MouseHookData));
                    NativePoint point = mouseData.Point;
                    if (IsWechatToolPopupAtPoint(point))
                        QueueInputWork(delegate { HandleMouseEmojiActivation(point); });
                }
            }
            catch { }
            return CallNextHookEx(mouseHook, code, message, data);
        }

        private void QueueInputWork(Action action)
        {
            if (action == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // Marshal from a worker so the low-level hook returns before UI Automation starts.
                    dispatch(action);
                }
                catch { }
            });
        }

        private void HandleKeyboardEmojiActivation()
        {
            string name = string.Empty;
            bool popupDetected = false;
            try
            {
                AutomationElement element = AutomationElement.FocusedElement;
                if (element != null)
                {
                    AutomationElement top = GetTopLevelElement(element);
                    popupDetected = IsWechatToolPopup(element, top);
                    if (popupDetected && TryGetEmojiChoice(element, top, out name))
                    {
                        RecordEmojiActivation(name, "键盘");
                        return;
                    }
                }
            }
            catch { }

            if (popupDetected)
            {
                lock (emojiStateLock) name = focusedEmojiName;
                if (name.Length > 0)
                {
                    RecordEmojiActivation(name, "键盘焦点缓存");
                    return;
                }
                WriteInputDiagnostic("表情键盘操作未识别到表情焦点");
            }
        }

        private void HandleMouseEmojiActivation(NativePoint point)
        {
            if (!IsWechatToolPopupAtPoint(point)) return;
            try
            {
                AutomationElement element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
                if (element == null) return;
                AutomationElement top = GetTopLevelElement(element);
                string name;
                if (IsWechatToolPopup(element, top) && TryGetEmojiChoice(element, top, out name))
                {
                    RecordEmojiActivation(name, "鼠标");
                    return;
                }
                WriteInputDiagnostic("表情点击未识别：" + DescribeElement(element));
            }
            catch (Exception ex)
            {
                WriteInputDiagnostic("表情点击检查失败：" + ex.Message);
            }
        }

        private void RecordEmojiActivation(string name, string source)
        {
            if (emojiInvoked == null) return;
            string normalizedName = (name ?? string.Empty).Trim();
            DateTime now = DateTime.UtcNow;
            lock (emojiStateLock)
            {
                if (now - lastEmojiActivationAt < TimeSpan.FromMilliseconds(750) &&
                    (normalizedName.Length == 0 || lastEmojiActivationName.Length == 0 ||
                     string.Equals(normalizedName, lastEmojiActivationName, StringComparison.OrdinalIgnoreCase))) return;
                lastEmojiActivationAt = now;
                lastEmojiActivationName = normalizedName;
            }
            AppLog.Write("检测到表情激活动作，来源=" + source + "，项目=" +
                (normalizedName.Length == 0 ? "未命名" : normalizedName));
            emojiInvoked(normalizedName);
        }

        private void ClearFocusedEmoji()
        {
            lock (emojiStateLock) focusedEmojiName = string.Empty;
        }

        private void WriteInputDiagnostic(string text)
        {
            lock (emojiStateLock)
            {
                if (inputDiagnosticsRemaining <= 0) return;
                inputDiagnosticsRemaining--;
            }
            AppLog.Write(text);
        }

        private static AutomationElement GetTopLevelElement(AutomationElement element)
        {
            AutomationElement top = element;
            TreeWalker walker = TreeWalker.RawViewWalker;
            AutomationElement parent;
            while ((parent = walker.GetParent(top)) != null)
            {
                if (parent.Current.ProcessId != element.Current.ProcessId) break;
                top = parent;
            }
            return top;
        }

        private static bool IsWechatToolPopup(AutomationElement element, AutomationElement top)
        {
            int nativeHandle = top.Current.NativeWindowHandle;
            if (nativeHandle == 0 || top.Current.ProcessId != element.Current.ProcessId) return false;
            return IsWechatToolPopupWindow(new IntPtr((long)(uint)nativeHandle));
        }

        private static bool IsWechatToolPopupWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero) return false;
            StringBuilder className = new StringBuilder(256);
            GetClassName(windowHandle, className, className.Capacity);
            return className.ToString() == "Qt51514QWindowToolSaveBits";
        }

        private static bool IsWechatToolPopupForeground()
        {
            IntPtr windowHandle = GetForegroundWindow();
            if (IsWechatToolPopupWindow(windowHandle)) return true;
            return IsWechatToolPopupWindow(GetAncestor(windowHandle, GaRoot));
        }

        private static bool IsWechatToolPopupAtPoint(NativePoint point)
        {
            IntPtr windowHandle = WindowFromPoint(point);
            if (IsWechatToolPopupWindow(windowHandle)) return true;
            return IsWechatToolPopupWindow(GetAncestor(windowHandle, GaRoot));
        }

        private static bool TryGetEmojiChoice(AutomationElement element, AutomationElement top, out string name)
        {
            name = string.Empty;
            if (!HasEmojiContext(element, top)) return false;
            TreeWalker walker = TreeWalker.RawViewWalker;
            AutomationElement current = element;
            while (current != null)
            {
                if (IsEmojiChoice(current, out name)) return true;
                if (Automation.Compare(current, top)) break;
                current = walker.GetParent(current);
            }
            return false;
        }

        private static bool IsEmojiChoice(AutomationElement element, out string name)
        {
            AutomationElement.AutomationElementInformation info = element.Current;
            name = (info.Name ?? string.Empty).Trim();
            if (info.ControlType == ControlType.Edit || info.ControlType == ControlType.Tab ||
                info.ControlType == ControlType.TabItem || info.ControlType == ControlType.ToolBar ||
                info.ControlType == ControlType.Window || info.ControlType == ControlType.Pane ||
                info.ControlType == ControlType.Group || info.ControlType == ControlType.List ||
                info.ControlType == ControlType.ScrollBar || info.ControlType == ControlType.Thumb ||
                info.ControlType == ControlType.Separator || info.ControlType == ControlType.TitleBar ||
                info.ControlType == ControlType.MenuBar || info.ControlType == ControlType.Menu ||
                info.ControlType == ControlType.MenuItem) return false;
            if (name == "表情" || name == "搜索表情" || name == "最近使用" ||
                name == "收藏" || name == "添加" || name == "设置" || name == "关闭" ||
                name == "搜索" || name == "查看更多" || name == "上一页" || name == "下一页") return false;
            if (name.Length > 0) return true;

            System.Windows.Rect bounds = info.BoundingRectangle;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 ||
                bounds.Width > 180 || bounds.Height > 180) return false;
            return info.ControlType == ControlType.Button || info.ControlType == ControlType.Image ||
                info.ControlType == ControlType.ListItem || info.ControlType == ControlType.DataItem ||
                info.ControlType == ControlType.Custom;
        }

        private static string DescribeElement(AutomationElement element)
        {
            try
            {
                AutomationElement.AutomationElementInformation info = element.Current;
                return "名称=" + (info.Name ?? string.Empty) + "，类型=" + info.ControlType.ProgrammaticName +
                    "，类=" + (info.ClassName ?? string.Empty) + "，ID=" + (info.AutomationId ?? string.Empty);
            }
            catch { return "元素信息不可用"; }
        }

        private static bool HasSearchContext(AutomationElement element, AutomationElement top)
        {
            TreeWalker walker = TreeWalker.RawViewWalker;
            AutomationElement current = element;
            while (current != null)
            {
                AutomationElement.AutomationElementInformation info = current.Current;
                string hints = (info.AutomationId ?? string.Empty) + " " +
                    (info.ClassName ?? string.Empty) + " " + (info.Name ?? string.Empty);
                if (hints.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    hints.IndexOf("搜索", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    hints.IndexOf("查找", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (Automation.Compare(current, top)) break;
                current = walker.GetParent(current);
            }
            return false;
        }

        private static bool HasEmojiContext(AutomationElement element, AutomationElement top)
        {
            TreeWalker walker = TreeWalker.RawViewWalker;
            AutomationElement current = element;
            while (current != null)
            {
                AutomationElement.AutomationElementInformation info = current.Current;
                string hints = (info.AutomationId ?? string.Empty) + " " +
                    (info.ClassName ?? string.Empty) + " " + (info.Name ?? string.Empty);
                if (hints.IndexOf("emoji", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    hints.IndexOf("emoticon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    hints.IndexOf("sticker", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    hints.IndexOf("表情", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (Automation.Compare(current, top)) break;
                current = walker.GetParent(current);
            }
            return false;
        }
    }
}
