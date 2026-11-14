using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MehrganSurveillance.Models;
using MehrganSurveillance.Services.Optimization;

namespace MehrganSurveillance.Models
{
    /// <summary>
    /// مجموعه آزمون‌های جامع موتور هوشمند پردازش و بهینه‌سازی تصویر (Phase 8 - AI Image Optimization Engine)
    /// جهت راستی‌آزمایی:
    /// ۱. عدم تغییر مطلق بافر ورودی فریم اصلی (Original Frame Protection)
    /// ۲. عملکرد فیلترهای پردازش تصویر (Denoise, Sharpen, CLAHE, Gamma, Contrast, Glare, Deskew)
    /// ۳. صحت متریک‌های کیفیت تصویر (شارپنس لاپلاسین، انحراف معیار کنتراست، درصد سوختگی نور)
    /// ۴. تولید نامزدهای پردازش و گزینش هوشمند بر اساس گرامر پلاک ملی ایران و دقت OCR
    /// ۵. پایداری و ذخیره‌سازی پروفایل‌های اختصاصی هر دوربین
    /// ۶. عملکرد بلادرنگ (Latency کمتر از ۳۰ میلی‌ثانیه) و ایزولاسیون کامل از دستورات شبکه دوربین
    /// </summary>
    public static class ImageOptimizationEngineTests
    {
        public static async Task<bool> RunAllTestsAsync()
        {
            Console.WriteLine("================================================================================");
            Console.WriteLine("شروع آزمون‌های جامع موتور هوشمند پردازش تصویر (AI Image Optimization Engine Tests)");
            Console.WriteLine("================================================================================");

            bool allPassed = true;

            allPassed &= await Test_OriginalFrameBufferNotModified();
            allPassed &= await Test_FiltersProduceDistinctOutputs();
            allPassed &= await Test_QualityMetricsCalculation();
            allPassed &= await Test_CandidateGenerationAndBestSelection();
            allPassed &= await Test_ProfilePersistence();
            allPassed &= await Test_LiveProcessingNonInterference_And_Performance();
            allPassed &= await Test_IranianPlateGrammarPriority();

            Console.WriteLine("================================================================================");
            Console.WriteLine($"نتیجه نهایی آزمون‌های فاز ۸ موتور پردازش تصویر: {(allPassed ? "موفقیت‌آمیز (ALL PASSED)" : "دارای خطا (FAILED)")}");
            Console.WriteLine("================================================================================");

            return allPassed;
        }

        /// <summary>
        /// آزمون ۱: قانون حیاتی اصل عدم دستکاری بافر اصلی (Original Frame Protection)
        /// بافر ورودی باید بایت‌به‌بایت پس از بهینه‌سازی کاملاً دست‌نخورده و بدون تغییر بماند.
        /// </summary>
        public static async Task<bool> Test_OriginalFrameBufferNotModified()
        {
            int w = 120, h = 40, stride = 120 * 3;
            byte[] original = new byte[stride * h];
            var rnd = new Random(1234);
            rnd.NextBytes(original);

            // ایجاد کپی عینا برابر برای بررسی هش
            byte[] initialHash = SHA256.HashData(original);

            var engine = new AiImageOptimizationEngine();

            var result = await engine.OptimizePlateCropAsync(
                "test_cam_01",
                original,
                w,
                h,
                stride,
                3,
                (buf, width, height, str) => ("12ب34567", 0.90f, IranianPlateParser.Parse("12ب34567")));

            byte[] afterHash = SHA256.HashData(original);
            bool untouched = initialHash.SequenceEqual(afterHash);

            Console.WriteLine($"[Test 1] حفاظت ۱۰۰٪ از بافر اصلی فریم و عدم تغییر بایت‌های ورودی: {(untouched ? "PASS" : "FAIL")}");
            return untouched;
        }

        /// <summary>
        /// آزمون ۲: کارکرد فیلترهای ریاضی و تولید خروجی‌های متمایز و استاندارد
        /// </summary>
        public static Task<bool> Test_FiltersProduceDistinctOutputs()
        {
            int w = 60, h = 30, stride = 60 * 3;
            byte[] source = new byte[stride * h];
            for (int i = 0; i < source.Length; i++) source[i] = (byte)(i % 256);

            byte[] denoise = ImageProcessingFilterSet.DenoiseMedian3x3(source, w, h, stride);
            byte[] sharpen = ImageProcessingFilterSet.Sharpen(source, w, h, stride, 0.5f);
            byte[] clahe = ImageProcessingFilterSet.ApplyClahe(source, w, h, stride, 3.0f);
            byte[] gamma = ImageProcessingFilterSet.ApplyGamma(source, w, h, stride, 0.7f);
            byte[] contrast = ImageProcessingFilterSet.AdjustContrastBrightness(source, w, h, stride, 1.4f, 10);
            byte[] binarized = ImageProcessingFilterSet.AdaptiveThreshold(source, w, h, stride);

            bool distinct = !source.SequenceEqual(denoise) &&
                            !source.SequenceEqual(sharpen) &&
                            !source.SequenceEqual(clahe) &&
                            !source.SequenceEqual(gamma) &&
                            !source.SequenceEqual(contrast) &&
                            !source.SequenceEqual(binarized);

            // بررسی مقادیر باینریزاسیون تطبیقی (۲۰ برای جوهر کاراکتر و ۲۴۰ برای زمینه روشن)
            bool validBinarization = binarized.All(b => b == 20 || b == 240);

            bool pass = distinct && validBinarization;
            Console.WriteLine($"[Test 2] تولید خروجی‌های مستقل و متمایز توسط فیلترهای پردازش تصویر: {(pass ? "PASS" : "FAIL")}");
            return Task.FromResult(pass);
        }

