using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MehrganSurveillance.Models;

namespace MehrganSurveillance.Services;

/// <summary>
/// سرویس اعتبارسنجی Execution Providerهای موتور استنتاج ONNX Runtime
/// وظایف:
/// ۱. تفکیک در دسترس بودن پرووایدر، ساخت سشن، اجرای استنتاج محاسباتی واقعی و فال‌بک CPU
/// ۲. رعایت محدودیت‌های DirectML (ترتیبی بودن، غیرفعال‌سازی الگوی حافظه، جلوگیری از فراخوانی‌های همزمان روی یک سشن)
/// ۳. عدم انتساب کاذب اجرای کامل روی GPU بدون شواهد پروفایلینگ
/// ۴. ارزیابی ناهمگام، پذیرش CancellationToken و لایه‌بندی ایمن خطاها
/// </summary>
public class ExecutionProviderVerifier
{
    // قفل همزمانی per-session جهت جلوگیری از اجرای همزمان چند متد Run روی یک سشن DirectML واحد
    private readonly SemaphoreSlim _sessionExecutionLock = new(1, 1);

    /// <summary>
    /// مدل مینیاتوری معتبر ONNX با عملگر محاسباتی واقعی Add با ورودی float[1, 2] و بیاس ثابت [2.0, 3.0]
    /// این مدل بر اساس Protobuf IR v8 و Opset 13 طراحی شده و به دلیل دارا بودن عملگر غیرقابل حذف Add توسط بهینه‌ساز گراف،
    /// موتور ONNX Runtime را مجبور به فراخوانی Kernel محاسباتی در پرووایدر مربوطه (DirectML یا CPU) می‌کند.
    /// طول این مدل مینیاتوری ۱۵۰ بایت است و هیچ وابستگی یا مدل خارجی لود نمی‌شود.
    /// </summary>
    private static readonly byte[] MinimalValidAddModelBytes = Convert.FromHexString(
        "0808120f4d65687267616e56657269666965723a7d0a200a05696e7075740a046269617312066f75747075741a04616464312203416464120e746573745f6164645f67726170682a160801080210014a0800000040000040403a04626961735a170a05696e707574120e0a0c080112080a0208010a02080262180a066f7574707574120e0a0c080112080a0208010a0208024202100d"
    );

    /// <summary>
    /// دسترسی امن به بایت‌های مدل مینیاتوری اعتبارسنجی شده برای استفاده در بنچمارک‌های تشخیصی
    /// </summary>
    public static byte[] GetMinimalValidAddModelBytes() => (byte[])MinimalValidAddModelBytes.Clone();


