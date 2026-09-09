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

        public override string Version => "1.1.0";

        public override string Description => "ドロップダウンリストの横幅を自動的に調整します。";

        public override MenuBarInfo ManuBarInfomation => null;

        public override IKeyboardAction[] KeyboardActions => new IKeyboardAction[] { };

        private readonly CancellationTokenSource lifetimeCts = new();
        private CancellationTokenSource sessionCts;
        private Task connectionMonitorTask;
        private Task updateTask;

        private readonly Dictionary<HWND, ComboBoxState> knownComboBoxes = new();
        private uint woditorPId;

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

            StopSessionAsync().GetAwaiter().GetResult();

            if (woditorPId != 0)
            {
                ResetAllDropDownListWidth();
            }
        }

        private async Task MonitorConnectionAsync(CancellationToken token)
        {
            bool isWoditorConnected = false;

            while (true)
            {
                await Task.Delay(10, token).ConfigureAwait(false);

                bool connected = Host.Environment.IsWoditorConnected;
                if (connected == isWoditorConnected)
                    continue;

                isWoditorConnected = connected;
                if (isWoditorConnected)
                {
                    OnConnected();
                }
                else
                {
                    await OnDisconnectedAsync().ConfigureAwait(false);
                }
            }
        }

        private void OnConnected()
        {
            uint pId;
            unsafe { PInvoke.GetWindowThreadProcessId((HWND)Host.MapEditor.GetMapEditorWindowHandle(), &pId); }
            woditorPId = pId;

            StartSession();
        }

        private async Task OnDisconnectedAsync()
        {
            await StopSessionAsync();

            // 接続解除時にすべてのドロップダウンリストの横幅を元に戻す
            ResetAllDropDownListWidth();

            woditorPId = 0;
        }

        private void StartSession()
        {
            sessionCts = new CancellationTokenSource();
            updateTask = UpdateLoopAsync(sessionCts.Token);
        }

        private async Task StopSessionAsync()
        {
            if (sessionCts is null)
                return;

            sessionCts.Cancel();
            try
            {
                await updateTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            sessionCts.Dispose();
            sessionCts = null;

            knownComboBoxes.Clear();
        }

        private async Task UpdateLoopAsync(CancellationToken token)
        {
            HWND lastWindow = HWND.Null;
            const int periodicUpdateInterval = 100;
            Task periodicUpdateDelay = Task.Delay(periodicUpdateInterval, token);

            while (true)
            {
                await Task.Delay(10, token).ConfigureAwait(false);

                HWND activeWindow = PInvoke.GetForegroundWindow();

                // アクティブウィンドウが切り替わったときに即時更新
                if (activeWindow != lastWindow)
                {
                    lastWindow = activeWindow;

                    uint activePId;
                    unsafe { PInvoke.GetWindowThreadProcessId(activeWindow, &activePId); }
                    if (activePId == woditorPId)
                    {
                        UpdateAllComboBoxes(activeWindow);
                        continue;
                    }
                }

                // 定期更新
                if (periodicUpdateDelay.IsCompleted)
                {
                    uint activePId;
                    unsafe { PInvoke.GetWindowThreadProcessId(activeWindow, &activePId); }
                    if (activePId == woditorPId)
                    {
                        UpdateChangedComboBoxes(activeWindow);
                    }

                    periodicUpdateDelay = Task.Delay(periodicUpdateInterval, token);
                }
            }
        }

        private void UpdateAllComboBoxes(HWND parentWindow)
        {
            knownComboBoxes.Clear();

            PInvoke.EnumChildWindows(parentWindow, (childWindow, lParam) =>
            {
                if (!IsTargetComboBox(childWindow))
                    return true;

                ComboBoxState state = GetComboBoxState(childWindow);
                knownComboBoxes[childWindow] = state;

                FitDropDownListWidth(childWindow);
                return true;
            }, 0);
        }

        private void UpdateChangedComboBoxes(HWND parentWindow)
        {
            PInvoke.EnumChildWindows(parentWindow, (childWindow, lParam) =>
            {
                if (!IsTargetComboBox(childWindow))
                    return true;

                // コンボボックスの状態（項目数と最初のテキスト）が前回と同じ場合はスキップする
                ComboBoxState state = GetComboBoxState(childWindow);
                if (knownComboBoxes.TryGetValue(childWindow, out ComboBoxState lastState))
                {
                    if (state.ItemCount == lastState.ItemCount && state.FirstText == lastState.FirstText)
                    {
                        return true;
                    }
                }
                knownComboBoxes[childWindow] = state;

                FitDropDownListWidth(childWindow);
                return true;
            }, 0);
        }

        private bool IsTargetComboBox(HWND window)
        {
            if (!PInvoke.IsWindowVisible(window) || !PInvoke.IsWindowEnabled(window))
                return false;

            return GetClassName(window) == "ComboBox";
        }

        private unsafe string GetClassName(HWND window)
        {
            const int classNameLength = 256;
            fixed (char* classNameChars = new char[classNameLength])
            {
                PInvoke.GetClassName(window, classNameChars, classNameLength);
                return new string(classNameChars);
            }
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

        private unsafe void FitDropDownListWidth(HWND comboBox)
        {
#if DEBUG
            var stopwatch = Stopwatch.StartNew();
#endif
            int itemCount = (int)(nint)PInvoke.SendMessage(comboBox, PInvoke.CB_GETCOUNT, 0, 0);
            if (itemCount == PInvoke.CB_ERR)
            {
                Debug.WriteLine("CB_GETCOUNTが失敗");
                return;
            }

            // 項目がない場合はスキップ
            if (itemCount == 0)
                return;

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

            var textLengths = new int[itemCount];
            for (int i = 0; i < itemCount; i++)
            {
                int textLength = (int)(nint)PInvoke.SendMessage(comboBox, PInvoke.CB_GETLBTEXTLEN, (nuint)i, 0);
                if (textLength == PInvoke.CB_ERR)
                {
                    Debug.WriteLine("CB_GETLBTEXTLENが失敗");
                    break;
                }
                textLengths[i] = textLength;
            }

            // 長さは文字単位だが2バイト文字だと幅がほぼ倍になるため、最も長いテキストの半分より長いテキストのサイズをチェックする
            int lengthLimit = textLengths.Max() / 2;
            for (int i = 0; i < itemCount; i++)
            {
                int length = textLengths[i];
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

            int width = longestTextSize.Width + longestTextSize.Height; // 余分に1文字分の幅を追加する

            // スクロールバーがついている場合は、その分の横幅を足す
            COMBOBOXINFO comboBoxInfo = new() { cbSize = (uint)sizeof(COMBOBOXINFO) };
            PInvoke.GetComboBoxInfo(comboBox, ref comboBoxInfo);
            int style = PInvoke.GetWindowLong(comboBoxInfo.hwndList, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            if ((style & (int)WINDOW_STYLE.WS_VSCROLL) != 0)
            {
                width += PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVSCROLL);
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

        private void ResetAllDropDownListWidth()
        {
            PInvoke.EnumWindows((window, lParam) =>
            {
                uint pId;
                unsafe { PInvoke.GetWindowThreadProcessId(window, &pId); }
                if (pId == woditorPId)
                {
                    PInvoke.EnumChildWindows(window, (childWindow, lParam2) =>
                    {
                        if (GetClassName(childWindow) == "ComboBox")
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
