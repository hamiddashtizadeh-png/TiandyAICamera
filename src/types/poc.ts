/**
 * Core type definitions for the Tiandy Camera Streaming POC & Audit Suite
 */

export type ConnectionMethod = 'direct' | 'nvr';

export type VideoCodec =
  | 'H.264'
  | 'H.265'
  | 'S+265 (Smart H.265)'
  | 'S+265 (Smart H.265 - فرضیه)'
  | 'MJPEG'
  | 'Unknown'
  | 'نامشخص (نیاز به ffprobe)';

export type AudioCodec = 'AAC' | 'G.711u' | 'G.711a' | 'Opus' | 'None' | 'بدون صدا / غیرفعال';
export type TransportProtocol = 'TCP (Interleaved)' | 'UDP' | 'HTTP Tunneling';

export type VerificationState = 'verified' | 'untested' | 'hypothesis' | 'failed';
export type StepStatus = 'pending' | 'in_progress' | 'passed' | 'failed' | 'warning';
export type CameraVendor = 'tiandy' | 'onvif' | 'hikvision' | 'dahua' | 'generic';

export interface StreamProfile {
  streamNumber: 1 | 2 | 3;
  name: string;
  channel: number;
  rtspPath: string; // /1/1, /1/2, /1/3
  targetCodec: VideoCodec;
  detectedCodec: VideoCodec;
  resolution: string;
  fps: number;
  bitrateKbps: number;
  gopSize: number;
  isDecoded: boolean;
  webRtcCompatible: boolean;
  hlsCompatible: boolean;
  windowsCompatible: boolean;
  lastDecodedFramesCount: number;
  verificationState?: VerificationState;
  notes: string;
}

export interface VendorAdapter {
  id: string;
  name: string;
  isPrimary?: boolean;
  rtspUrlTemplate: string;
  generateUrl: (params: { host: string; port: number; channel: number; stream: number }) => string;
  managementPortDefault: number;
  rtspPortDefault: number;
  description: string;
}

export interface AuditStep {
  id: string;
  stageNumber: number;
  title: string;
  objective: string;
  method: ConnectionMethod;
  verificationState: VerificationState;
  commands: {
    label: string;
    description: string;
    command: string;
    expectedOutputSnippet?: string;
  }[];
  parameters: Record<string, string>;
  decodedFrameCount?: number;
  notes?: string;
  evidenceRequired: string;
}

export interface StreamEndpointConfig {
  label: string;
  method: ConnectionMethod;
  host: string;
  rtspPort: number;
  webManagementPort: number; // Tiandy Web Port: 3000
  username: string;
  passwordMask: string;
  channel: number;
  streamNumber: 1 | 2 | 3;
  resolvedRtspPath: string; // /1/1, /1/2, /1/3
  detectedCodec: VideoCodec;
  resolution: string;
  fps: number;
  bitrateKbps: number;
  keyframeIntervalGop: number;
  verificationState: VerificationState;
}

export interface DecodedFrameTelemetry {
  frameIndex: number;
  timestamp: string;
  frameType: 'I-Frame (کلیدی)' | 'P-Frame' | 'B-Frame' | 'I-Frame (Key)';
  sizeBytes: number;
  decodeTimeMs: number;
  pts: number;
  dts: number;
  resolution: string;
  codec: VideoCodec;
  colorSpace: string;
  transport: 'TCP';
  errorCount: number;
}

export interface ComparisonCriterion {
  criterion: string;
  category: 'latency' | 'stability' | 'compatibility' | 'resource' | 'security';
  directState: string;
  nvrState: string;
  verifiedEvidenceStatus: 'تاییدشده بر اساس آزمون' | 'نیازمند سنجش عملی در محل' | 'فرضیه معماری';
  technicalAnalysis: string;
}
