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

                await Task.Delay(1200); // Chờ Viber hiển thị danh sách kết quả

                // 4. Click chính xác vào nút Bắt đầu cuộc trò chuyện thông qua tọa độ DOM thực (Không di chuột)
                bool clickedDom = ViberAutomationService.AutomationClickStartChatButton(hwnd, phone);
                if (!clickedDom)
                {
                    // Click tương đối tọa độ cố định bằng PostMessage (Không di chuyển chuột thật)
                    ViberAutomationService.ClickRelative(hwnd, 150, 195);
                    await Task.Delay(150);
                    ViberAutomationService.ClickRelative(hwnd, 150, 195);
                }
                
                // 5. Đợi 1.5 giây để đảm bảo Viber tải và render hoàn chỉnh trang chat mới.
                // Gọi ForceRealignment liên tục mỗi 150ms để giật khít khung hình ngay lập tức khi Qt đổi layout.
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(150);
                    ViberHost.ForceRealignment(hwnd);
                }

                // ----------------------------------------------------
                // LỚP 1: KIỂM TRA DOM ĐỂ TÌM Ô NHẬP TIN NHẮN VÀ NÚT VIBER OUT GIỮA (ĐỘ CHÍNH XÁC 100% THEO LOGIC KHÁCH HÀNG)
                // ----------------------------------------------------
                try
                {
                    AutomationElement viberEl = AutomationElement.FromHandle(hwnd);
                    if (viberEl != null && ViberAutomationService.GetWindowRect(hwnd, out ViberAutomationService.RECT rect))
                    {
                        int winWidth  = rect.Right  - rect.Left;
                        int winHeight = rect.Bottom - rect.Top;

                        // 1. Kiểm tra ô nhập tin nhắn ở pane phải (X > 320, Y > 200)
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
                                if (bound.Left > rect.Left + 320 && bound.Top > rect.Top + 200 && bound.Height > 0)
                                {
                                    hasMessageInput = true;
                                    break;
                                }
                            }
                            catch { }
                        }

                        // 2. Kiểm tra nút Viber Out trong card giữa màn hình (phân biệt với nút header ở góc trên phải)
                        //    - Nút header: nhỏ, nằm ở Y gần rect.Top (< rect.Top + 80)
                        //    - Nút card giữa: lớn, nằm ở Y > rect.Top + 200, nằm ở X giữa cửa sổ
                        bool hasCenterViberOutButton = false;
                        int centerXMin = rect.Left + (int)(winWidth * 0.35);
                        int centerXMax = rect.Left + (int)(winWidth * 0.85);
                        AutomationElementCollection buttons = viberEl.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                        foreach (AutomationElement btn in buttons)
                        {
                            try
                            {
                                string btnName = btn.Current.Name.ToLower();
                                if (btnName.Contains("viber out"))
                                {
                                    var bound = btn.Current.BoundingRectangle;
                                    // Nút card giữa: nằm trong vùng trung tâm và đủ xa header
                                    if (bound.Top > rect.Top + 200 && bound.Left > centerXMin && bound.Left < centerXMax && bound.Height > 0)
                                    {
                                        hasCenterViberOutButton = true;
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }

                        // 3. Kiểm tra thêm: có text "Không biết" / "unknown" trong chat pane không?
                        //    Đây là text tiêu đề hiển thị khi số chưa có tài khoản Viber.
                        bool hasUnknownText = false;
                        AutomationElementCollection textEls = viberEl.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                        foreach (AutomationElement txt in textEls)
                        {
                            try
                            {
                                string name = txt.Current.Name.ToLower();
                                var bound = txt.Current.BoundingRectangle;
                                // Chỉ xét text trong pane phải (X > 320) để tránh nhầm sidebar
                                if (bound.Left > rect.Left + 320 && bound.Top > rect.Top + 80 &&
                                    (name.Contains("không biết") || name.Contains("unknown") ||
                                     name.Contains("viber out") || name.Contains("chưa có")))
                                {
                                    hasUnknownText = true;
                                    break;
                                }
                            }
                            catch { }
                        }

                        System.Diagnostics.Debug.WriteLine($"[DOM ANALYSIS SĐT {phone}] hasMessageInput={hasMessageInput}, hasCenterViberOutButton={hasCenterViberOutButton}, hasUnknownText={hasUnknownText}");

                        // Kết luận:
                        // Not LIVE nếu: có nút Viber Out giữa màn hình, HOẶC có text "Không biết/Unknown", HOẶC không có ô nhập tin nhắn
                        if (hasCenterViberOutButton || hasUnknownText || !hasMessageInput)
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
