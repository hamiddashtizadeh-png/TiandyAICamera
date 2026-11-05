# Iranian License Plate Recognition (ANPR) Models Specification

## Architecture Overview
The Mehrgan ANPR Pipeline operates in two decoupled stages:
1. **Vehicle & License Plate Detection**:
   - Model: YOLOv8n-License-Plate (Compact edge variant)
   - Input: 640x640x3 Float32 RGB tensor
   - Output: Normalized bounding boxes [x, y, w, h] + class confidence
   - Target Hardware: DirectML on Windows (Intel/AMD/NVIDIA GPU) with CPU Fallback
   - License: AGPL-3.0 / Commercial friendly conversion for ONNX runtime deployment

2. **Iranian Plate Character Recognition (OCR)**:
   - Model: CRNN / Conv-BiLSTM-CTC or Vision Transformer for Iranian Plate Structure
   - Input: 160x40x1 (Grayscale or RGB) preprocessed normalized crop
   - Vocabulary: 26 Iranian characters (0-9, الف, ب, پ, ت, ث, ج, د, س, ص, ط, ع, ق, ل, م, ن, و, ه, ی, ژ, ک, ش, ز)
   - Output: Character sequence probability CTC matrix
   - Normalization: Passed through `IranianPlateNormalizer` to enforce official Iranian plate grammar (`۲ رقمی + حرف + ۳ رقمی + کد استان`).

## Weight Files & Deployment Readiness
In accordance with production deployment and zero-hallucination policies:
- If the binary `.onnx` weight files (`yolov8n-license-plate.onnx` and `iranian-plate-ocr.onnx`) are placed in this directory, `AnprPipeline` loads and attaches them via ONNX Runtime DirectML.
- If weight files are not installed, the engine explicitly reports:
  `DetectorLoaded = false, OcrLoaded = false, StatusMessage = "Not Ready"`
  and will NOT fabricate fake predictions.
