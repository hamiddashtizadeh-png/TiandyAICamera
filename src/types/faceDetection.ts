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
  interpolatedEnhanced?: boolean;
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

export type InterpolationFactor = 2 | 4;
export type UpscaleFactor = 2 | 3 | 4;

export interface FrameInterpolationConfig {
  interpolationFactor: InterpolationFactor;
  upscaleFactor: UpscaleFactor;
  stabilizationStrength: number; // 0.0 to 1.0
  deblurStrength: number; // 0.0 to 1.0
  temporalDenoise: boolean;
  sourceFps: number;
}

export interface MotionVectorCell {
  gx: number; // normalized 0..1 x center
  gy: number; // normalized 0..1 y center
  vx: number; // horizontal displacement in source px
  vy: number; // vertical displacement in source px
}

export interface ProcessedFrameItem {
  index: number;
  isInterpolated: boolean;
  sourceIndex: number;
  alpha: number; // 0 for original keyframe, (0..1) for interpolated
  dataUrl: string;
  width: number;
  height: number;
  shiftX: number; // estimated raw horizontal jitter before stabilization
  shiftY: number; // estimated raw vertical jitter before stabilization
  correctedX: number; // applied stabilization shift X
  correctedY: number; // applied stabilization shift Y
  sharpness: number;
  motionVectors: MotionVectorCell[];
}

export interface FrameInterpolationMetrics {
  inputWidth: number;
  inputHeight: number;
  outputWidth: number;
  outputHeight: number;
  inputFrameCount: number;
  outputFrameCount: number;
  inputFps: number;
  outputFps: number;
  jitterRmsBeforePx: number;
  jitterRmsAfterPx: number;
  stabilizationGainPct: number;
  sharpnessBefore: number;
  sharpnessAfter: number;
  sharpnessGainPct: number;
  verificationScoreBefore: number;
  verificationScoreAfter: number;
  bestFrameIndex: number;
  processingTimeMs: number;
}

export interface FrameInterpolationResult {
  sourceFrames: string[];
  processedFrames: ProcessedFrameItem[];
  bestFrame: ProcessedFrameItem;
  metrics: FrameInterpolationMetrics;
  sourceLabel: string;
}
