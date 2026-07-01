using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace ViberManager.Models
{
    public class ViberProfile : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string Name { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Status { get; set; } = "Đang đóng"; // "Đang đóng", "Đang mở", "Đang nhúng"
        public string DataDirPath { get; set; } = string.Empty;

        /// <summary>Thời gian mở/sử dụng gần nhất (được lưu vào file).</summary>
        public DateTime? LastUpdated { get; set; } = null;

        [JsonIgnore]
        public string LastUpdatedDisplay =>
            LastUpdated.HasValue
                ? LastUpdated.Value.ToString("dd/MM HH:mm")
                : "—";

        // STT hiển thị trong DataGrid (được tính theo thứ tự lọc hiện tại)
        private int _displayIndex;
        [JsonIgnore]
        public int DisplayIndex
        {
            get => _displayIndex;
            set { if (_displayIndex != value) { _displayIndex = value; Notify(nameof(DisplayIndex)); } }
        }

        private bool _isSelected;
        [JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; Notify(nameof(IsSelected)); } }
        }

        [JsonIgnore]
        public int ProcessId { get; set; } = 0;

        [JsonIgnore]
        public IntPtr WindowHandle { get; set; } = IntPtr.Zero;

        [JsonIgnore]
        public object? AttachedHost { get; set; } = null; // Lưu trữ thực thể ViberHost của riêng profile này

        [JsonIgnore]
        public string DisplayName => string.IsNullOrEmpty(Phone) ? Name : $"{Name} ({Phone})";
    }
}
