using System;
using System.Threading.Tasks;
using ViberManager.Models;

namespace ViberManager.Services
{
    public class ViberVerifier
    {
        /// <summary>
        /// Thực hiện giả lập kiểm tra số điện thoại Viber bất đồng bộ.
        /// </summary>
        public static async Task<VerifyResult> VerifyPhoneNumberAsync(string phone, Action<int> progressCallback)
        {
            // Giả lập tiến trình quét
            for (int i = 0; i <= 100; i += 20)
            {
                progressCallback(i);
                await Task.Delay(150);
            }

            // Quy luật giả lập: số chẵn có Viber, số lẻ không có Viber
            bool hasViber = phone.Length >= 9 && (phone.GetHashCode() % 2 == 0);

            return new VerifyResult
            {
                Phone = phone,
                Result = hasViber ? "Đã đăng ký Viber" : "Chưa đăng ký Viber",
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
        }
    }
}
