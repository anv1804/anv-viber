using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Threading.Tasks;

namespace ViberManager
{
    public class ViberHost : HwndHost
    {
        private readonly IntPtr _childHandle;
        private IntPtr _containerHandle = IntPtr.Zero;
        private static IntPtr _purpleBrush = IntPtr.Zero;
        private static bool _isInternalResizing = false;
        private bool _isLoginScreen = false;
        private readonly string _profilePath = "";

        // Kích thước TỰ NHIÊN của cửa sổ Viber trước khi nhúng (vật lý, pixel)
        // Dùng để tính object-fit:contain cho màn login, tránh dải đen thừa
        private double _naturalViberW = 0;
        private double _naturalViberH = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateSolidBrush(uint crColor);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        // Buộc Viber reflow/repaint sau khi resize (SetWindowPos đôi khi không kích hoạt Qt repaint)
        [DllImport("user32.dll")]
        private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        // Gửi message WM_SIZE để notify Qt app về kích thước mới, buộc layout lại
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        // ShowWindow constants
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_SHOW     = 5;
        private const int SW_RESTORE  = 9;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        // Trạng thái tach/gắn
        private bool _isDetached = false;
        public bool IsDetached => _isDetached;

        // Màu nền tím nhạt chuẩn của Viber: #7C74C7
        private const uint VIBER_BG_COLORREF = 0x00C7747C;

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc lpSubclassProc, uint uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData);
        private static SubclassProc? _childSubclassDelegate;

        private static int _currentPhysicalX = 0;
        private static int _currentPhysicalY = 0;
        private static int _currentPhysicalW = 0;
        private static int _currentPhysicalH = 0;