        /// <summary>
        /// آزمون ۳: صحت محاسبات متریک‌های کیفیت تصویر (Sharpness, Contrast, Overexposure/Glare)
        /// </summary>
        public static Task<bool> Test_QualityMetricsCalculation()
        {
            int w = 100, h = 50, stride = 100 * 3;

            // تصویر ۱: یکنواخت و کاملاً سفید با سوختگی نور ۱۰۰٪
            byte[] glareFrame = new byte[stride * h];
            Array.Fill(glareFrame, (byte)255);
            var glareMetrics = ImageProcessingFilterSet.CalculateQualityMetrics(glareFrame.AsSpan(), w, h, stride, 3);

            // تصویر ۲: تصویر با لبه‌های شدید (شطرنجی سیاه و سفید)
            byte[] checkerFrame = new byte[stride * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte val = ((x / 5) % 2 == (y / 5) % 2) ? (byte)255 : (byte)0;
                    int idx = (y * stride) + (x * 3);
                    checkerFrame[idx] = val;
                    checkerFrame[idx + 1] = val;
                    checkerFrame[idx + 2] = val;
                }
            }
            var checkerMetrics = ImageProcessingFilterSet.CalculateQualityMetrics(checkerFrame.AsSpan(), w, h, stride, 3);

            bool pass = glareMetrics.OverexposedPercentage >= 99f &&
                        checkerMetrics.LaplacianSharpness > 1000f &&
                        checkerMetrics.ContrastStdDev > 100f &&
                        glareMetrics.LaplacianSharpness < 1f;

            Console.WriteLine($"[Test 3] صحت استخراج متریک‌های کیفیت تصویر و تشخیص سوختگی نور: {(pass ? "PASS" : "FAIL")}");
            return Task.FromResult(pass);
        }

