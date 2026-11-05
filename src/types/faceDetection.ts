export interface DetectedFaceBox {
  x: number; // 0.0 to 1.0 (relative to frame width)
  y: number; // 0.0 to 1.0 (relative to frame height)
  width: number;
  height: number;
}

export interface DetectedFaceEvent {
  id: string;
  timestamp: string; // HH:mm:ss.SSS
  date: string; // YYYY-MM-DD
  channelId: number;
  channelName: string;
  confidence: number; // 0 to 100 percentage
  box: DetectedFaceBox;
  thumbnailUrl?: string;
  faceSvgKey?: number; // index to generate consistent distinct avatar
  trackId: number;
  qualityScore: number; // 0 - 100
  pose: 'روبرو (Frontal)' | 'زاویه‌دار (Slight Tilt)' | 'نیم‌رخ (Profile)';
  hasMask?: boolean;
  hasGlasses?: boolean;
}

export interface FaceDetectionSettings {
  enabled: boolean;
  showOverlay: boolean;
  showConfidenceLabel: boolean;
  minConfidence: number; // e.g. 70
  autoCaptureIntervalMs: number;
  anprEnabled: boolean;
  digitalZoom: number; // 1.0, 1.5, 2.0, 3.0
}