    /// <summary>
    /// ارزیابی کامل و تفکیک‌شده Execution Providerها در پس‌زمینه با پشتیبانی کامل از لغو ناهمگام
    /// </summary>
    public async Task<ExecutionProviderProfile> VerifyProvidersAsync(int directMlDeviceId = 0, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var warnings = new List<string>();
            string ortVersion = typeof(InferenceSession).Assembly.GetName().Version?.ToString() ?? "Unknown";

            // ۱. دریافت لیست Providerهای رجیستر شده در OrtEnv
            string[] availableProviders;
            try
            {
                availableProviders = OrtEnv.Instance().GetAvailableProviders();
            }
            catch (Exception ex)
            {
                warnings.Add($"خطا در دریافت لیست پرووایدرهای ONNX Runtime: {ex.Message}");
                availableProviders = Array.Empty<string>();
            }

            bool isCpuRegistered = availableProviders.Any(p => p.Contains("CPU", StringComparison.OrdinalIgnoreCase));
            bool isDmlRegistered = availableProviders.Any(p => p.Contains("DML", StringComparison.OrdinalIgnoreCase) || 
                                                               p.Contains("DirectML", StringComparison.OrdinalIgnoreCase));

            cancellationToken.ThrowIfCancellationRequested();

            // ۲. ارزیابی مستقل CPU Execution Provider
            ProviderEvaluationResult cpuResult = EvaluateCpuProvider(isCpuRegistered, cancellationToken, warnings);

            cancellationToken.ThrowIfCancellationRequested();

            // ۳. ارزیابی مستقل DirectML Execution Provider با رعایت قیود اختصاصی DML
            ProviderEvaluationResult dmlResult = EvaluateDirectMlProvider(isDmlRegistered, directMlDeviceId, cancellationToken, warnings);

            // ۴. بررسی اعتبارسنجی مکانیزم بازگشت مستقل (CPU Fallback)
            var (fallbackVerified, fallbackNotes) = VerifyCpuFallbackMechanism(dmlResult, cpuResult, warnings);

            return new ExecutionProviderProfile
            {
                EvaluatedAtUtc = DateTime.UtcNow,
                OnnxRuntimeVersion = ortVersion,
                AvailableProviders = availableProviders,
                CpuResult = cpuResult,
                DirectMlResult = dmlResult,
                FallbackToCpuVerified = fallbackVerified,
                FallbackNotes = fallbackNotes,
                VerificationWarnings = warnings.ToArray()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// ارزیابی مستقل CPU Execution Provider
    /// </summary>
    private ProviderEvaluationResult EvaluateCpuProvider(bool isRegistered, CancellationToken cancellationToken, List<string> warnings)
    {
        if (!isRegistered)
        {
            return new ProviderEvaluationResult
            {
                ProviderName = "CPUExecutionProvider",
                IsRegisteredInRuntime = false,
                State = ProviderOperationalState.NotAvailable,
                CanCreateSession = false,
                CanExecuteInference = false,
                PlacementStatus = OperatorPlacementStatus.Verified,
                PlacementNotes = "پرووایدر CPU در لیست پرووایدرهای ران‌تایم موجود نیست."
            };
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var sessionOptions = new SessionOptions();
            sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

            // ساخت سشن با مدل تست محاسباتی Add
            using var session = new InferenceSession(MinimalValidAddModelBytes, sessionOptions);

            cancellationToken.ThrowIfCancellationRequested();

            // اجرای استنتاج محاسباتی کنترل‌شده
            var (success, latencyMs, error) = ExecuteTestInference(session, cancellationToken);

            if (success)
            {
                return new ProviderEvaluationResult
                {
                    ProviderName = "CPUExecutionProvider",
                    IsRegisteredInRuntime = true,
                    State = ProviderOperationalState.FullyOperational,
                    CanCreateSession = true,
                    CanExecuteInference = true,
                    InferenceLatencyMs = latencyMs,
                    PlacementStatus = OperatorPlacementStatus.Verified,
                    PlacementNotes = "عملگر محاسباتی Add به صورت قطعی و با صحت کامل خروجی روی هسته‌های CPU اجرا شد."
                };
            }
            else
            {
                warnings.Add($"سشن CPU ساخته شد اما اجرای استنتاج محاسباتی ناموفق بود: {error}");
                return new ProviderEvaluationResult
                {
                    ProviderName = "CPUExecutionProvider",
                    IsRegisteredInRuntime = true,
                    State = ProviderOperationalState.SessionCreatedExecutionFailed,
                    CanCreateSession = true,
                    CanExecuteInference = false,
                    ErrorMessage = error,
                    PlacementStatus = OperatorPlacementStatus.Unknown,
                    PlacementNotes = "ساخت سشن موفق بود اما اجرای عملگر Add با شکست مواجه شد."
                };
            }
        }
        catch (OperationCanceledException)
        {
            throw; // هدایت صریح عملیات لغو شده به لایه بالاتر
        }
        catch (Exception ex)
        {
            warnings.Add($"خطا در ساخت سشن CPU: {ex.Message}");
            return new ProviderEvaluationResult
            {
                ProviderName = "CPUExecutionProvider",
                IsRegisteredInRuntime = true,
                State = ProviderOperationalState.SessionCreationFailed,
                CanCreateSession = false,
                CanExecuteInference = false,
                ErrorMessage = ex.Message,
                PlacementStatus = OperatorPlacementStatus.Unknown,
                PlacementNotes = "ساخت سشن CPU با خطای ران‌تایم متوقف شد."
            };
        }
    }

    /// <summary>
    /// ارزیابی مستقل DirectML Provider با رعایت قیود عدم همزمانی و بهینه‌سازی حافظه
    /// </summary>
    private ProviderEvaluationResult EvaluateDirectMlProvider(bool isRegistered, int deviceId, CancellationToken cancellationToken, List<string> warnings)
    {
        if (!isRegistered)
        {
            return new ProviderEvaluationResult
            {
                ProviderName = "DmlExecutionProvider",
                IsRegisteredInRuntime = false,
                State = ProviderOperationalState.NotAvailable,
                CanCreateSession = false,
                CanExecuteInference = false,
                PlacementStatus = OperatorPlacementStatus.Unknown,
                PlacementNotes = "پرووایدر DirectML در میان پرووایدرهای ثبت‌شده ONNX Runtime یافت نشد."
            };
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var sessionOptions = new SessionOptions();

            // ۱. الزامات صریح DirectML: حالت ترتیبی (Sequential)
            sessionOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;

            // ۲. غیرفعال‌سازی الگوی حافظه جهت پیشگیری از خطاهای تخصیص در DirectML
            sessionOptions.EnableMemoryPattern = false;

            // ۳. الصاق DirectML Provider با Device ID مشخص‌شده
            try
            {
                sessionOptions.AppendExecutionProvider_DML(deviceId);
            }
            catch (Exception ex)
            {
                warnings.Add($"پرووایدر DML رجیستر شده است اما الصاق به سخت‌افزار گرافیکی با خطا مواجه شد (Device {deviceId}): {ex.Message}");
                return new ProviderEvaluationResult
                {
                    ProviderName = "DmlExecutionProvider",
                    IsRegisteredInRuntime = true,
                    State = ProviderOperationalState.SessionCreationFailed,
                    CanCreateSession = false,
                    CanExecuteInference = false,
                    ErrorMessage = $"خطا در الصاق پرووایدر DirectML: {ex.Message}",
                    PlacementStatus = OperatorPlacementStatus.Unknown,
                    PlacementNotes = "الصاق پرووایدر به آداپتور گرافیکی با خطای سخت‌افزار یا درایور متوقف شد."
                };
            }

            // ۴. ساخت سشن با مدل تست محاسباتی Add
            using var session = new InferenceSession(MinimalValidAddModelBytes, sessionOptions);

            cancellationToken.ThrowIfCancellationRequested();

            // ۵. اجرای استنتاج محاسباتی تحت محافظت قفل همزمانی per-session
            var (success, latencyMs, error) = ExecuteTestInference(session, cancellationToken);

            if (success)
            {
                return new ProviderEvaluationResult
                {
                    ProviderName = "DmlExecutionProvider",
                    IsRegisteredInRuntime = true,
                    State = ProviderOperationalState.FullyOperational,
                    CanCreateSession = true,
                    CanExecuteInference = true,
                    InferenceLatencyMs = latencyMs,
                    // جایگذاری اپراتور بر اساس شواهد استنتاج محاسباتی به صورت Inferred علامت‌گذاری می‌شود
                    PlacementStatus = OperatorPlacementStatus.Inferred,
                    PlacementNotes = "سشن DirectML و مدل محاسباتی Add با موفقیت و صحت کامل خروجی ([5.14, 5.71]) اجرا شدند. جایگذاری اپراتورها در لایه گرافیک استنباط‌شده (Inferred) است و تایید قطعی ۱۰۰٪ بدون لاگ پروفایل نیتیو گزارش نمی‌شود."
                };
            }
            else
            {
                warnings.Add($"سشن DirectML ایجاد شد اما اجرای مدل محاسباتی Add با شکست مواجه شد: {error}");
                return new ProviderEvaluationResult
                {
                    ProviderName = "DmlExecutionProvider",
                    IsRegisteredInRuntime = true,
                    State = ProviderOperationalState.SessionCreatedExecutionFailed,
                    CanCreateSession = true,
                    CanExecuteInference = false,
                    ErrorMessage = error,
                    PlacementStatus = OperatorPlacementStatus.Unknown,
                    PlacementNotes = "سشن DirectML ساخته شد اما اجرای محاسباتی Add با خطای لایه DirectML یا درایور مواجه شد."
                };
            }
        }
        catch (OperationCanceledException)
        {
            throw; // لغو درخواست قبل یا حین ساخت سشن
        }
        catch (Exception ex)
        {
            warnings.Add($"خطا در ساخت یا اجرای سشن DirectML: {ex.Message}");
            return new ProviderEvaluationResult
            {
                ProviderName = "DmlExecutionProvider",
                IsRegisteredInRuntime = true,
                State = ProviderOperationalState.SessionCreationFailed,
                CanCreateSession = false,
                CanExecuteInference = false,
                ErrorMessage = ex.Message,
                PlacementStatus = OperatorPlacementStatus.Unknown,
                PlacementNotes = "ساخت سشن DirectML با خطای سیستم یا عدم پشتیبانی کارت گرافیک متوقف شد."
            };
        }
    }

    /// <summary>
    /// اجرای استنتاج کنترل‌شده روی مدل محاسباتی Add با ورودی و خروجی معین
    /// ورودی dynamic tensor: float[1, 2] = [3.14f, 2.71f]
    /// ثابت داخل گراف (initializer): float[1, 2] = [2.0f, 3.0f]
    /// خروجی مورد انتظار از Add: [5.14f, 5.71f]
    /// </summary>
    private (bool Success, double? LatencyMs, string? Error) ExecuteTestInference(InferenceSession session, CancellationToken cancellationToken)
    {        // محافظت در برابر فراخوانی‌های همزمان روی یک سشن در صورت دسترسی چندباره
        _sessionExecutionLock.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inputData = new float[] { 3.14f, 2.71f };
            var tensor = new DenseTensor<float>(inputData, new[] { 1, 2 });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", tensor)
            };

            var sw = Stopwatch.StartNew();
            // فراخوانی نیتیو ONNX Runtime session.Run
            // توجه: فراخوانی session.Run یک فراخوانی همگام نیتیو C است. CancellationToken قبل و بعد از این فراخوانی بررسی می‌شود.
            using var results = session.Run(inputs);
            sw.Stop();

            cancellationToken.ThrowIfCancellationRequested();

            var outputTensor = results.FirstOrDefault(r => r.Name == "output")?.AsTensor<float>();
            if (outputTensor == null)
            {
                return (false, null, "تنسور خروجی با نام 'output' در نتایج استنتاج مدل یافت نشد.");
            }

            var outputArray = outputTensor.ToArray();
            if (outputArray.Length != 2 || 
                Math.Abs(outputArray[0] - 5.14f) > 0.001f || 
                Math.Abs(outputArray[1] - 5.71f) > 0.001f)
            {
                return (false, null, $"مقادیر خروجی مدل محاسباتی ({outputArray[0]}, {outputArray[1]}) با مقادیر مورد انتظار ([5.14, 5.71]) مطابقت نداشت.");
            }

            return (true, sw.Elapsed.TotalMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            return (false, null, "عملیات استنتاج پیش از اتمام توسط CancellationToken لغو گردید.");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
        finally
        {
            _sessionExecutionLock.Release();
        }
    }

    /// <summary>
    /// اعتبارسنجی مکانیزم بازگشت مستقل به CPU (CPU Fallback)
    /// </summary>
    private (bool Verified, string Notes) VerifyCpuFallbackMechanism(
        ProviderEvaluationResult dmlResult, 
        ProviderEvaluationResult cpuResult,
        List<string> warnings)
    {
        if (cpuResult.State == ProviderOperationalState.FullyOperational)
        {
            if (dmlResult.State != ProviderOperationalState.FullyOperational)
            {
                warnings.Add("پرووایدر DirectML کاملاً عملیاتی نیست؛ سیستم به صورت خودکار به سشن مستقل CPU بازگشت می‌کند.");
                return (true, "DirectML غیرفعال یا ناموفق است، اما سشن مستقل CPU اعتبارسنجی شده و آماده جایگزینی است.");
            }

            return (true, "هر دو پرووایدر DirectML و CPU کاملاً اعتبارسنجی شده و آماده بهره‌برداری با قابلیت Fallback مستقل هستند.");
        }

        warnings.Add("هشدار بحرانی: پرووایدر CPU در آزمون محاسباتی شکست خورده است؛ مکانیزم امنی برای Fallback وجود ندارد.");
        return (false, "سشن مستقل CPU با شکست مواجه شد و مکانیزم Fallback ایمن در دسترس نیست.");
    }
}
