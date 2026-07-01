using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace ViberManager.Services
{
    public class ViberAutomationService
    {
        public static Action<bool>? ToggleLoadingOverlay;

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;

        private const uint MOUSEEVENTF_LEFTDOWN = 0x02;
        private const uint MOUSEEVENTF_LEFTUP = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public int SystemY;

            public POINT(int x, int y)
            {
                this.X = x;
                this.Y = y;
                this.SystemY = 0;
            }
        }

        private static bool IsValidCapture(Bitmap bmp)
        {
            try
            {
                if (bmp.Width < 50 || bmp.Height < 50) return false;
                Color c1 = bmp.GetPixel(10, 10);
                Color c2 = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
                Color c3 = bmp.GetPixel(bmp.Width - 10, bmp.Height - 10);

                if (c1.A == 0 && c2.A == 0 && c3.A == 0) return false;
                if (c1.R == 0 && c1.G == 0 && c1.B == 0 &&
                    c2.R == 0 && c2.G == 0 && c2.B == 0 &&
                    c3.R == 0 && c3.G == 0 && c3.B == 0) return false;
                if (c1.R == 255 && c1.G == 255 && c1.B == 255 &&
                    c2.R == 255 && c2.G == 255 && c2.B == 255 &&
                    c3.R == 255 && c3.G == 255 && c3.B == 255) return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static Bitmap? CaptureWindow(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect)) return null;

                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                int left = rect.Left;
                int top = rect.Top;

                if (width <= 0 || height <= 0) return null;

                // Tạm ẩn loading overlay trước khi chụp hình để tránh dính đè màu tối lên screenshot
                ToggleLoadingOverlay?.Invoke(false);
                System.Threading.Thread.Sleep(50); // Đợi 50ms cho DWM ẩn hẳn

                // 1. Thử chụp ảnh ngầm bằng PrintWindow (PW_RENDERFULLCONTENT = 2) để không chiếm màn hình
                Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                bool success = false;
                using (Graphics gfx = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = gfx.GetHdc();
                    try
                    {
                        success = PrintWindow(hwnd, hdc, 2);
                    }
                    finally
                    {
                        gfx.ReleaseHdc(hdc);
                    }
                }

                if (success && IsValidCapture(bmp))
                {
                    // Hiện lại loading overlay
                    ToggleLoadingOverlay?.Invoke(true);

                    try
                    {
                        string debugPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_capture.png");
                        bmp.Save(debugPath, ImageFormat.Png);
                    }
                    catch { }
                    return bmp;
                }

                bmp.Dispose();

                // 2. Fallback cuối cùng nếu chụp ngầm lỗi (chụp đè màn hình)
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                System.Threading.Thread.Sleep(100);

                Bitmap fallbackBmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (Graphics gfx = Graphics.FromImage(fallbackBmp))
                {
                    gfx.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                }

                // Hiện lại loading overlay
                ToggleLoadingOverlay?.Invoke(true);

                try
                {
                    string debugPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_capture.png");
                    fallbackBmp.Save(debugPath, ImageFormat.Png);
                }
                catch { }

                return fallbackBmp;
            }
            catch (Exception ex)
            {
                // Đảm bảo luôn bật lại overlay nếu xảy ra lỗi
                ToggleLoadingOverlay?.Invoke(true);
                System.Diagnostics.Debug.WriteLine("Lỗi CaptureWindow: " + ex.Message);
                return null;
            }
        }

        public static void ClickRelative(IntPtr hwnd, int relX, int relY)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                IntPtr lParam = (IntPtr)((relY << 16) | (relX & 0xFFFF));
                PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)1, lParam);
                System.Threading.Thread.Sleep(50);
                PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
            }
            catch { }
        }

        public static void ClickSystemRelative(IntPtr hwnd, int relX, int relY)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return;
                SetForegroundWindow(hwnd);
                System.Threading.Thread.Sleep(100);

                POINT pt = new POINT(relX, relY);
                if (ClientToScreen(hwnd, ref pt))
                {
                    SetCursorPos(pt.X, pt.Y);
                    System.Threading.Thread.Sleep(50);
                    mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                }
            }
            catch { }
        }

        public static void SendVirtualKey(IntPtr hwnd, byte vkCode)
        {
            try
            {
                const uint WM_KEYDOWN = 0x0100;
                const uint WM_KEYUP = 0x0101;
                PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vkCode, IntPtr.Zero);
                System.Threading.Thread.Sleep(30);
                PostMessage(hwnd, WM_KEYUP, (IntPtr)vkCode, IntPtr.Zero);
            }
            catch { }
        }

        public static void SendText(IntPtr hwnd, string text)
        {
            try
            {
                const uint WM_CHAR = 0x0102;
                foreach (char c in text)
                {
                    PostMessage(hwnd, WM_CHAR, (IntPtr)c, IntPtr.Zero);
                    System.Threading.Thread.Sleep(15);
                }
            }
            catch { }
        }

        public static void SendCtrlShiftF(IntPtr hwnd)
        {
            try
            {
                const byte VK_CONTROL = 0x11;
                const byte VK_SHIFT = 0x10;
                const byte VK_F = 0x46;
                const uint KEYEVENTF_KEYUP = 0x0002;

                SetForegroundWindow(hwnd);
                SetFocus(hwnd);
                System.Threading.Thread.Sleep(150);

                keybd_event(VK_CONTROL, 0, 0, 0);
                keybd_event(VK_SHIFT, 0, 0, 0);
                keybd_event(VK_F, 0, 0, 0);
                System.Threading.Thread.Sleep(50);
                keybd_event(VK_F, 0, KEYEVENTF_KEYUP, 0);
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, 0);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
            }
            catch { }
        }

        public static void ScrollDown(IntPtr hwnd, int steps)
        {
            try
            {
                const uint WM_MOUSEWHEEL = 0x020A;
                int delta = -120 * steps;
                IntPtr wParam = (IntPtr)((delta << 16) & 0xFFFF0000);
                IntPtr lParam = (IntPtr)((250 << 16) | 120);
                PostMessage(hwnd, WM_MOUSEWHEEL, wParam, lParam);
            }
            catch { }
        }

        public static void SendPhysicalKey(byte vkCode)
        {
            try
            {
                const uint KEYEVENTF_KEYUP = 0x0002;
                keybd_event(vkCode, 0, 0, 0);
                System.Threading.Thread.Sleep(50);
                keybd_event(vkCode, 0, KEYEVENTF_KEYUP, 0);
            }
            catch { }
        }

        public static void ClickPhysical(IntPtr hwnd, int clientX, int clientY)
        {
            try
            {
                SetForegroundWindow(hwnd);
                SetFocus(hwnd);
                System.Threading.Thread.Sleep(80);

                POINT pt = new POINT { X = clientX, Y = clientY };
                if (ClientToScreen(hwnd, ref pt))
                {
                    SetCursorPos(pt.X, pt.Y);
                    System.Threading.Thread.Sleep(50);

                    const uint MOUSEEVENTF_LEFTDOWN = 0x02;
                    const uint MOUSEEVENTF_LEFTUP = 0x04;
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                    System.Threading.Thread.Sleep(30);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                }
            }
            catch { }
        }

        public static string AutomationCheckViberStatus(IntPtr hwnd)
        {
            try
            {
                AutomationElement viberEl = AutomationElement.FromHandle(hwnd);
                if (viberEl == null) return "UNKNOWN_DOM_ERROR";

                AutomationElementCollection allTexts = viberEl.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                
                bool hasViberOutSign = false;
                bool hasLiveSign = false;

                foreach (AutomationElement el in allTexts)
                {
                    string text = el.Current.Name.ToLower();

                    // CHỈ khớp các cụm từ ĐỘC QUYỀN trên màn hình "Không biết" (số không có Viber)
                    // KHÔNG dùng "viber out" vì nút đó xuất hiện trên HEADER của MỌI cuộc chat (kể cả LIVE)
                    if (text.Contains("không biết") ||
                        text.Contains("khong biet") ||
                        text.Contains("chưa có trên viber") ||
                        text.Contains("thực hiện cuộc gọi viber out") ||
                        text.Contains("make a viber out call") ||
                        text.Contains("not on viber yet"))
                    {
                        hasViberOutSign = true;
                    }

                    // Các cụm từ CHỈ xuất hiện khi chat thực sự mở với số CÓ Viber
                    // "lưu lại bằng viber" xuất hiện cả ở Not-LIVE nên KHÔNG dùng
                    if (text.Contains("nhập tin nhắn") ||
                        text.Contains("type a message") ||
                        text.Contains("tin nhắn trong trò chuyện này là riêng tư") ||
                        text.Contains("messages in this chat are private"))
                    {
                        hasLiveSign = true;
                    }
                }

                // hasLiveSign có độ ưu tiên thấp hơn: nếu cả 2 đều true thì ưu tiên Unknown
                if (hasViberOutSign && !hasLiveSign) return "Không biết (Unknown)";
                if (hasLiveSign) return "Có Viber (LIVE)";
                if (hasViberOutSign) return "Không biết (Unknown)";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi UI Automation DOM quét status: " + ex.Message);
            }
            
            return "UNKNOWN_OCR_FALLBACK";
        }

        public static bool AutomationClickStartChatButton(IntPtr hwnd, string phone)
        {
            try
            {
                AutomationElement viberEl = AutomationElement.FromHandle(hwnd);
                if (viberEl == null) return false;

                // Chuẩn bị số định dạng quốc tế để tìm kiếm (+84...)
                string phoneIntl = phone.StartsWith("0") ? "+84" + phone.Substring(1) : phone;

                AutomationElement btnElement = null;

                // Lớp 1: Tìm text element khớp số điện thoại (cả định dạng 0x và +84x)
                AutomationElementCollection texts = viberEl.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                foreach (AutomationElement el in texts)
                {
                    string name = el.Current.Name;
                    if (name.Contains("Bắt đầu cuộc trò chuyện") || name.Contains("trò chuyện") ||
                        name.Contains(phone) || name.Contains(phoneIntl))
                    {
                        btnElement = el;
                        break;
                    }
                }

                // Lớp 2: Tìm ListItem khớp số điện thoại
                if (btnElement == null)
                {
                    AutomationElementCollection listItems = viberEl.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                    foreach (AutomationElement el in listItems)
                    {
                        string name = el.Current.Name;
                        if (name.Contains("Bắt đầu") || name.Contains("trò chuyện") ||
                            name.Contains(phone) || name.Contains(phoneIntl))
                        {
                            btnElement = el;
                            break;
                        }
                    }

                    // Lớp 3: Tìm BẤT KỲ ListItem nào trong sidebar trái (không cần khớp text)
                    // → Hoạt động với mọi ngôn ngữ Viber, mọi phiên bản
                    if (btnElement == null && GetWindowRect(hwnd, out RECT winRect))
                    {
                        int sidebarMaxX = winRect.Left + (winRect.Right - winRect.Left) / 3;
                        AutomationElementCollection allListItems = viberEl.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                        foreach (AutomationElement el in allListItems)
                        {
                            try
                            {
                                var bound = el.Current.BoundingRectangle;
                                // Chỉ lấy ListItem nằm trong vùng sidebar (1/3 bên trái)
                                if (bound.Left < sidebarMaxX && bound.Height > 20 && bound.Width > 50)
                                {
                                    btnElement = el;
                                    System.Diagnostics.Debug.WriteLine($"[UIA FALLBACK] Tìm thấy ListItem sidebar: '{el.Current.Name}'");
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }

                if (btnElement != null)
                {
                    // Bước 1: InvokePattern
                    try
                    {
                        if (btnElement.TryGetCurrentPattern(InvokePattern.Pattern, out object invokeP))
                        {
                            ((InvokePattern)invokeP).Invoke();
                            System.Diagnostics.Debug.WriteLine("[UIA INVOKE] OK");
                            return true;
                        }
                    }
                    catch { }

                    // Bước 2: SelectionItemPattern
                    try
                    {
                        if (btnElement.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selP))
                        {
                            ((SelectionItemPattern)selP).Select();
                            System.Diagnostics.Debug.WriteLine("[UIA SELECT] OK");
                            return true;
                        }
                    }
                    catch { }

                    // Bước 3: Click ảo ngầm bằng PostMessage (Không chiếm chuột vật lý của người dùng)
                    System.Windows.Rect bounds = btnElement.Current.BoundingRectangle;
                    if (bounds != System.Windows.Rect.Empty && GetWindowRect(hwnd, out RECT winRect))
                    {
                        int clickX = (int)(bounds.Left + bounds.Width / 2);
                        int clickY = (int)(bounds.Top + bounds.Height / 2);

                        // Đổi tọa độ màn hình sang tọa độ Client của Viber để dùng PostMessage
                        POINT screenPt = new POINT(clickX, clickY);
                        if (ScreenToClient(hwnd, ref screenPt))
                        {
                            IntPtr lParam = (IntPtr)((screenPt.Y << 16) | (screenPt.X & 0xFFFF));
                            PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)1, lParam);
                            System.Threading.Thread.Sleep(30);
                            PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
                            System.Threading.Thread.Sleep(50);
                            PostMessage(hwnd, WM_LBUTTONDOWN, (IntPtr)1, lParam);
                            System.Threading.Thread.Sleep(30);
                            PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
                            System.Diagnostics.Debug.WriteLine($"[POST MESSAGE VIRTUAL CLICK] OK");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi AutomationClickStartChatButton: " + ex.Message);
            }
            return false;
        }

        public static string AutomationCheckRightHeaderName(IntPtr hwnd)
        {
            try
            {
                AutomationElement viberEl = AutomationElement.FromHandle(hwnd);
                if (viberEl == null) return "UNKNOWN";

                AutomationElementCollection texts = viberEl.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                
                foreach (AutomationElement el in texts)
                {
                    string name = el.Current.Name;
                    if (string.IsNullOrEmpty(name)) continue;

                    string nameLower = name.ToLower().Trim();

                    if (nameLower == "không biết" || 
                        nameLower == "khong biet" || 
                        nameLower == "unknown")
                    {
                        return "Không biết (Unknown)";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi DOM check header: " + ex.Message);
            }
            return "UNKNOWN";
        }

        public static string GetChatContactName(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect)) return "Unknown";
                
                AutomationElement viberEl = AutomationElement.FromHandle(hwnd);
                if (viberEl == null) return "Unknown";

                AutomationElementCollection texts = viberEl.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                
                double leftBoundary = rect.Left + 320;
                string bestName = "";

                foreach (AutomationElement el in texts)
                {
                    try
                    {
                        var bound = el.Current.BoundingRectangle;
                        if (bound.Left > leftBoundary && bound.Top >= rect.Top && bound.Bottom <= rect.Top + 150)
                        {
                            string name = el.Current.Name.Trim();
                            if (string.IsNullOrEmpty(name)) continue;

                            if (name == "Viber Out" || name == "Chats" || name.Contains("người đăng ký") || name.Contains("subscriber") || name.Contains("thành viên"))
                                continue;

                            if (string.IsNullOrEmpty(bestName))
                            {
                                bestName = name;
                            }
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(bestName)) return bestName;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi lấy tên từ DOM: " + ex.Message);
            }
            return "Unknown";
        }
    }
}
