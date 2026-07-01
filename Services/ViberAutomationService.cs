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

        /// <summary>
        /// Chụp ảnh màn hình cửa sổ Viber theo tọa độ chuẩn an toàn của Windows
        /// </summary>
        public static Bitmap? CaptureWindow(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect)) return null;

                // Sử dụng trực tiếp tọa độ gốc trả về (không nhân scale để tránh bị nhân đôi DPI trên một số máy)
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                int left = rect.Left;
                int top = rect.Top;

                if (width <= 0 || height <= 0) return null;

                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
                System.Threading.Thread.Sleep(150);

                Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (Graphics gfx = Graphics.FromImage(bmp))
                {
                    gfx.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                }

                // Lưu ảnh debug kiểm tra trực quan
                try
                {
                    string debugPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_capture.png");
                    bmp.Save(debugPath, ImageFormat.Png);
                    System.Diagnostics.Debug.WriteLine($"Đã lưu ảnh debug Viber tại: {debugPath}");
                }
                catch { }

                return bmp;
            }
            catch (Exception ex)
            {
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

                    if (text.Contains("không biết") || 
                        text.Contains("khong biet") || 
                        text.Contains("viber out") || 
                        text.Contains("chưa có trên viber") || 
                        text.Contains("thực hiện cuộc gọi viber out"))
                    {
                        hasViberOutSign = true;
                    }

                    if (text.Contains("chặn") || 
                        text.Contains("tin nhắn trong trò chuyện") || 
                        text.Contains("lưu lại bằng viber") || 
                        text.Contains("nhập tin nhắn"))
                    {
                        hasLiveSign = true;
                    }
                }

                if (hasViberOutSign) return "Không biết (Unknown)";
                if (hasLiveSign) return "Có Viber (LIVE)";
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

                AutomationElement btnElement = null;

                AutomationElementCollection texts = viberEl.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                foreach (AutomationElement el in texts)
                {
                    string name = el.Current.Name;
                    if (name.Contains("Bắt đầu cuộc trò chuyện") || name.Contains("trò chuyện") || name.Contains(phone))
                    {
                        btnElement = el;
                        break;
                    }
                }

                if (btnElement == null)
                {
                    AutomationElementCollection listItems = viberEl.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                    foreach (AutomationElement el in listItems)
                    {
                        string name = el.Current.Name;
                        if (name.Contains("Bắt đầu") || name.Contains("trò chuyện") || name.Contains(phone))
                        {
                            btnElement = el;
                            break;
                        }
                    }
                }

                if (btnElement != null)
                {
                    System.Windows.Rect bounds = btnElement.Current.BoundingRectangle;
                    if (bounds != System.Windows.Rect.Empty)
                    {
                        int clickX = (int)(bounds.Left + (bounds.Width / 2));
                        int clickY = (int)(bounds.Top + (bounds.Height / 2));

                        SetCursorPos(clickX, clickY);
                        System.Threading.Thread.Sleep(50);
                        const uint MOUSEEVENTF_LEFTDOWN = 0x02;
                        const uint MOUSEEVENTF_LEFTUP = 0x04;
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                        System.Threading.Thread.Sleep(30);
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);

                        System.Threading.Thread.Sleep(100);
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                        System.Threading.Thread.Sleep(30);
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);

                        System.Diagnostics.Debug.WriteLine($"Đã click nút Bắt đầu cuộc trò chuyện thành công qua DOM tại X={clickX}, Y={clickY}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi tìm DOM: " + ex.Message);
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
