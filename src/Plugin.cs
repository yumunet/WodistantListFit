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

        public override string Version => "1.0.0";

        public override string Description => "ドロップダウンリストの横幅を自動的に調整します。";

        public override MenuBarInfo ManuBarInfomation { get { return null; } }

        public override IKeyboardAction[] KeyboardActions => new IKeyboardAction[] { };

        private HWND oldActiveWindowHandle;
        private readonly Dictionary<HWND, ComboBoxMemory> knownComboBoxes = new();

        public override void OnInitializePlugin()
        {
            Task.Run(() => Observer());
        }

        private unsafe void Observer()
        {
            while (true)
            {
                if (Host.Environment.IsWoditorConnected)
                {
                    HWND activeWindowHandle = PInvoke.GetForegroundWindow();
                    uint activePId;
                    PInvoke.GetWindowThreadProcessId(activeWindowHandle, &activePId);

                    uint woditorPId;
                    PInvoke.GetWindowThreadProcessId((HWND)Host.MapEditor.GetMapEditorWindowHandle(), &woditorPId);

                    if (activePId == woditorPId)
                    {
                        // アクティブウィンドウが変わったときに、記憶しているコンボボックスすべての存在をチェック
                        if (activeWindowHandle != oldActiveWindowHandle)
                        {
                            oldActiveWindowHandle = activeWindowHandle;
                            foreach (var item in new Dictionary<HWND, ComboBoxMemory>(knownComboBoxes))
                            {
                                if (!PInvoke.IsWindow(item.Key))
                                {
                                    knownComboBoxes.Remove(item.Key);
                                }
                            }
                        }

                        PInvoke.EnumChildWindows(activeWindowHandle, (HWND childWindowHandle, LPARAM lParam) =>
                        {
                            if (PInvoke.IsWindowVisible(childWindowHandle) && PInvoke.IsWindowEnabled(childWindowHandle))
                            {
                                string className;
                                const int classNameLength = 256;
                                fixed (char* classNameChars = new char[classNameLength])
                                {
                                    PInvoke.GetClassName(childWindowHandle, classNameChars, classNameLength);
                                    className = new string(classNameChars);
                                }
                                if (className == "ComboBox")
                                {
                                    FitDropDownListWidth(childWindowHandle);
                                }
                            }
                            return true;
                        }, 0);
                    }
                }
                Thread.Sleep(100);
            }
        }

        private unsafe void FitDropDownListWidth(HWND comboBoxHandle)
        {
#if DEBUG
            var stopwatch = new Stopwatch();
            stopwatch.Start();
#endif
            var itemCount = (int)PInvoke.SendMessage(comboBoxHandle, PInvoke.CB_GETCOUNT, 0, 0);
            if (itemCount == PInvoke.CB_ERR)
            {
                Debug.WriteLine("CB_GETCOUNTが失敗");
                return;
            }

            // 項目数と最初のテキストの長さが前回と同じ場合は中断する
            int firstTextLength = 0;
            if (knownComboBoxes.TryGetValue(comboBoxHandle, out ComboBoxMemory comboBox))
            {
                if (itemCount == comboBox.itemCount)
                {
                    firstTextLength = (int)PInvoke.SendMessage(comboBoxHandle, PInvoke.CB_GETLBTEXTLEN, 0, 0);
                    if (firstTextLength == comboBox.firstTextLength)
                        return;
                }
            }
            knownComboBoxes[comboBoxHandle] = new ComboBoxMemory() { itemCount = itemCount, firstTextLength = firstTextLength };

            int width;
            if (itemCount == 0)
            {
                // 項目がない場合は0pxにする（コンボボックスの幅と同じになる）
                width = 0;
            }
            else
            {
                SIZE longestTextSize = new(0, 0);
                var fontHandle = (HFONT)(nint)PInvoke.SendMessage(comboBoxHandle, PInvoke.WM_GETFONT, 0, 0);
                // 外部プロセスのフォントハンドルはそのまま使えないので作り直す
                LOGFONTW logFont;
                PInvoke.GetObject(fontHandle, sizeof(LOGFONTW), &logFont);
                using var newFontHandle = PInvoke.CreateFontIndirect(logFont);
                {
                    HDC hDC = PInvoke.CreateCompatibleDC((HDC)(void*)0);
                    HBITMAP bitmapHandle = PInvoke.CreateCompatibleBitmap(hDC, 1, 1);
                    var originalBitmapHandle = PInvoke.SelectObject(hDC, bitmapHandle);
                    var originalFontHandle = PInvoke.SelectObject(hDC, (HFONT)newFontHandle.DangerousGetHandle());

                    var textLengthes = new int[itemCount];
                    for (int i = 0; i < itemCount; i++)
                    {
                        var textLength = (int)PInvoke.SendMessage(comboBoxHandle, PInvoke.CB_GETLBTEXTLEN, (nuint)i, 0);
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
                        int textLength = textLengthes[i];
                        if (textLength >= lengthLimit)
                        {
                            string text;
                            fixed (char* textChars = new char[textLength])
                            {
                                PInvoke.SendMessage(comboBoxHandle, PInvoke.CB_GETLBTEXT, (nuint)i, (nint)textChars);
                                text = new string(textChars);
                            }

                            PInvoke.GetTextExtentPoint32W(hDC, text, textLength, out SIZE textSize);
                            if (textSize.Width > longestTextSize.Width)
                            {
                                longestTextSize = textSize;
                            }
                        }
                    }

                    PInvoke.SelectObject(hDC, originalBitmapHandle);
                    PInvoke.SelectObject(hDC, originalFontHandle);
                    PInvoke.DeleteObject(bitmapHandle);
                    PInvoke.DeleteDC(hDC);
                }

                width = longestTextSize.Width + longestTextSize.Height; // 余分に1文字分の幅を追加する

                // スクロールバーがついている場合は、その分の横幅を足す
                COMBOBOXINFO comboBoxInfo = new() { cbSize = (uint)sizeof(COMBOBOXINFO) };
                PInvoke.GetComboBoxInfo(comboBoxHandle, ref comboBoxInfo);
                int style = PInvoke.GetWindowLong(comboBoxInfo.hwndList, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
                if ((style & (int)WINDOW_STYLE.WS_VSCROLL) != 0)
                {
                    width += PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVSCROLL);
                }
            }

            bool isShownList = (PInvoke.SendMessage(comboBoxHandle, PInvoke.CB_GETDROPPEDSTATE, 0, 0) != 0);
            PInvoke.SendMessage(comboBoxHandle, PInvoke.CB_SETDROPPEDWIDTH, (nuint)width, 0);

            // リストが開かれていた場合は、CB_SETDROPPEDWIDTHでリストが閉じられてしまうため、再度開く
            // テキストサイズの取得処理中やアクティブウィンドウを切り替えた瞬間などに、ユーザーに開かれる可能性がある
            if (isShownList)
            {
                PInvoke.SendMessage(comboBoxHandle, PInvoke.CB_SHOWDROPDOWN, 1, 0);
            }
#if DEBUG
            stopwatch.Stop();
            Debug.WriteLine($"{(int)(void*)comboBoxHandle:X8} {stopwatch.ElapsedMilliseconds}ms");
#endif
        }

        private struct ComboBoxMemory
        {
            public int itemCount;
            public int firstTextLength;
        }
    }
}