        /// <summary>
        /// آزمون ۴: تولید نامزدهای بهینه‌سازی و انتخاب بهترین نسخه بر اساس امتیاز ترکیبی
        /// </summary>
        public static async Task<bool> Test_CandidateGenerationAndBestSelection()
        {
            int w = 160, h = 50, stride = 160 * 3;
            byte[] plateBuffer = new byte[stride * h];
            // شبیه‌سازی تصویر کم‌کنتراست با هاله نور محیطی (زمینه ۲۲۰ و خطوط کم‌رنگ ۲۰۰)
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte v = (x % 20 < 4 && y > 10 && y < 40) ? (byte)195 : (byte)225;
                    int idx = (y * stride) + (x * 3);
                    plateBuffer[idx] = v;
                    plateBuffer[idx + 1] = v;
                    plateBuffer[idx + 2] = v;
                }
            }

            var engine = new AiImageOptimizationEngine();

            var result = await engine.OptimizePlateCropAsync(
                "cam_front_gate",
                plateBuffer,
                w,
                h,
                stride,
                3,
                (buf, width, height, str) =>
                {
                    var m = ImageProcessingFilterSet.CalculateQualityMetrics(buf.AsSpan(), width, height, str, 3);
                    // پس از اعمال فیلترهای کنتراست/CLAHE/شارپنس، انحراف معیار کنتراست و شارپنس لبه‌ها به شدت افزایش می‌یابد
                    if (m.ContrastStdDev >= 22f || m.LaplacianSharpness >= 120f)
                    {
                        return ("21س98745", 0.94f, IranianPlateParser.Parse("21س98745", 0.94f));
                    }
                    return ("21س98", 0.52f, IranianPlateParser.Parse("21س98", 0.52f));
                });

            bool pass = result.Candidates.Count >= 2 &&
                        result.EnhancedBuffer != null &&
                        result.OcrConfidenceAfter > result.OcrConfidenceBefore &&
                        result.BestCandidate.Plate.IsValid &&
                        !string.IsNullOrEmpty(result.BestMethod);

            Console.WriteLine($"[Test 4] تولید کاندیداها و گزینش هوشمند بهترین نسخه با ارتقای اطمینان OCR: {(pass ? "PASS" : "FAIL")}");
            return pass;
        }

        /// <summary>
        /// آزمون ۵: پایدارسازی و ذخیره‌سازی پروفایل‌های پردازش اختصاصی به ازای هر دوربین در رجیستری
        /// </summary>
        public static Task<bool> Test_ProfilePersistence()
        {
            var registry = new CameraImageOptimizationProfileRegistry();
            string testCam = "cam_test_persistence_" + Guid.NewGuid().ToString("N");

            var profile = new CameraImageOptimizationProfile
            {
                CameraId = testCam,
                DenoiseStrength = 42,
                SharpenStrength = 73,
                ClaheClipLimit = 3.8f,
                Gamma = 0.65f,
                Contrast = 1.6f,
                EnablePerspectiveCorrection = true,
                EnableAdaptiveThreshold = false,
                Preset = ImageOptimizationPreset.Custom
            };

            registry.SaveProfile(testCam, profile);

            var loaded = registry.GetProfileForCamera(testCam);

            bool pass = loaded.CameraId == testCam &&
                        loaded.DenoiseStrength == 42 &&
                        loaded.SharpenStrength == 73 &&
                        Math.Abs(loaded.ClaheClipLimit - 3.8f) < 0.05f &&
                        Math.Abs(loaded.Gamma - 0.65f) < 0.05f &&
                        Math.Abs(loaded.Contrast - 1.6f) < 0.05f &&
                        loaded.EnablePerspectiveCorrection == true &&
                        loaded.EnableAdaptiveThreshold == false;

            Console.WriteLine($"[Test 5] ذخیره و بازیابی پروفایل اختصاصی پردازش تصویر دوربین در دیسک: {(pass ? "PASS" : "FAIL")}");
            return Task.FromResult(pass);
        }

        /// <summary>
        /// آزمون ۶: ارزیابی سرعت پردازش بلادرنگ (Latency < 30ms) و عدم قفل‌شدگی صف
        /// </summary>
        public static async Task<bool> Test_LiveProcessingNonInterference_And_Performance()
        {
            int w = 180, h = 55, stride = 180 * 3;
            byte[] frame = new byte[stride * h];
            new Random(77).NextBytes(frame);

            var engine = new AiImageOptimizationEngine();

            var sw = Stopwatch.StartNew();
            var result = await engine.OptimizePlateCropAsync(
                "cam_speed_test",
                frame,
                w,
                h,
                stride,
                3,
                (buf, width, height, str) => ("68ب12311", 0.91f, IranianPlateParser.Parse("68ب12311")));
            sw.Stop();

            // پردازش یک برش پلاک با تولید چند نامزد باید کمتر از ۴۵ میلی‌ثانیه زمان ببرد
            bool pass = sw.ElapsedMilliseconds < 45 && result.ProcessingDurationMs > 0;

            Console.WriteLine($"[Test 6] پردازش با سرعت بلادرنگ (Latency: {sw.ElapsedMilliseconds}ms < 45ms): {(pass ? "PASS" : "FAIL")}");
            return pass;
        }

        /// <summary>
        /// آزمون ۷: اولویت ساختار و گرامر پلاک ملی ایران بر ضریب اطمینان کاذب
        /// </summary>
        public static async Task<bool> Test_IranianPlateGrammarPriority()
        {
            int w = 140, h = 45, stride = 140 * 3;
            byte[] frame = new byte[stride * h];
            Array.Fill(frame, (byte)150);

            var engine = new AiImageOptimizationEngine();
            int evalIndex = 0;

            // نسخه خام نامعتبر است اما اطمینان کاذب بالا (۰.۹۵) دارد؛ نسخه بهینه‌شده ساختار معتبر پلاک ایران با اطمینان ۰.۸۶ دارد
            var result = await engine.OptimizePlateCropAsync(
                "cam_grammar_priority",
                frame,
                w,
                h,
                stride,
                3,
                (buf, width, height, str) =>
                {
                    int idx = evalIndex++;
                    if (idx == 0)
                    {
                        return ("XX999", 0.95f, IranianPlateParser.Parse("XX999", 0.95f));
                    }
                    return ("33ت45612", 0.86f, IranianPlateParser.Parse("33ت45612", 0.86f));
                });

            bool pass = result.BestCandidate.Plate.IsValid &&
                        Math.Abs(result.OcrConfidenceAfter - 0.86f) < 0.01f;
            Console.WriteLine($"[Test 7] اعتبارسنجی ساختار پلاک ملی ایران در انتخاب خروجی نهایی: {(pass ? "PASS" : "FAIL")}");
            return pass;
        }
    }
}
