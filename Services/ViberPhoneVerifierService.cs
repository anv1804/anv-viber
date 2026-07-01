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

                bool inputSuccess = false;
                try
                {
                    AutomationElement viberEl = AutomationElement.FromHandle(hwnd);
                    if (viberEl != null && ViberAutomationService.GetWindowRect(hwnd, out ViberAutomationService.RECT rect))
                    {
                        AutomationElement searchEdit = null;
                        AutomationElementCollection edits = viberEl.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                        foreach (AutomationElement edit in edits)
                        {
                            try
                            {
                                var bound = edit.Current.BoundingRectangle;
                                // Ô tìm kiếm thường nằm ở bên trái (X < rect.Left + 320)
                                if (bound.Left < rect.Left + 320 && bound.Height > 0)
                                {
                                    searchEdit = edit;
                                    break;
                                }
                            }
                            catch { }
                        }

                        if (searchEdit != null)
                        {
                            // Thử đặt giá trị ô tìm kiếm bằng UIA ValuePattern (Không chiếm bàn phím/chuột)
                            if (searchEdit.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
                            {
                                ((ValuePattern)pattern).SetValue(phone);
                                inputSuccess = true;
                                System.Diagnostics.Debug.WriteLine($"[UIA SETVALUE] Đã điền số {phone} vào ô tìm kiếm.");
                            }
                            else
                            {
                                // Cách dự phòng: SetFocus ảo rồi gửi Backspace qua PostMessage và gõ text
                                searchEdit.SetFocus();
                                await Task.Delay(50);
                                for (int i = 0; i < 20; i++)
                                {
                                    ViberAutomationService.SendVirtualKey(hwnd, 0x08); // Backspace
                                }
                                await Task.Delay(50);
                                ViberAutomationService.SendText(hwnd, phone);
                                inputSuccess = true;
                                System.Diagnostics.Debug.WriteLine($"[UIA FOCUS + POSTMESSAGE] Đã điền số {phone} vào ô tìm kiếm.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Lỗi điền ô tìm kiếm qua DOM: " + ex.Message);
                }

                if (!inputSuccess)
                {
                    // Cách cuối cùng nếu DOM bị đơ: Chiếm tiêu điểm thực (Sẽ chiếm phím)
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
                }

                await Task.Delay(400); // Chờ Viber hiển thị danh sách kết quả (tối ưu hóa từ 1200ms)

                // 4. Click vào kết quả tìm kiếm để mở chat
                bool clickedDom = ViberAutomationService.AutomationClickStartChatButton(hwnd, phone);
                if (!clickedDom)
                {
                    // Fallback: click ảo ngầm (không di chuyển chuột thật)
                    if (ViberAutomationService.GetWindowRect(hwnd, out ViberAutomationService.RECT fbRect))
                    {
                        int fbW = fbRect.Right - fbRect.Left;
                        int fbH = fbRect.Bottom - fbRect.Top;
                        // Click tại ~18% chiều rộng, ~35% chiều cao → vùng kết quả tìm kiếm sidebar
                        ViberAutomationService.ClickRelative(hwnd, (int)(fbW * 0.18), (int)(fbH * 0.35));
                        await Task.Delay(100);
                        ViberAutomationService.ClickRelative(hwnd, (int)(fbW * 0.18), (int)(fbH * 0.35));
                    }
                }

                // 5. Đợi Viber render trang chat (tối ưu hóa xuống 4 bước * 150ms = 600ms)
                for (int i = 0; i < 4; i++)
                {
                    await Task.Delay(150);
                    ViberHost.ForceRealignment(hwnd);
                }

                // 5b. Kiểm tra xem chat có thực sự mở không (text-based, không phụ thuộc tọa độ)
                //     Nếu chưa mở (vẫn ở màn hình tìm kiếm / home), thử nhấn Enter để chọn kết quả đầu tiên
                string quickCheck = ViberAutomationService.AutomationCheckViberStatus(hwnd);
                bool chatOpened = quickCheck != "UNKNOWN_OCR_FALLBACK";

                if (!chatOpened)
                {
                    System.Diagnostics.Debug.WriteLine($"[VERIFY SĐT {phone}] Chat chưa mở, thử nhấn Enter...");
                    // Enter thường chọn kết quả đầu tiên trong danh sách tìm kiếm Viber
                    ViberAutomationService.SendVirtualKey(hwnd, 0x0D); // VK_RETURN
                    await Task.Delay(200);

                    // Nếu Enter cũng không được, thử click nhiều vị trí khác nhau
                    ViberAutomationService.ClickRelative(hwnd, 170, 180);
                    await Task.Delay(100);
                    ViberAutomationService.ClickRelative(hwnd, 170, 220);
                    await Task.Delay(100);

                    for (int i = 0; i < 3; i++)
                    {
                        await Task.Delay(100);
                        ViberHost.ForceRealignment(hwnd);
                    }

                    quickCheck = ViberAutomationService.AutomationCheckViberStatus(hwnd);
                    chatOpened = quickCheck != "UNKNOWN_OCR_FALLBACK";
                    System.Diagnostics.Debug.WriteLine($"[VERIFY SĐT {phone}] Sau retry: chatOpened={chatOpened}, status={quickCheck}");
                }

                // 5c. Nếu AutomationCheckViberStatus đã cho kết quả rõ ràng → dùng luôn, không cần phân tích thêm
                if (chatOpened)
                {
                    if (quickCheck == "Không biết (Unknown)")
                    {
                        System.Diagnostics.Debug.WriteLine($"[QUICK CHECK SĐT {phone}] AutomationCheckViberStatus → Not LIVE");
                        return "Not LIVE";
                    }
                    if (quickCheck == "Có Viber (LIVE)")
                    {
                        System.Diagnostics.Debug.WriteLine($"[QUICK CHECK SĐT {phone}] AutomationCheckViberStatus → LIVE");
                        return "LIVE";
                    }
                }

                // ----------------------------------------------------
                // LỚP 1: KIỂM TRA DOM - ĐẾM SỐ NÚT VIBER OUT & Ô NHẬP TIN NHẮN
                // Nguyên lý: 
                //   - Số LIVE:       1 nút Viber Out (ở header góc phải) + có ô nhập tin nhắn bên phải
                //   - Số Không biết: 2 nút Viber Out (header + card giữa) + không/có ô nhập tin nhắn
                // Cách này KHÔNG phụ thuộc vào tọa độ tuyệt đối → hoạt động đúng trên mọi resolution/DPI
                // ----------------------------------------------------
                try
                {
                    AutomationElement viberEl = AutomationElement.FromHandle(hwnd);
                    if (viberEl != null && ViberAutomationService.GetWindowRect(hwnd, out ViberAutomationService.RECT rect))
                    {
                        // 1. Đếm tất cả nút Viber Out trong cửa sổ
                        int viberOutCount = 0;
                        AutomationElementCollection buttons = viberEl.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                        foreach (AutomationElement btn in buttons)
                        {
                            try
                            {
                                string btnName = btn.Current.Name.ToLower();
                                if (btnName.Contains("viber out"))
                                    viberOutCount++;
                            }
                            catch { }
                        }

                        // 2. Kiểm tra ô nhập tin nhắn ở pane phải (X > 33% chiều rộng cửa sổ, Y > 25% chiều cao)
                        bool hasMessageInput = false;
                        int winWidth  = rect.Right  - rect.Left;
                        int winHeight = rect.Bottom - rect.Top;
                        int rightPaneX = rect.Left + (int)(winWidth  * 0.33);
                        int msgAreaY   = rect.Top  + (int)(winHeight * 0.25);

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
                                if (bound.Left > rightPaneX && bound.Top > msgAreaY && bound.Height > 0)
                                {
                                    hasMessageInput = true;
                                    break;
                                }
                            }
                            catch { }
                        }

                        System.Diagnostics.Debug.WriteLine($"[DOM ANALYSIS SĐT {phone}] viberOutCount={viberOutCount}, hasMessageInput={hasMessageInput}");

                        // Kết luận:
                        // Not LIVE nếu: có >= 2 nút Viber Out (có cả header + card) HOẶC không có ô nhập tin nhắn
                        // LIVE nếu: chỉ có <= 1 nút Viber Out (chỉ header) VÀ có ô nhập tin nhắn
                        if (viberOutCount >= 2 || !hasMessageInput)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DOM SĐT {phone}] Kết luận: Not LIVE (viberOutCount={viberOutCount}, hasInput={hasMessageInput})");
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
                ViberHost.ForceRealignment(hwnd);
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

            bool inputSuccess = false;
            try
            {
                AutomationElement viberEl = AutomationElement.FromHandle(hwnd);
                if (viberEl != null && ViberAutomationService.GetWindowRect(hwnd, out ViberAutomationService.RECT rect))
                {
                    AutomationElement searchEdit = null;
                    AutomationElementCollection edits = viberEl.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    foreach (AutomationElement edit in edits)
                    {
                        try
                        {
                            var bound = edit.Current.BoundingRectangle;
                            if (bound.Left < rect.Left + 320 && bound.Height > 0)
                            {
                                searchEdit = edit;
                                break;
                            }
                        }
                        catch { }
                    }

                    if (searchEdit != null)
                    {
                        if (searchEdit.TryGetCurrentPattern(ValuePattern.Pattern, out object pattern))
                        {
                            ((ValuePattern)pattern).SetValue(phone);
                            inputSuccess = true;
                        }
                        else
                        {
                            searchEdit.SetFocus();
                            await Task.Delay(50);
                            for (int i = 0; i < 20; i++)
                            {
                                ViberAutomationService.SendVirtualKey(hwnd, 0x08); // Backspace
                            }
                            await Task.Delay(50);
                            ViberAutomationService.SendText(hwnd, phone);
                            inputSuccess = true;
                        }
                    }
                }
            }
            catch { }

            if (!inputSuccess)
            {
                ViberAutomationService.SetForegroundWindow(hwnd);
                ViberAutomationService.SetFocus(hwnd);
                await Task.Delay(100);
                ViberAutomationService.SendCtrlShiftF(hwnd);
                await Task.Delay(150);
                ViberAutomationService.SendPhysicalKey(0x11);
                await Task.Delay(30);
                ViberAutomationService.SendPhysicalKey(0x41);
                await Task.Delay(50);
                ViberAutomationService.SendPhysicalKey(0x08);
                await Task.Delay(150);
                ViberAutomationService.SendText(hwnd, phone);
            }

            await Task.Delay(1200);

            bool clickedDom = ViberAutomationService.AutomationClickStartChatButton(hwnd, phone);
            if (!clickedDom)
            {
                ViberAutomationService.ClickRelative(hwnd, 150, 195);
                await Task.Delay(150);
                ViberAutomationService.ClickRelative(hwnd, 150, 195);
            }
            
            // Gọi ForceRealignment liên tục mỗi 150ms trong vòng 1.05 giây để đảm bảo mượt mà tuyệt đối khi chuyển trang
            for (int i = 0; i < 7; i++)
            {
                await Task.Delay(150);
                ViberHost.ForceRealignment(hwnd);
            }
        }
    }
}
