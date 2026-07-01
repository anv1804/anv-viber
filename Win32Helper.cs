using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ViberManager
{
    public static class Win32Helper
    {
        public const int GWL_STYLE = -16;
        public const int WS_CAPTION = 0x00C00000;
        public const int WS_THICKFRAME = 0x00040000;
        public const int WS_SYSMENU = 0x00080000;
        public const int WS_CHILD = 0x40000000;

        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOOWNERZORDER = 0x0200;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        // Fallback for 32-bit systems
        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool SetFocus(IntPtr hWnd);

        public static IntPtr GetWindowLongPtrOr32(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return GetWindowLongPtr(hWnd, nIndex);
            else
                return new IntPtr(GetWindowLong(hWnd, nIndex));
        }

        public static IntPtr SetWindowLongPtrOr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr(hWnd, nIndex, dwNewLong);
            else
                return new IntPtr(SetWindowLong(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        public static IntPtr FindWindowByProcessId(int processId)
        {
            IntPtr foundHwnd = IntPtr.Zero;
            EnumWindows((hwnd, lParam) =>
            {
                GetWindowThreadProcessId(hwnd, out int pid);
                if (pid == processId)
                {
                    if (IsWindowVisible(hwnd))
                    {
                        StringBuilder title = new StringBuilder(256);
                        GetWindowText(hwnd, title, title.Capacity);
                        string titleStr = title.ToString();
                        
                        // Loại trừ các cửa sổ ẩn, cửa sổ không có tiêu đề, hoặc màn hình splash
                        if (!string.IsNullOrEmpty(titleStr) && 
                            !titleStr.Contains("splash", StringComparison.OrdinalIgnoreCase) && 
                            !titleStr.Equals("Viber", StringComparison.OrdinalIgnoreCase)) // Đôi khi cửa sổ chính có tiêu đề chứa SĐT hoặc "Viber (..."
                        {
                            foundHwnd = hwnd;
                            return false; 
                        }
                        else if (titleStr.Equals("Viber", StringComparison.OrdinalIgnoreCase))
                        {
                            // Dự phòng nếu chỉ có tiêu đề Viber
                            foundHwnd = hwnd;
                        }
                    }
                }
                return true; 
            }, IntPtr.Zero);
            return foundHwnd;
        }
    }
}
