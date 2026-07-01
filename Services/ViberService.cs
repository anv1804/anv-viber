using System;
using System.Diagnostics;
using System.IO;

namespace ViberManager.Services
{
    public class ViberService
    {
        public string ViberPath { get; set; } = string.Empty;

        public ViberService(string viberPath)
        {
            ViberPath = viberPath;
        }

        /// <summary>
        /// Khởi chạy tiến trình Viber được cô lập bằng cách định hướng lại APPDATA.
        /// </summary>
        public Process? StartIsolatedViber(string profileName, string dataDirPath)
        {
            if (!File.Exists(ViberPath))
            {
                throw new FileNotFoundException("Đường dẫn Viber.exe không tồn tại.");
            }

            // Tạo các thư mục dữ liệu biệt lập
            string profileDir = Path.GetFullPath(dataDirPath);
            string appDataRoaming = Path.Combine(profileDir, "AppData", "Roaming");
            string appDataLocal = Path.Combine(profileDir, "AppData", "Local");
            string appDataTemp = Path.Combine(profileDir, "AppData", "Temp");
            
            Directory.CreateDirectory(appDataRoaming);
            Directory.CreateDirectory(appDataLocal);
            Directory.CreateDirectory(appDataTemp);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = ViberPath,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            // Định hướng biến môi trường hệ thống cho Viber tiến trình con
            startInfo.EnvironmentVariables["APPDATA"] = appDataRoaming;
            startInfo.EnvironmentVariables["LOCALAPPDATA"] = appDataLocal;
            startInfo.EnvironmentVariables["USERPROFILE"] = profileDir;
            startInfo.EnvironmentVariables["TEMP"] = appDataTemp;
            startInfo.EnvironmentVariables["TMP"] = appDataTemp;

            // Tắt auto-scale của Qt, để WPF HwndHost kiểm soát hoàn toàn kích thước cửa sổ
            // KHÔNG dùng QT_SCALE_FACTOR cứng vì nó khiến Viber render nội dung ở tỷ lệ sai
            // gây khuyết góc phải/dưới khi container WPF không khớp chính xác với giá trị hardcode.
            startInfo.EnvironmentVariables["QT_AUTO_SCREEN_SCALE_FACTOR"] = "0";
            startInfo.EnvironmentVariables["QT_SCALE_FACTOR"] = "1";

            return Process.Start(startInfo);
        }

        /// <summary>
        /// Đóng tiến trình Viber theo ID tiến trình.
        /// </summary>
        public bool StopViberProcess(int processId)
        {
            if (processId == 0) return false;
            try
            {
                Process proc = Process.GetProcessById(processId);
                
                // Thử gửi lệnh đóng cửa sổ chính trước (giúp Viber tự dọn dẹp System Tray Icon)
                proc.CloseMainWindow();
                
                if (!proc.WaitForExit(1500))
                {
                    proc.Kill();
                    proc.WaitForExit(1000);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
