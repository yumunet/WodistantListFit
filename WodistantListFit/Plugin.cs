using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.WindowsAndMessaging;
using Wodistant.PluginLibrary.Hook.Keyboard;
using Wodistant.PluginLibrary.MenuBar;

namespace WodistantListFit
{
    public class Plugin : Wodistant.PluginLibrary.PluginBase
    {
        public override string Name => "ListFit";

        public override string Author => "yumu";

        public override string Version => "1.0.1";

        public override string Description => "ドロップダウンリストの横幅を自動的に調整します。";

        public override MenuBarInfo ManuBarInfomation => null;

        public override IKeyboardAction[] KeyboardActions => new IKeyboardAction[] { };

        private readonly CancellationTokenSource lifetimeCts = new();
        private CancellationTokenSource sessionCts;
        private Task connectionMonitorTask;
        private Task activeWindowMonitorTask;
        private Task updateTask;

        private uint woditorPId;
        private Dictionary<HWND, ComboBoxState> knownComboBoxes = new();
        private bool isActiveWindowChanging = false;

        public override void OnInitializePlugin()
        {
            connectionMonitorTask = MonitorConnectionAsync(lifetimeCts.Token);
        }

        public override void OnFinalizePlugin()
        {
            lifetimeCts.Cancel();
            try
            {
                connectionMonitorTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { }
            lifetimeCts.Dispose();

            StopSessionTasksAsync().GetAwaiter().GetResult();

            if (woditorPId != 0)
            {
                ReestAllDropDownListWidth();
            }
        }

        private async Task MonitorConnectionAsync(CancellationToken token)
        {
            bool isWoditorConnected = false;

            while (true)
            {
                await Task.Delay(10, token).ConfigureAwait(false);

                if (Host.Environment.IsWoditorConnected)
                {
                    if (!isWoditorConnected)
                    {
                        isWoditorConnected = true;
                        OnConnected();
                    }
                }
                else
                {
                    if (isWoditorConnected)
                    {
                        isWoditorConnected = false;
                        await OnDisconnectedAsync().ConfigureAwait(false);
                    }
                }
            }
        }

        private void OnConnected()
        {
            uint pId;
            unsafe { PInvoke.GetWindowThreadProcessId((HWND)Host.MapEditor.GetMapEditorWindowHandle(), &pId); }
            woditorPId = pId;

            StartSessionTasks();
        }

        private async Task OnDisconnectedAsync()
        {
            await StopSessionTasksAsync();

            // 接続解除時にすべてのドロップダウンリストの横幅を元に戻す
            ReestAllDropDownListWidth();

            woditorPId = 0;
        }

        private void StartSessionTasks()
        {
            sessionCts = new CancellationTokenSource();
            activeWindowMonitorTask = MonitorActiveWindowAsync(sessionCts.Token);
            updateTask = UpdateLoopAsync(sessionCts.Token);
        }

        private async Task StopSessionTasksAsync()
        {
            if (sessionCts is null)
                return;

            sessionCts.Cancel();
            try
            {
                await Task.WhenAll(activeWindowMonitorTask, updateTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            sessionCts.Dispose();
            sessionCts = null;

            ReplaceComboBoxCache();
        }

        private async Task MonitorActiveWindowAsync(CancellationToken token)
        {
            HWND lastWindow = HWND.Null;

            while (true)
            {
                await Task.Delay(10, token).ConfigureAwait(false);

                HWND activeWindow = PInvoke.GetForegroundWindow();
                if (activeWindow != lastWindow)
                {
                    lastWindow = activeWindow;

                    uint activePId;
                    unsafe { PInvoke.GetWindowThreadProcessId(activeWindow, &activePId); }
                    if (activePId == woditorPId)
                    {
                        OnActiveWindowChanged(activeWindow);
                    }
                }
            }
        }

        private async Task UpdateLoopAsync(CancellationToken token)
        {
            while (true)
            {
                await Task.Delay(100, token).ConfigureAwait(false);

                // すでにアクティブウィンドウを処理中の場合はスキップ
                if (Volatile.Read(ref isActiveWindowChanging))
                    continue;

                HWND activeWindow = PInvoke.GetForegroundWindow();

                uint activePId;
                unsafe { PInvoke.GetWindowThreadProcessId(activeWindow, &activePId); }
                if (activePId != woditorPId)
                    continue;

                var cache = Volatile.Read(ref knownComboBoxes);
                PInvoke.EnumChildWindows(activeWindow, (childWindow, lParam) =>
                {
                    if (!IsTargetComboBox(childWindow))
                        return true;

                    // コンボボックスの状態（項目数と最初のテキスト）が前回と同じ場合はスキップする
                    ComboBoxState state = GetComboBoxState(childWindow);
                    if (cache.TryGetValue(childWindow, out ComboBoxState lastState))
                    {
                        if (state.ItemCount == lastState.ItemCount && state.FirstText == lastState.FirstText)
                        {
                            return true;
                        }
                    }
                    cache[childWindow] = state;

                    FitDropDownListWidth(childWindow);
                    return true;
                }, 0);
            }
        }

        private void OnActiveWindowChanged(HWND activeWindow)
        {
            Volatile.Write(ref isActiveWindowChanging, true);
            try
            {
                // アクティブウィンドウが切り替わったときに即時更新
                ReplaceComboBoxCache();

                var cache = Volatile.Read(ref knownComboBoxes);
                PInvoke.EnumChildWindows(activeWindow, (childWindow, lParam) =>
                {
                    if (!IsTargetComboBox(childWindow))
                        return true;

                    ComboBoxState state = GetComboBoxState(childWindow);
                    cache[childWindow] = state;

                    FitDropDownListWidth(childWindow);
                    return true;
                }, 0);
            }
            finally
            {
                Volatile.Write(ref isActiveWindowChanging, false);
            }
        }

        private bool IsTargetComboBox(HWND window)
        {
            if (!PInvoke.IsWindowVisible(window) || !PInvoke.IsWindowEnabled(window))
                return false;

            string className;
            unsafe
            {
                const int classNameLength = 256;
                fixed (char* classNameChars = new char[classNameLength])
                {
                    PInvoke.GetClassName(window, classNameChars, classNameLength);
                    className = new string(classNameChars);
                }
            }
            if (className != "ComboBox")
                return false;

            return true;
        }

        private ComboBoxState GetComboBoxState(HWND comboBox)
        {
            int itemCount = (int)(nint)PInvoke.SendMessage(comboBox, PInvoke.CB_GETCOUNT, 0, 0);

            string firstText = "";
            const int index = 0;
            int textLength = (int)(nint)PInvoke.SendMessage(comboBox, PInvoke.CB_GETLBTEXTLEN, index, 0);
            if (textLength > 0)
            {
                unsafe
                {
                    fixed (char* textChars = new char[textLength + 1])
                    {
                        PInvoke.SendMessage(comboBox, PInvoke.CB_GETLBTEXT, index, (nint)textChars);
                        firstText = new string(textChars);
                    }
                }
            }
            return new ComboBoxState() { ItemCount = itemCount, FirstText = firstText };
        }

        private void ReplaceComboBoxCache()
        {
            Interlocked.Exchange(ref knownComboBoxes, new Dictionary<HWND, ComboBoxState>());
        }

        private unsafe void FitDropDownListWidth(HWND comboBox)
        {
#if DEBUG
            var stopwatch = new Stopwatch();
            stopwatch.Start();
#endif
            int itemCount = (int)(nint)PInvoke.SendMessage(comboBox, PInvoke.CB_GETCOUNT, 0, 0);
            if (itemCount == PInvoke.CB_ERR)
            {
                Debug.WriteLine("CB_GETCOUNTが失敗");
                return;
            }

            int width;
            if (itemCount == 0)
            {
                // 項目がない場合は0pxにする（コンボボックスの幅と同じになる）
                width = 0;
            }
            else
            {
                SIZE longestTextSize = new(0, 0);

                // 外部プロセスのフォントハンドルはそのまま使えないので作り直す
                HFONT originalFont = (HFONT)(nint)PInvoke.SendMessage(comboBox, PInvoke.WM_GETFONT, 0, 0);
                LOGFONTW logFont;
                PInvoke.GetObject(originalFont, sizeof(LOGFONTW), &logFont);
                HFONT newFont = PInvoke.CreateFontIndirect(logFont);

                HDC hDC = PInvoke.CreateCompatibleDC((HDC)(void*)0);
                HBITMAP bitmap = PInvoke.CreateCompatibleBitmap(hDC, 1, 1);
                HGDIOBJ oldBitmap = PInvoke.SelectObject(hDC, bitmap);
                HGDIOBJ oldFont = PInvoke.SelectObject(hDC, newFont);

                var textLengthes = new int[itemCount];
                for (int i = 0; i < itemCount; i++)
                {
                    int textLength = (int)(nint)PInvoke.SendMessage(comboBox, PInvoke.CB_GETLBTEXTLEN, (nuint)i, 0);
                    if (textLength == PInvoke.CB_ERR)
                    {
                        Debug.WriteLine("CB_GETLBTEXTLENが失敗");
                        break;
                    }
                    textLengthes[i] = textLength;
                }

                // 長さは文字単位だが2バイト文字だと幅がほぼ倍になるため、最も長いテキストの半分より長いテキストのサイズをチェックする
                int lengthLimit = textLengthes.Max() / 2;
                for (int i = 0; i < itemCount; i++)
                {
                    int length = textLengthes[i];
                    if (length >= lengthLimit)
                    {
                        string text;
                        fixed (char* textChars = new char[length + 1])
                        {
                            PInvoke.SendMessage(comboBox, PInvoke.CB_GETLBTEXT, (nuint)i, (nint)textChars);
                            text = new string(textChars);
                        }

                        PInvoke.GetTextExtentPoint32W(hDC, text, text.Length, out SIZE textSize);
                        if (textSize.Width > longestTextSize.Width)
                        {
                            longestTextSize = textSize;
                        }
                    }
                }

                PInvoke.SelectObject(hDC, oldBitmap);
                PInvoke.SelectObject(hDC, oldFont);
                PInvoke.DeleteObject(bitmap);
                PInvoke.DeleteDC(hDC);
                PInvoke.DeleteObject(newFont);

                width = longestTextSize.Width + longestTextSize.Height; // 余分に1文字分の幅を追加する

                // スクロールバーがついている場合は、その分の横幅を足す
                COMBOBOXINFO comboBoxInfo = new() { cbSize = (uint)sizeof(COMBOBOXINFO) };
                PInvoke.GetComboBoxInfo(comboBox, ref comboBoxInfo);
                int style = PInvoke.GetWindowLong(comboBoxInfo.hwndList, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
                if ((style & (int)WINDOW_STYLE.WS_VSCROLL) != 0)
                {
                    width += PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVSCROLL);
                }
            }

            bool isListOpen = (PInvoke.SendMessage(comboBox, PInvoke.CB_GETDROPPEDSTATE, 0, 0) != 0);
            PInvoke.SendMessage(comboBox, PInvoke.CB_SETDROPPEDWIDTH, (nuint)width, 0);

            // リストが開かれていた場合は、CB_SETDROPPEDWIDTHでリストが閉じられてしまうため、再度開く
            // テキストサイズの取得処理中やアクティブウィンドウを切り替えた瞬間などに、ユーザーに開かれる可能性がある
            if (isListOpen)
            {
                PInvoke.SendMessage(comboBox, PInvoke.CB_SHOWDROPDOWN, 1, 0);
            }
#if DEBUG
            stopwatch.Stop();
            Debug.WriteLine($"{(int)(void*)comboBox:X8} {stopwatch.ElapsedMilliseconds}ms");
#endif
        }

        private void ReestAllDropDownListWidth()
        {
            PInvoke.EnumWindows((window, lParam) =>
            {
                uint pId;
                unsafe { PInvoke.GetWindowThreadProcessId(window, &pId); }
                if (pId == woditorPId)
                {
                    PInvoke.EnumChildWindows(window, (childWindow, lParam2) =>
                    {
                        string className;
                        unsafe
                        {
                            const int classNameLength = 256;
                            fixed (char* classNameChars = new char[classNameLength])
                            {
                                PInvoke.GetClassName(childWindow, classNameChars, classNameLength);
                                className = new string(classNameChars);
                            }
                        }
                        if (className == "ComboBox")
                        {
                            // 0ではなく1でリセットできる
                            PInvoke.SendMessage(childWindow, PInvoke.CB_SETDROPPEDWIDTH, 1, 0);
                        }
                        return true;
                    }, 0);
                }
                return true;
            }, 0);
        }

        private struct ComboBoxState
        {
            public int ItemCount;
            public string FirstText;
        }
    }
}
