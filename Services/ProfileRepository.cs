using System;
using System.IO;
using System.Text.Json;
using ViberManager.Models;

namespace ViberManager.Services
{
    public class ProfileRepository
    {
        private readonly string _filePath;

        public ProfileRepository(string? filePath = null)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles.json");
            }
            else
            {
                _filePath = filePath;
            }
        }

        /// <summary>
        /// Tải toàn bộ cấu hình bao gồm đường dẫn Viber và danh sách profile.
        /// </summary>
        public ViberManagerConfig LoadConfig()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    var config = JsonSerializer.Deserialize<ViberManagerConfig>(json);
                    if (config != null)
                    {
                        // Đảm bảo khởi tạo danh sách nếu null
                        config.Profiles ??= new();
                        foreach (var item in config.Profiles)
                        {
                            item.Status = "Đang đóng"; // Reset trạng thái khi chạy lại app
                        }
                        return config;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi tải config: {ex.Message}");
            }

            // Nếu chưa có file hoặc lỗi, trả về đối tượng mới
            return new ViberManagerConfig();
        }

        /// <summary>
        /// Lưu toàn bộ cấu hình xuống file JSON.
        /// </summary>
        public bool SaveConfig(ViberManagerConfig config)
        {
            try
            {
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi lưu config: {ex.Message}");
                return false;
            }
        }
    }
}
