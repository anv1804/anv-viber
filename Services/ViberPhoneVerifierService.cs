using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace ViberManager.Services
{
    public static class ViberPhoneVerifierService
    {
        /// <summary>
        /// Thực hiện gõ số điện thoại, click mở cuộc trò chuyện mới và phân loại LIVE/Unknown dựa trên OCR, DOM và Quét Màu Sắc Vùng Trung Tâm
        /// </summary>
        public static async Task<string> VerifySinglePhoneAsync(IntPtr hwnd, string phone)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return "Không thể kết nối Viber";

                // Đưa Viber lên Foreground và thiết lập focus bàn phím
                ViberAutomationService.SetForegroundWindow(hwnd);
                ViberAutomationService.SetFocus(hwnd);
                await Task.Delay(100);

                // 1. Focus ô tìm kiếm bằng Ctrl + Shift + F
                ViberAutomationService.SendCtrlShiftF(hwnd);
                await Task.Delay(150);

                // 2. Xóa sạch ô nhập cũ bằng Ctrl + A rồi nhấn Backspace
                ViberAutomationService.SendPhysicalKey(0x11); // Ctrl
                await Task.Delay(30);
                ViberAutomationService.SendPhysicalKey(0x41); // A
                await Task.Delay(50);
                ViberAutomationService.SendPhysicalKey(0x08); // Backspace
                await Task.Delay(150);

                // 3. Gõ số điện thoại mới
                ViberAutomationService.SendText(hwnd, phone);
                await Task.Delay(1200); // Chờ Viber hiển thị danh sách kết quả

                // 4. Click chính xác vào nút Bắt đầu cuộc trò chuyện thông qua tọa độ DOM thực
                bool clickedDom = ViberAutomationService.AutomationClickStartChatButton(hwnd, phone);
                if (!clickedDom)
                {
                    // Fallback click tọa độ cố định
                    ViberAutomationService.ClickPhysical(hwnd, 150, 195);
                    await Task.Delay(150);
                    ViberAutomationService.ClickPhysical(hwnd, 150, 195);
                }
                
                // 5. Đợi 2.5 giây để đảm bảo Viber tải và render hoàn chỉnh trang chat mới
                await Task.Delay(2500);

                // ----------------------------------------------------
                // LỚP 1: KIỂM TRA DOM ĐỂ TÌM Ô NHẬP TIN NHẮN VÀ NÚT VIBER OUT GIỮA (ĐỘ CHÍNH XÁC 100% THEO LOGIC KHÁCH HÀNG)
                // ----------------------------------------------------
                try
                {
                    AutomationElement viberEl = AutomationElement.FromHandle(hwnd);
                    if (viberEl != null && ViberAutomationService.GetWindowRect(hwnd, out ViberAutomationService.RECT rect))
                    {
                        // 1. Kiểm tra sự tồn tại của ô nhập tin nhắn "Type a message..." ở dưới cùng bên phải
                        bool hasMessageInput = false;
                        AutomationElementCollection editFields = viberEl.FindAll(TreeScope.Descendants, 
                            new OrCondition(
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)
                            ));
                        
                        foreach (AutomationElement edit in editFields)
                        {
                            try
                            {
                                var bound = edit.Current.BoundingRectangle;
                                // Ô nhập tin nhắn phải nằm ở bên phải danh sách chat (X > rect.Left + 320) và ở nửa dưới cửa sổ (Y > rect.Top + 200)
                                if (bound.Left > rect.Left + 320 && bound.Top > rect.Top + 200 && bound.Height > 0)
                                {
                                    hasMessageInput = true;
                                    break;
                                }
                            }
                            catch { }
                        }

                        // 2. Kiểm tra xem có nút Viber Out ở khu vực giữa/dưới chat pane không
                        bool hasCenterViberOutButton = false;
                        AutomationElementCollection buttons = viberEl.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                        foreach (AutomationElement btn in buttons)
                        {
                            try
                            {
                                string btnName = btn.Current.Name.ToLower();
                                if (btnName.Contains("viber out"))
                                {
                                    var bound = btn.Current.BoundingRectangle;
                                    // Nút Viber Out ở góc trên cùng bên phải nằm ở tọa độ Y rất cao (gần rect.Top), nút ở giữa chat pane sẽ ở thấp hơn (Top > rect.Top + 120)
                                    if (bound.Top > rect.Top + 120 && bound.Height > 0)
                                    {
                                        hasCenterViberOutButton = true;
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }

                        System.Diagnostics.Debug.WriteLine($"[DOM ANALYSIS SĐT {phone}] Có ô nhập tin nhắn (Right Pane) = {hasMessageInput}, Có nút Viber Out giữa = {hasCenterViberOutButton}");

                        // SỰ KHÁC BIỆT CHÍNH XÁC: 
                        // - Tài khoản Unknown: KHÔNG có ô nhập tin nhắn HOẶC CÓ nút Viber Out ở giữa màn hình chat.
                        // - Tài khoản Live: CÓ ô nhập tin nhắn VÀ KHÔNG CÓ nút Viber Out ở giữa.
                        if (hasCenterViberOutButton || !hasMessageInput)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DOM SĐT {phone}] Kết luận: Not LIVE");
                            return "Not LIVE";
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[DOM SĐT {phone}] Kết luận: LIVE");
                            return "LIVE";
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Lỗi phân tích DOM check SĐT {phone}: {ex.Message}");
                }

                // ----------------------------------------------------
                // LỚP 2: QUÉT MÀU TÍM VÀ OCR (DỰ PHÒNG KHI DOM GẶP SỰ CỐ)
                // ----------------------------------------------------
                using (Bitmap? bmp = ViberAutomationService.CaptureWindow(hwnd))
                {
                    if (bmp != null)
                    {
                        // Quét dải pixel màu tím (Giới hạn tối đa Y ở 82% để tránh nút microphone màu tím ở góc dưới cùng bên phải)
                        int startX = (int)(bmp.Width * 0.40);
                        int endX = (int)(bmp.Width * 0.85);
                        int startY = (int)(bmp.Height * 0.20);
                        int endY = (int)(bmp.Height * 0.82);

                        bool foundPurpleButton = false;
                        for (int x = startX; x < endX; x += 2)
                        {
                            for (int y = startY; y < endY; y += 2)
                            {
                                Color pixel = bmp.GetPixel(x, y);
                                if (pixel.R >= 85 && pixel.R <= 145 &&
                                    pixel.G >= 65 && pixel.G <= 125 &&
                                    pixel.B >= 200 && pixel.B <= 255)
                                {
                                    foundPurpleButton = true;
                                    break;
                                }
                            }
                            if (foundPurpleButton) break;
                        }

                        if (foundPurpleButton)
                        {
                            System.Diagnostics.Debug.WriteLine($"[COLOR FALLBACK SĐT {phone}] Phát hiện màu tím Viber Out. Kết luận: Not LIVE");
                            return "Not LIVE";
                        }

                        // Quét OCR dự phòng
                        string text = await ViberOcrService.PerformOcrAsync(bmp);
                        text = text.ToLower();

                        if (text.Contains("unknown") || text.Contains("yet") || text.Contains("invite") ||
                            text.Contains("make") || text.Contains("out") || text.Contains("chưa") || text.Contains("gọi"))
                        {
                            System.Diagnostics.Debug.WriteLine($"[OCR FALLBACK SĐT {phone}] Khớp từ khóa Not LIVE. Kết luận: Not LIVE");
                            return "Not LIVE";
                        }
                    }
                }

                // Mặc định an toàn cho trường hợp còn lại
                return "LIVE";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi Verify SĐT {phone}: {ex.Message}");
            }

            return "Not LIVE";
        }

        public static async Task OpenChatWithPhoneAsync(IntPtr hwnd, string phone)
        {
            if (hwnd == IntPtr.Zero) return;

            ViberAutomationService.SetForegroundWindow(hwnd);
            ViberAutomationService.SetFocus(hwnd);
            await Task.Delay(100);

            ViberAutomationService.SendCtrlShiftF(hwnd);
            await Task.Delay(150);

            ViberAutomationService.SendPhysicalKey(0x11); // Ctrl
            await Task.Delay(30);
            ViberAutomationService.SendPhysicalKey(0x41); // A
            await Task.Delay(50);
            ViberAutomationService.SendPhysicalKey(0x08); // Backspace
            await Task.Delay(150);

            ViberAutomationService.SendText(hwnd, phone);
            await Task.Delay(1200);

            bool clickedDom = ViberAutomationService.AutomationClickStartChatButton(hwnd, phone);
            if (!clickedDom)
            {
                ViberAutomationService.ClickPhysical(hwnd, 150, 195);
                await Task.Delay(150);
                ViberAutomationService.ClickPhysical(hwnd, 150, 195);
            }
        }
    }
}
