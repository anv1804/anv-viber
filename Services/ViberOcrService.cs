using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Globalization;

namespace ViberManager.Services
{
    public class ViberOcrService
    {
        /// <summary>
        /// Nhận diện chữ viết từ đối tượng Bitmap bằng Windows OCR Engine (Hỗ trợ tiếng Việt)
        /// </summary>
        public static async Task<string> PerformOcrAsync(Bitmap bitmap)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    ms.Position = 0;

                    var randomAccessStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    using (var writer = new BinaryWriter(randomAccessStream.AsStreamForWrite()))
                    {
                        writer.Write(ms.ToArray());
                        await randomAccessStream.FlushAsync();
                    }

                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                    using (SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync())
                    {
                        Language lang = new Language("vi-VN");
                        if (!OcrEngine.IsLanguageSupported(lang))
                        {
                            lang = OcrEngine.AvailableRecognizerLanguages[0] ?? new Language("en-US");
                        }

                        OcrEngine ocrEngine = OcrEngine.TryCreateFromLanguage(lang);
                        if (ocrEngine == null) return string.Empty;

                        OcrResult result = await ocrEngine.RecognizeAsync(softwareBitmap);
                        return result.Text ?? string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi OCR: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Tìm tọa độ Y của một từ khóa (ví dụ: "unblock" hoặc "support") nằm ở cột bên trái (X < 280)
        /// </summary>
        public static async Task<int> FindTextLocationAsync(Bitmap bitmap, string keyword)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    ms.Position = 0;

                    var randomAccessStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    using (var writer = new BinaryWriter(randomAccessStream.AsStreamForWrite()))
                    {
                        writer.Write(ms.ToArray());
                        await randomAccessStream.FlushAsync();
                    }

                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
                    using (SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync())
                    {
                        Language lang = new Language("en-US");
                        if (!OcrEngine.IsLanguageSupported(lang))
                        {
                            lang = OcrEngine.AvailableRecognizerLanguages[0] ?? new Language("vi-VN");
                        }

                        OcrEngine ocrEngine = OcrEngine.TryCreateFromLanguage(lang);
                        if (ocrEngine == null) return -1;

                        OcrResult result = await ocrEngine.RecognizeAsync(softwareBitmap);
                        
                        foreach (var line in result.Lines)
                        {
                            string lineText = line.Text.ToLower();
                            if (lineText.Contains(keyword.ToLower()))
                            {
                                foreach (var word in line.Words)
                                {
                                    if (word.Text.ToLower().Contains(keyword.ToLower()))
                                    {
                                        if (word.BoundingRect.Left < 280)
                                        {
                                            return (int)(word.BoundingRect.Top + (word.BoundingRect.Height / 2));
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Lỗi tìm tọa độ OCR: {ex.Message}");
            }
            return -1;
        }
    }
}
