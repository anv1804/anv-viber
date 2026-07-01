using System;

namespace ViberManager.Models
{
    public class VerifyResult
    {
        public string Phone { get; set; } = string.Empty;
        public string Result { get; set; } = "Chưa kiểm tra";
        public string Time { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
    }
}
