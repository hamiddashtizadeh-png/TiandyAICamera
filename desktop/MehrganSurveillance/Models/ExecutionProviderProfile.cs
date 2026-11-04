using System;

namespace MehrganSurveillance.Models;

/// <summary>
/// وضعیت بررسی و انتساب اپراتورها روی شتاب‌دهنده گرافیکی
/// </summary>
public enum OperatorPlacementStatus
{
    /// <summary>
    /// وضعیت جایگذاری اپراتورها نامشخص یا تایید نشده است
    /// </summary>
    Unknown,

    /// <summary>
    /// بر اساس شواهد آماری، تاخیر، یا رفتار سیستم استنباط شده است
    /// </summary>
    Inferred,

    /// <summary>
    /// با شواهد پروفایلینگ رسمی یا لاگ‌های رسمی ONNX Runtime به صورت قطعی تایید شده است
    /// </summary>
    Verified
}

/// <summary>
/// وضعیت دسترسی و قابلیت اجرای یک Execution Provider خاص
/// </summary>
public enum ProviderOperationalState
{
    NotAvailable,
    AvailableNotTested,
    SessionCreationFailed,
    SessionCreatedExecutionFailed,
    FullyOperational
}

/// <summary>
/// پرونده نتیجه بررسی یک Execution Provider در ONNX Runtime
/// </summary>
public class ProviderEvaluationResult
{
    public string ProviderName { get; init; } = string.Empty;
    public bool IsRegisteredInRuntime { get; init; }
    public ProviderOperationalState State { get; init; } = ProviderOperationalState.NotAvailable;
    public bool CanCreateSession { get; init; }
    public bool CanExecuteInference { get; init; }
    public double? InferenceLatencyMs { get; init; }
    public string? ErrorMessage { get; init; }
    public OperatorPlacementStatus PlacementStatus { get; init; } = OperatorPlacementStatus.Unknown;
    public string PlacementNotes { get; init; } = string.Empty;
}

/// <summary>
/// نمایه جامع بررسی وضعیت Execution Providerهای موتور هوش مصنوعی در پروژه مهرگان
/// </summary>
public class ExecutionProviderProfile
{
    public DateTime EvaluatedAtUtc { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// نسخه اسمبلی ONNX Runtime شناسایی‌شده در پروژه
    /// </summary>
    public string OnnxRuntimeVersion { get; init; } = string.Empty;

    /// <summary>
    /// لیست تمام Providerهای در دسترس اعلام‌شده توسط موتور
    /// </summary>
    public string[] AvailableProviders { get; init; } = Array.Empty<string>();

    /// <summary>
    /// نتیجه ارزیابی اختصاصی CPU Execution Provider
    /// </summary>
    public ProviderEvaluationResult CpuResult { get; init; } = new();

    /// <summary>
    /// نتیجه ارزیابی اختصاصی DirectML Execution Provider
    /// </summary>
    public ProviderEvaluationResult DirectMlResult { get; init; } = new();

    /// <summary>
    /// آیا مکانیزم Fallback از DirectML به CPU به صورت مستقل تست شده و کار می‌کند؟
    /// </summary>
    public bool FallbackToCpuVerified { get; init; }

    /// <summary>
    /// پیام یا توضیحات فنی فال‌بک
    /// </summary>
    public string FallbackNotes { get; init; } = string.Empty;

    /// <summary>
    /// پرووایدر اجرایی توصیه‌شده اولیه برای مدل‌ها
    /// </summary>
    public string RecommendedInitialProvider => 
        DirectMlResult.State == ProviderOperationalState.FullyOperational ? "DML" : "CPU";

    /// <summary>
    /// هشدارهای مهندسی یا ریسک‌های تشخیص‌داده‌شده در هنگام سنجش
    /// </summary>
    public string[] VerificationWarnings { get; init; } = Array.Empty<string>();
}
