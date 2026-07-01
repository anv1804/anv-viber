using System;

namespace ViberManager.Services
{
    public class WindowManager
    {
        /// <summary>
        /// Tìm handle cửa sổ chính của tiến trình dựa trên ProcessID.
        /// </summary>
        public static IntPtr FindWindowByProcessId(int processId)
        {
            return Win32Helper.FindWindowByProcessId(processId);
        }

        /// <summary>
        /// Nhúng cửa sổ con vào cửa sổ cha và tùy chỉnh style không viền.
        /// </summary>
        public static void EmbedWindow(IntPtr childHandle, IntPtr parentHandle, int width, int height)
        {
            if (childHandle == IntPtr.Zero || parentHandle == IntPtr.Zero) return;

            // Đặt làm cửa sổ con
            Win32Helper.SetParent(childHandle, parentHandle);

            // Bỏ border, caption, menu
            int style = Win32Helper.GetWindowLong(childHandle, Win32Helper.GWL_STYLE);
            style &= ~Win32Helper.WS_CAPTION;
            style &= ~Win32Helper.WS_THICKFRAME;
            style &= ~Win32Helper.WS_SYSMENU;
            style |= Win32Helper.WS_CHILD;
            Win32Helper.SetWindowLong(childHandle, Win32Helper.GWL_STYLE, style);

            // Cập nhật vị trí
            ResizeWindow(childHandle, width, height);
        }

        /// <summary>
        /// Thay đổi kích thước cửa sổ nhúng.
        /// </summary>
        public static void ResizeWindow(IntPtr handle, int width, int height)
        {
            if (handle != IntPtr.Zero)
            {
                Win32Helper.SetWindowPos(handle, IntPtr.Zero, 0, 0, width, height, Win32Helper.SWP_NOZORDER | Win32Helper.SWP_SHOWWINDOW);
            }
        }

        /// <summary>
        /// Focus bàn phím vào cửa sổ chỉ định.
        /// </summary>
        public static void FocusWindow(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                Win32Helper.SetFocus(handle);
            }
        }
    }
}
