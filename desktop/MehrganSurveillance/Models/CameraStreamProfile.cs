namespace MehrganSurveillance.Models
{
    /// <summary>
    /// مشخصات پروفایل ویدیویی دوربین (استریم اصلی یا فرعی)
    /// </summary>
    public class CameraStreamProfile
    {
        public string Name { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string RtspUri { get; set; } = string.Empty;
        public string Resolution { get; set; } = "نامشخص";
        public string Encoding { get; set; } = "H.264";
        public int Fps { get; set; } = 25;
        public bool IsSubStream { get; set; }

        public string DisplayLabel =>
            $"{Name} ({(IsSubStream ? "استریم فرعی" : "استریم اصلی")}) - {Resolution} - {Encoding}";

        public override string ToString() => DisplayLabel;
    }
}