        private static IntPtr CustomChildSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData)
        {
            const uint WM_WINDOWPOSCHANGING = 0x0046;
            const uint WM_MOVE = 0x0003;
            const int SWP_NOMOVE = 0x0002;
            const int SWP_NOSIZE = 0x0001;

            if (uMsg == WM_WINDOWPOSCHANGING)
            {
                IntPtr posPtr = lParam;
                if (posPtr != IntPtr.Zero)
                {
                    WINDOWPOS pos = Marshal.PtrToStructure<WINDOWPOS>(posPtr);
                    
                    // Nếu KHÔNG PHẢI do ViberHost chủ động gọi lệnh resize (Viber tự ý co giãn do mở cột Info)
                    if (!_isInternalResizing && _currentPhysicalW > 0 && _currentPhysicalH > 0)
                    {
                        // Cưỡng chế kích thước và vị trí của Viber phải khớp chính xác tuyệt đối với khung chứa
                        pos.x = _currentPhysicalX;
                        pos.y = _currentPhysicalY;
                        pos.cx = _currentPhysicalW;
                        pos.cy = _currentPhysicalH;

                        // Xóa cờ NOMOVE và NOSIZE để Windows và Qt nhận được thông số giới hạn này,
                        // từ đó tự động co giãn các cột bên trong cho khít màn hình thay vì đẩy tràn ra ngoài
                        pos.flags &= ~SWP_NOMOVE;
                        pos.flags &= ~SWP_NOSIZE;

                        Marshal.StructureToPtr(pos, posPtr, true);
                    }
                }
            }
            else if (uMsg == WM_MOVE)
            {
                return IntPtr.Zero;
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public int flags;
        }

        private static IntPtr CustomWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            const uint WM_ERASEBKGND = 0x0014;
            if (uMsg == WM_ERASEBKGND)
            {
                RECT rect;
                if (GetClientRect(hWnd, out rect))
                {
                    FillRect(wParam, ref rect, _purpleBrush);
                    return new IntPtr(1);
                }
            }
            return DefWindowProc(hWnd, uMsg, wParam, lParam);
        }

        private static void RegisterContainerClass()
        {
            if (_classRegistered) return;

            _wndProcDelegate = CustomWndProc;
            _purpleBrush = CreateSolidBrush(VIBER_BG_COLORREF);
            
            var wndClass = new WNDCLASSEX();
            wndClass.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
            wndClass.style = 0;
            wndClass.lpfnWndProc = Marshal.GetFunctionPointerForDelegate((Delegate)_wndProcDelegate);
            wndClass.cbClsExtra = 0;
            wndClass.cbWndExtra = 0;
            wndClass.hInstance = GetModuleHandle(null);
            wndClass.hIcon = IntPtr.Zero;
            wndClass.hCursor = IntPtr.Zero;
            wndClass.hbrBackground = _purpleBrush;
            wndClass.lpszMenuName = null;
            wndClass.lpszClassName = "ViberContainerPanel";
            wndClass.hIconSm = IntPtr.Zero;

            RegisterClassEx(ref wndClass);
            _classRegistered = true;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
        private static WndProcDelegate? _wndProcDelegate;
        private static bool _classRegistered = false;

        public ViberHost(IntPtr childHandle, string profilePath)
        {
            _childHandle = childHandle;
            _profilePath = profilePath;
            RegisterContainerClass();
            
            // Nhận diện màn hình Login: Nếu file viber.db chưa được tạo -> đang ở màn hình login
            _isLoginScreen = !IsProfileLoggedIn();
        }

        private bool IsProfileLoggedIn()
        {
            if (string.IsNullOrEmpty(_profilePath)) return false;
            try
            {
                string viberPcDir = System.IO.Path.Combine(_profilePath, "AppData", "Roaming", "ViberPC");
                if (System.IO.Directory.Exists(viberPcDir))
                {
                    // Chỉ quét các thư mục con trực tiếp của ViberPC để tránh quét toàn bộ đệ quy gây lỗi UnauthorizedAccess
                    foreach (string dir in System.IO.Directory.GetDirectories(viberPcDir))
                    {
                        string dbPath = System.IO.Path.Combine(dir, "viber.db");
                        if (System.IO.File.Exists(dbPath))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Lỗi kiểm tra viber.db: " + ex.Message);
            }
            return false;
        }

        private double GetDpiScale()
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                if (dpi.DpiScaleY > 0) return dpi.DpiScaleY;
            }
            catch { }
            return 1.0;
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            try
            {
                const uint WS_CHILD = 0x40000000;
                const uint WS_VISIBLE = 0x10000000;
                const uint WS_CLIPCHILDREN = 0x02000000;

                double w = double.IsNaN(Width) || Width <= 0 ? 800 : Width;
                double h = double.IsNaN(Height) || Height <= 0 ? 600 : Height;

                double dpiScale = GetDpiScale();
                int physicalW = (int)(w * dpiScale);
                int physicalH = (int)(h * dpiScale);

                // 1. Tạo cửa sổ đệm
                _containerHandle = CreateWindowEx(
                    0,
                    "ViberContainerPanel",
                    "",
                    WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
                    0, 0, physicalW, physicalH,
                    hwndParent.Handle,
                    IntPtr.Zero,
                    GetModuleHandle(null),
                    IntPtr.Zero);

                // 2. Nhúng Viber vào cửa sổ đệm
                if (_childHandle != IntPtr.Zero && _containerHandle != IntPtr.Zero)
                {
                    const int WS_POPUP = unchecked((int)0x80000000);
                    const int WS_CAPTION = 0x00C00000;
                    const int WS_THICKFRAME = 0x00040000;
                    const int WS_SYSMENU = 0x00080000;

                    int origStyle = Win32Helper.GetWindowLong(_childHandle, Win32Helper.GWL_STYLE);

                    // === CAPTURE KÍCH THƯỚC TỰ NHIÊN CỦA VIBER TRƯỚC KHI STRIP STYLE ===
                    // Phải lấy TRƯỚC khi SetWindowLong/SetParent vì sau đó kích thước sẽ thay đổi.
                    // Đây là cơ sở để tính object-fit:contain, đảm bảo không bị dải đen hay bị cắt.
                    RECT naturalRect;
                    if (GetWindowRect(_childHandle, out naturalRect))
                    {
                        _naturalViberW = naturalRect.right - naturalRect.left;
                        _naturalViberH = naturalRect.bottom - naturalRect.top;
                    }

                    // Loại bỏ hoàn toàn Title bar và Viền để Viber tự động vẽ phẳng và KHÔNG có khoảng đen
                    int style = origStyle != 0 ? origStyle : Win32Helper.GetWindowLong(_childHandle, Win32Helper.GWL_STYLE);
                    style &= ~WS_POPUP;
                    style &= ~WS_CAPTION;
                    style &= ~WS_THICKFRAME;
                    style &= ~WS_SYSMENU;
                    style |= (int)WS_CHILD;

                    Win32Helper.SetWindowLong(_childHandle, Win32Helper.GWL_STYLE, style);
                    Win32Helper.SetParent(_childHandle, _containerHandle);

                    // Thiết lập Subclass Proc để chặn đứng hoàn toàn việc di chuyển tự phát của Qt khi nhận focus
                    _childSubclassDelegate = CustomChildSubclassProc;
                    SetWindowSubclass(_childHandle, _childSubclassDelegate, 1, IntPtr.Zero);

                    // Kiểm tra động trạng thái đăng nhập thực tế ngay từ lúc nhúng
                    _isLoginScreen = !IsProfileLoggedIn();

                    // Buộc windows cập nhật style frame để Qt không tự khôi phục vị trí cũ
                    const uint SWP_NOZORDER = 0x0004;
                    const uint SWP_NOMOVE = 0x0002;
                    const uint SWP_NOSIZE = 0x0001;
                    const uint SWP_FRAMECHANGED = 0x0020;
                    Win32Helper.SetWindowPos(_childHandle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOZORDER | SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);

                    AlignAndScale(w, h);

                    DelayedAlign(100);
                    DelayedAlign(300);
                    DelayedAlign(800);
                    DelayedAlign(1500);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi nhúng BuildWindowCore: {ex.Message}");
            }

            return new HandleRef(this, _containerHandle);
        }

        private async void DelayedAlign(int delayMs)
        {
            try
            {
                await Task.Delay(delayMs);
                if (_childHandle != IntPtr.Zero && _containerHandle != IntPtr.Zero)
                {
                    double w = ActualWidth;
                    double h = ActualHeight;

                    if (w <= 0 || h <= 0)
                    {
                        IntPtr parentHwnd = GetParent(_containerHandle);
                        if (parentHwnd != IntPtr.Zero)
                        {
                            RECT rect;
                            if (GetClientRect(parentHwnd, out rect))
                            {
                                double dpiScale = GetDpiScale();
                                w = (rect.right - rect.left) / dpiScale;
                                h = (rect.bottom - rect.top) / dpiScale;
                            }
                        }
                    }

                    if (w <= 0) w = double.IsNaN(Width) || Width <= 0 ? 800 : Width;
                    if (h <= 0) h = double.IsNaN(Height) || Height <= 0 ? 600 : Height;

                    AlignAndScale(w, h);
                }
            }
            catch { }
        }

        private void AlignAndScale(double width, double height)
        {
            if (_childHandle != IntPtr.Zero && _containerHandle != IntPtr.Zero)
            {
                const uint SWP_NOZORDER = 0x0004;
                const uint SWP_SHOWWINDOW = 0x0040;
                const uint SWP_FRAMECHANGED = 0x0020;

                double dpiScale = GetDpiScale();
                double containerW = width * dpiScale;
                double containerH = height * dpiScale;

                // Kiểm tra động xem người dùng đã quét mã đăng nhập chưa để tự động giãn tràn viền ngay lập tức
                if (_isLoginScreen)
                {
                    if (IsProfileLoggedIn())
                    {
                        _isLoginScreen = false;
                    }
                }

                double targetW, targetH;
                int x, y;

                if (_isLoginScreen)
                {
                    // === OBJECT-FIT: CONTAIN CHO MÀN HÌNH LOGIN ===
                    // Viber Qt render login UI theo kích thước TỰ NHIÊN của nó (đã capture trước).
                    // Nếu ta đặt window cao hơn natural height → phần thừa thành DẢI ĐEN.
                    // Giải pháp: scale Viber window tỷ lệ contain vào container, tương tự CSS object-fit:contain.
                    // WPF background màu tím sẽ fill các khoảng trống xung quanh.

                    // Lấy tham chiếu kích thước tự nhiên (fallback 900:580 nếu chưa capture được)
                    double refW = _naturalViberW > 0 ? _naturalViberW : 900.0;
                    double refH = _naturalViberH > 0 ? _naturalViberH : 580.0;

                    // Tính scale factor để vừa khít container (maintain aspect ratio)
                    double scaleW = containerW / refW;
                    double scaleH = containerH / refH;
                    double scale  = Math.Min(scaleW, scaleH);

                    targetW = refW * scale;
                    targetH = refH * scale;

                    // Căn giữa trong container (phần thừa = màu nền tím WPF, không đen)
                    x = (int)((containerW - targetW) / 2.0);
                    y = (int)((containerH - targetH) / 2.0);
                    if (x < 0) x = 0;
                    if (y < 0) y = 0;
                }
                else
                {
                    // Ở màn hình chat chính: Co giãn tràn viền 100%
                    x = 0;
                    y = 0;
                    targetW = containerW;
                    targetH = containerH;
                }

                // Cập nhật tọa độ vật lý cho SubclassProc
                _currentPhysicalX = x;
                _currentPhysicalY = y;
                _currentPhysicalW = (int)targetW;
                _currentPhysicalH = (int)targetH;

                _isInternalResizing = true;
                try
                {
                    // 1. Container buffer phủ kín vùng WPF
                    Win32Helper.SetWindowPos(
                        _containerHandle,
                        IntPtr.Zero,
                        0, 0,
                        (int)containerW,
                        (int)containerH,
                        SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

                    // 2. Định vị Viber bên trong container
                    // SWP_FRAMECHANGED đã tự động gửi WM_SIZE cho Viber — KHÔNG gời thêm MoveWindow
                    // hay SendMessage(WM_SIZE) vì Qt sẽ xử lý WM_SIZE nhiều lần và gọi
                    // QWidget::move() với tọa độ màn hình sai, dẫn đến window bị xô vị trí.
                    Win32Helper.SetWindowPos(
                        _childHandle,
                        IntPtr.Zero,
                        x, y,
                        (int)targetW,
                        (int)targetH,
                        SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                }
                finally
                {
                    _isInternalResizing = false;
                }
            }
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (_containerHandle != IntPtr.Zero)
            {
                DestroyWindow(_containerHandle);
                _containerHandle = IntPtr.Zero;
            }
        }

        public void Resize(double width, double height)
        {
            if (!_isDetached)
                AlignAndScale(width, height);
        }

        // ================================================================
        // KEYBOARD FOCUS: override để WPF không chặn key message của Viber
        // ================================================================
        protected override bool TranslateAcceleratorCore(ref MSG msg, ModifierKeys modifiers)
        {
            // Trả về false = để Viber tự xử lý toàn bộ phím tắt
            return false;
        }

        protected override bool HasFocusWithinCore()
        {
            IntPtr focused = GetFocus();
            if (focused == IntPtr.Zero) return false;
            return focused == _childHandle || IsChild(_childHandle, focused);
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_SETFOCUS = 0x0007;
            if (msg == WM_SETFOCUS)
            {
                // Khi Viber host nhận focus từ WPF, chuyển thẳng sang cho Viber con
                // và cưỡng chế tính toán lại layout tránh bị trượt tọa độ
                FocusViber();
                double w = ActualWidth > 0 ? ActualWidth : (double.IsNaN(Width) || Width <= 0 ? 800 : Width);
                double h = ActualHeight > 0 ? ActualHeight : (double.IsNaN(Height) || Height <= 0 ? 600 : Height);
                AlignAndScale(w, h);
            }
            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        /// <summary>Chuyển keyboard focus vào cửa sổ Viber đang được nhúng.</summary>
        public void FocusViber()
        {
            if (_childHandle != IntPtr.Zero && !_isDetached)
            {
                SetFocus(_childHandle);
                // Sau khi SetFocus, ép Viber nằm đúng tọa độ của Container đệm ngay lập tức
                double w = ActualWidth > 0 ? ActualWidth : (double.IsNaN(Width) || Width <= 0 ? 800 : Width);
                double h = ActualHeight > 0 ? ActualHeight : (double.IsNaN(Height) || Height <= 0 ? 600 : Height);
                AlignAndScale(w, h);
            }
        }

        // ================================================================
        // DETACH / REATTACH: tách hoặc gắn lại Viber vào màn chiếu
        // ================================================================

        /// <summary>
        /// Tách Viber khỏi màn chiếu, chạy độc lập như cửa sổ thường.
        /// </summary>
        public void Detach()
        {
            if (_childHandle == IntPtr.Zero || _isDetached) return;

            const int WS_POPUP      = unchecked((int)0x80000000);
            const int WS_CAPTION    = 0x00C00000;
            const int WS_THICKFRAME = 0x00040000;
            const int WS_SYSMENU    = 0x00080000;
            const int WS_CHILD      = unchecked((int)0x40000000);

            // 1. Khôi phục style cửa sổ bình thường
            int style = Win32Helper.GetWindowLong(_childHandle, Win32Helper.GWL_STYLE);
            style &= ~WS_CHILD;   // bỏ WS_CHILD
            style |= WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU;
            Win32Helper.SetWindowLong(_childHandle, Win32Helper.GWL_STYLE, style);

            // 2. Bỏ parent (thành cửa sổ top-level)
            Win32Helper.SetParent(_childHandle, IntPtr.Zero);

            // 3. Hiển thị ở giữa màn hình với kích thước hợp lý
            const uint SWP_FRAMECHANGED = 0x0020;
            const uint SWP_SHOWWINDOW   = 0x0040;
            Win32Helper.SetWindowPos(_childHandle, IntPtr.Zero,
                150, 100, 960, 640,
                SWP_FRAMECHANGED | SWP_SHOWWINDOW);

            ShowWindow(_childHandle, SW_RESTORE);
            SetForegroundWindow(_childHandle);

            _isDetached = true;
        }

        /// <summary>
        /// Gắn lại Viber vào màn chiếu sau khi đã tách ra ngoài.
        /// </summary>
        public void Reattach()
        {
            if (_childHandle == IntPtr.Zero || _containerHandle == IntPtr.Zero || !_isDetached) return;

            const int WS_POPUP      = unchecked((int)0x80000000);
            const int WS_CAPTION    = 0x00C00000;
            const int WS_THICKFRAME = 0x00040000;
            const int WS_SYSMENU    = 0x00080000;
            const int WS_CHILD      = unchecked((int)0x40000000);

            // 1. Strip title bar và viền, đưa về WS_CHILD
            int style = Win32Helper.GetWindowLong(_childHandle, Win32Helper.GWL_STYLE);
            style &= ~WS_POPUP;
            style &= ~WS_CAPTION;
            style &= ~WS_THICKFRAME;
            style &= ~WS_SYSMENU;
            style |= WS_CHILD;
            Win32Helper.SetWindowLong(_childHandle, Win32Helper.GWL_STYLE, style);

            // 2. Tái gắn vào container
            Win32Helper.SetParent(_childHandle, _containerHandle);

            _isDetached = false;

            // 3. Căn chỉnh lại
            double w = ActualWidth  > 0 ? ActualWidth  : (double.IsNaN(Width)  || Width  <= 0 ? 800 : Width);
            double h = ActualHeight > 0 ? ActualHeight : (double.IsNaN(Height) || Height <= 0 ? 600 : Height);
            AlignAndScale(w, h);

            // 4. Delayed realign để đảm bảo Qt kịp render
            DelayedAlign(150);
            DelayedAlign(500);
        }
    }
}
