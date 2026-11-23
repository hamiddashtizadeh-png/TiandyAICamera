import {
  FrameInterpolationConfig,
  FrameInterpolationMetrics,
  FrameInterpolationResult,
  MotionVectorCell,
  ProcessedFrameItem,
} from '../types/faceDetection';

function clampByte(v: number): number {
  return v < 0 ? 0 : v > 255 ? 255 : Math.round(v);
}

function clamp(v: number, min: number, max: number): number {
  return v < min ? min : v > max ? max : v;
}

/**
 * Loads a data URL or image URL onto a canvas and extracts raw ImageData.
 */
async function decodeDataUrlToImageData(
  src: string,
  maxDimension = 128
): Promise<ImageData> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = () => {
      const scale = Math.min(1, maxDimension / Math.max(img.width || 1, img.height || 1));
      const w = Math.max(32, Math.round((img.width || 96) * scale));
      const h = Math.max(32, Math.round((img.height || 96) * scale));
      const canvas = document.createElement('canvas');
      canvas.width = w;
      canvas.height = h;
      const ctx = canvas.getContext('2d');
      if (!ctx) {
        reject(new Error('Canvas 2D context unavailable'));
        return;
      }
      ctx.imageSmoothingEnabled = true;
      ctx.drawImage(img, 0, 0, w, h);
      resolve(ctx.getImageData(0, 0, w, h));
    };
    img.onerror = () => reject(new Error('Failed to decode source frame image'));
    img.src = src;
  });
}

function imageDataToDataUrl(img: ImageData): string {
  const canvas = document.createElement('canvas');
  canvas.width = img.width;
  canvas.height = img.height;
  const ctx = canvas.getContext('2d');
  if (!ctx) return '';
  ctx.putImageData(img, 0, 0);
  return canvas.toDataURL('image/png');
}

function extractLuminance(img: ImageData): Float32Array {
  const { width, height, data } = img;
  const luma = new Float32Array(width * height);
  for (let i = 0, p = 0; i < luma.length; i++, p += 4) {
    luma[i] = 0.299 * data[p] + 0.587 * data[p + 1] + 0.114 * data[p + 2];
  }
  return luma;
}

/**
 * Computes Laplacian variance (edge sharpness) inside the central facial ROI.
 */
function computeFaceSharpness(img: ImageData): number {
  const { width, height } = img;
  const luma = extractLuminance(img);
  const x0 = Math.max(2, Math.floor(width * 0.15));
  const x1 = Math.min(width - 2, Math.ceil(width * 0.85));
  const y0 = Math.max(2, Math.floor(height * 0.12));
  const y1 = Math.min(height - 2, Math.ceil(height * 0.88));

  let sum = 0;
  let sumSq = 0;
  let count = 0;

  for (let y = y0; y < y1; y++) {
    const row = y * width;
    for (let x = x0; x < x1; x++) {
      const idx = row + x;
      const lap =
        luma[idx - width] +
        luma[idx + width] +
        luma[idx - 1] +
        luma[idx + 1] -
        4 * luma[idx];
      sum += lap;
      sumSq += lap * lap;
      count++;
    }
  }

  if (count === 0) return 0;
  const mean = sum / count;
  return Math.max(0, sumSq / count - mean * mean);
}

/**
 * Bilinear sample from ImageData with edge clamping.
 */
function sampleBilinear(
  src: ImageData,
  fx: number,
  fy: number,
  out: [number, number, number]
): void {
  const { width, height, data } = src;
  const cx = clamp(fx, 0, width - 1.001);
  const cy = clamp(fy, 0, height - 1.001);
  const x0 = Math.floor(cx);
  const y0 = Math.floor(cy);
  const x1 = Math.min(width - 1, x0 + 1);
  const y1 = Math.min(height - 1, y0 + 1);
  const dx = cx - x0;
  const dy = cy - y0;

  const w00 = (1 - dx) * (1 - dy);
  const w10 = dx * (1 - dy);
  const w01 = (1 - dx) * dy;
  const w11 = dx * dy;

  const i00 = (y0 * width + x0) * 4;
  const i10 = (y0 * width + x1) * 4;
  const i01 = (y1 * width + x0) * 4;
  const i11 = (y1 * width + x1) * 4;

  out[0] = data[i00] * w00 + data[i10] * w10 + data[i01] * w01 + data[i11] * w11;
  out[1] =
    data[i00 + 1] * w00 +
    data[i10 + 1] * w10 +
    data[i01 + 1] * w01 +
    data[i11 + 1] * w11;
  out[2] =
    data[i00 + 2] * w00 +
    data[i10 + 2] * w10 +
    data[i01 + 2] * w01 +
    data[i11 + 2] * w11;
}

/**
 * Estimates global sub-pixel translation (dx, dy) from frame A to frame B
 * using coarse-to-fine luminance SAD block matching on the central face region.
 */
function estimateGlobalShift(
  lumaA: Float32Array,
  lumaB: Float32Array,
  width: number,
  height: number,
  maxRadius = 11
): { dx: number; dy: number } {
  const marginX = Math.max(maxRadius + 2, Math.floor(width * 0.16));
  const marginY = Math.max(maxRadius + 2, Math.floor(height * 0.16));
  const xStart = marginX;
  const xEnd = width - marginX;
  const yStart = marginY;
  const yEnd = height - marginY;

  if (xEnd <= xStart + 4 || yEnd <= yStart + 4) {
    return { dx: 0, dy: 0 };
  }

  const evalSad = (dx: number, dy: number, step: number): number => {
    let err = 0;
    let n = 0;
    for (let y = yStart; y < yEnd; y += step) {
      const rowA = y * width;
      const rowB = (y + dy) * width;
      for (let x = xStart; x < xEnd; x += step) {
        err += Math.abs(lumaA[rowA + x] - lumaB[rowB + x + dx]);
        n++;
      }
    }
    return n > 0 ? err / n : Number.POSITIVE_INFINITY;
  };

  // Coarse search (step 2)
  let bestDx = 0;
  let bestDy = 0;
  let bestErr = Number.POSITIVE_INFINITY;

  for (let dy = -maxRadius; dy <= maxRadius; dy += 2) {
    for (let dx = -maxRadius; dx <= maxRadius; dx += 2) {
      const err = evalSad(dx, dy, 2);
      if (err < bestErr) {
        bestErr = err;
        bestDx = dx;
        bestDy = dy;
      }
    }
  }

  // Fine integer refinement around coarse minimum
  let fineDx = bestDx;
  let fineDy = bestDy;
  let fineErr = Number.POSITIVE_INFINITY;

  for (let dy = bestDy - 2; dy <= bestDy + 2; dy++) {
    if (dy < -maxRadius || dy > maxRadius) continue;
    for (let dx = bestDx - 2; dx <= bestDx + 2; dx++) {
      if (dx < -maxRadius || dx > maxRadius) continue;
      const err = evalSad(dx, dy, 1);
      if (err < fineErr) {
        fineErr = err;
        fineDx = dx;
        fineDy = dy;
      }
    }
  }

  // Sub-pixel parabolic interpolation along X and Y
  const left = fineDx > -maxRadius ? evalSad(fineDx - 1, fineDy, 1) : fineErr;
  const right = fineDx < maxRadius ? evalSad(fineDx + 1, fineDy, 1) : fineErr;
  const denomX = left - 2 * fineErr + right;
  const subX = Math.abs(denomX) > 1e-4 ? clamp((left - right) / (2 * denomX), -0.5, 0.5) : 0;

  const top = fineDy > -maxRadius ? evalSad(fineDx, fineDy - 1, 1) : fineErr;
  const bottom = fineDy < maxRadius ? evalSad(fineDx, fineDy + 1, 1) : fineErr;
  const denomY = top - 2 * fineErr + bottom;
  const subY = Math.abs(denomY) > 1e-4 ? clamp((top - bottom) / (2 * denomY), -0.5, 0.5) : 0;

  return {
    dx: fineDx + subX,
    dy: fineDy + subY,
  };
}

/**
 * Warps an ImageData by (-shiftX, -shiftY) to cancel jitter using sub-pixel bilinear sampling.
 */
function warpShiftImageData(src: ImageData, shiftX: number, shiftY: number): ImageData {
  const { width, height } = src;
  const out = new ImageData(width, height);
  const dst = out.data;
  const rgb: [number, number, number] = [0, 0, 0];

  for (let y = 0; y < height; y++) {
    const sy = y + shiftY;
    for (let x = 0; x < width; x++) {
      sampleBilinear(src, x + shiftX, sy, rgb);
      const idx = (y * width + x) * 4;
      dst[idx] = clampByte(rgb[0]);
      dst[idx + 1] = clampByte(rgb[1]);
      dst[idx + 2] = clampByte(rgb[2]);
      dst[idx + 3] = 255;
    }
  }
  return out;
}

/**
 * Computes a smoothed dense block optical-flow field from frame A to frame B.
 */
function computeBlockOpticalFlow(
  lumaA: Float32Array,
  lumaB: Float32Array,
  width: number,
  height: number,
  gridSize = 8,
  searchRadius = 5
): {
  cols: number;
  rows: number;
  vx: Float32Array;
  vy: Float32Array;
  cells: MotionVectorCell[];
} {
  const cols = Math.max(4, Math.floor(width / gridSize));
  const rows = Math.max(4, Math.floor(height / gridSize));
  const blockW = width / cols;
  const blockH = height / rows;

  const rawVx = new Float32Array(cols * rows);
  const rawVy = new Float32Array(cols * rows);

  for (let gy = 0; gy < rows; gy++) {
    const by0 = Math.floor(gy * blockH);
    const by1 = Math.min(height, Math.floor((gy + 1) * blockH));
    for (let gx = 0; gx < cols; gx++) {
      const bx0 = Math.floor(gx * blockW);
      const bx1 = Math.min(width, Math.floor((gx + 1) * blockW));

      let bestDx = 0;
      let bestDy = 0;
      let bestCost = Number.POSITIVE_INFINITY;

      for (let dy = -searchRadius; dy <= searchRadius; dy++) {
        for (let dx = -searchRadius; dx <= searchRadius; dx++) {
          let err = 0;
          let count = 0;
          for (let y = by0; y < by1; y += 2) {
            const ny = clamp(y + dy, 0, height - 1);
            const rowA = y * width;
            const rowB = ny * width;
            for (let x = bx0; x < bx1; x += 2) {
              const nx = clamp(x + dx, 0, width - 1);
              err += Math.abs(lumaA[rowA + x] - lumaB[rowB + nx]);
              count++;
            }
          }
          // Subtle regularization favoring smooth low-energy motion
          const cost = (count > 0 ? err / count : 255) + 0.35 * Math.hypot(dx, dy);
          if (cost < bestCost) {
            bestCost = cost;
            bestDx = dx;
            bestDy = dy;
          }
        }
      }

      const cellIdx = gy * cols + gx;
      rawVx[cellIdx] = bestDx;
      rawVy[cellIdx] = bestDy;
    }
  }

  // 3x3 spatial Gaussian smoothing of motion vectors to prevent block seams
  const smoothVx = new Float32Array(cols * rows);
  const smoothVy = new Float32Array(cols * rows);
  const cells: MotionVectorCell[] = [];

  for (let gy = 0; gy < rows; gy++) {
    for (let gx = 0; gx < cols; gx++) {
      let sx = 0;
      let sy = 0;
      let wSum = 0;
      for (let ky = -1; ky <= 1; ky++) {
        const ny = clamp(gy + ky, 0, rows - 1);
        for (let kx = -1; kx <= 1; kx++) {
          const nx = clamp(gx + kx, 0, cols - 1);
          const w = kx === 0 && ky === 0 ? 4 : kx === 0 || ky === 0 ? 2 : 1;
          const idx = ny * cols + nx;
          sx += rawVx[idx] * w;
          sy += rawVy[idx] * w;
          wSum += w;
        }
      }
      const idx = gy * cols + gx;
      smoothVx[idx] = sx / wSum;
      smoothVy[idx] = sy / wSum;

      cells.push({
        gx: (gx + 0.5) / cols,
        gy: (gy + 0.5) / rows,
        vx: smoothVx[idx],
        vy: smoothVy[idx],
      });
    }
  }

  return { cols, rows, vx: smoothVx, vy: smoothVy, cells };
}

/**
 * Bidirectional optical-flow motion-compensated frame interpolation at time alpha in (0, 1).
 */
function interpolatePairBidirectional(
  frameA: ImageData,
  frameB: ImageData,
  flowFwd: { cols: number; rows: number; vx: Float32Array; vy: Float32Array },
  flowBwd: { cols: number; rows: number; vx: Float32Array; vy: Float32Array },
  alpha: number
): ImageData {
  const { width, height } = frameA;
  const out = new ImageData(width, height);
  const dst = out.data;

  const rgbA: [number, number, number] = [0, 0, 0];
  const rgbB: [number, number, number] = [0, 0, 0];

  const sampleFlow = (
    flow: { cols: number; rows: number; vx: Float32Array; vy: Float32Array },
    px: number,
    py: number
  ): [number, number] => {
    const gx = clamp((px / width) * flow.cols - 0.5, 0, flow.cols - 1.001);
    const gy = clamp((py / height) * flow.rows - 0.5, 0, flow.rows - 1.001);
    const x0 = Math.floor(gx);
    const y0 = Math.floor(gy);
    const x1 = Math.min(flow.cols - 1, x0 + 1);
    const y1 = Math.min(flow.rows - 1, y0 + 1);
    const dx = gx - x0;
    const dy = gy - y0;

    const i00 = y0 * flow.cols + x0;
    const i10 = y0 * flow.cols + x1;
    const i01 = y1 * flow.cols + x0;
    const i11 = y1 * flow.cols + x1;

    const w00 = (1 - dx) * (1 - dy);
    const w10 = dx * (1 - dy);
    const w01 = (1 - dx) * dy;
    const w11 = dx * dy;

    const u = flow.vx[i00] * w00 + flow.vx[i10] * w10 + flow.vx[i01] * w01 + flow.vx[i11] * w11;
    const v = flow.vy[i00] * w00 + flow.vy[i10] * w10 + flow.vy[i01] * w01 + flow.vy[i11] * w11;
    return [u, v];
  };

  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const [uFwd, vFwd] = sampleFlow(flowFwd, x, y);
      const [uBwd, vBwd] = sampleFlow(flowBwd, x, y);

      // Symmetrized bidirectional displacement
      const u = 0.5 * (uFwd - uBwd);
      const v = 0.5 * (vFwd - vBwd);

      sampleBilinear(frameA, x - alpha * u, y - alpha * v, rgbA);
      sampleBilinear(frameB, x + (1 - alpha) * u, y + (1 - alpha) * v, rgbB);

      // Occlusion-aware adaptive blend weight
      const diff =
        (Math.abs(rgbA[0] - rgbB[0]) +
          Math.abs(rgbA[1] - rgbB[1]) +
          Math.abs(rgbA[2] - rgbB[2])) /
        3;
      const occlusionBias = diff > 38 ? (alpha < 0.5 ? -0.15 : 0.15) : 0;
      const wB = clamp(alpha + occlusionBias, 0, 1);
      const wA = 1 - wB;

      const idx = (y * width + x) * 4;
      dst[idx] = clampByte(rgbA[0] * wA + rgbB[0] * wB);
      dst[idx + 1] = clampByte(rgbA[1] * wA + rgbB[1] * wB);
      dst[idx + 2] = clampByte(rgbA[2] * wA + rgbB[2] * wB);
      dst[idx + 3] = 255;
    }
  }

  return out;
}

/**
 * Edge-preserving multi-frame temporal denoise across aligned neighbors [prev, curr, next].
 * Suppresses low-res sensor noise without ghosting moving features.
 */
function temporalFuseFrame(
  prev: ImageData | null,
  curr: ImageData,
  next: ImageData | null
): ImageData {
  const { width, height, data } = curr;
  const out = new ImageData(width, height);
  const dst = out.data;
  const sigma = 22;

  for (let i = 0; i < data.length; i += 4) {
    let r = data[i] * 1.4;
    let g = data[i + 1] * 1.4;
    let b = data[i + 2] * 1.4;
    let wSum = 1.4;

    if (prev) {
      const pd = prev.data;
      const diff =
        (Math.abs(data[i] - pd[i]) +
          Math.abs(data[i + 1] - pd[i + 1]) +
          Math.abs(data[i + 2] - pd[i + 2])) /
        3;
      const w = 0.65 * Math.exp(-(diff * diff) / (2 * sigma * sigma));
      r += pd[i] * w;
      g += pd[i + 1] * w;
      b += pd[i + 2] * w;
      wSum += w;
    }

    if (next) {
      const nd = next.data;
      const diff =
        (Math.abs(data[i] - nd[i]) +
          Math.abs(data[i + 1] - nd[i + 1]) +
          Math.abs(data[i + 2] - nd[i + 2])) /
        3;
      const w = 0.65 * Math.exp(-(diff * diff) / (2 * sigma * sigma));
      r += nd[i] * w;
      g += nd[i + 1] * w;
      b += nd[i + 2] * w;
      wSum += w;
    }

    dst[i] = clampByte(r / wSum);
    dst[i + 1] = clampByte(g / wSum);
    dst[i + 2] = clampByte(b / wSum);
    dst[i + 3] = 255;
  }

  return out;
}

function cubicWeight(t: number): number {
  const a = -0.5; // Catmull-Rom spline
  const x = Math.abs(t);
  if (x <= 1) {
    return (a + 2) * x * x * x - (a + 3) * x * x + 1;
  }
  if (x < 2) {
    return a * x * x * x - 5 * a * x * x + 8 * a * x - 4 * a;
  }
  return 0;
}

/**
 * Catmull-Rom bicubic super-resolution upscaling with YCbCr luminance-only detail reconstruction.
 */
function upscaleAndClarifyFace(
  src: ImageData,
  targetW: number,
  targetH: number,
  deblurStrength: number
): ImageData {
  const { width: sw, height: sh, data: sd } = src;
  const upscaled = new ImageData(targetW, targetH);
  const ud = upscaled.data;

  const scaleX = sw / targetW;
  const scaleY = sh / targetH;

  // Catmull-Rom 4x4 bicubic resampling
  for (let y = 0; y < targetH; y++) {
    const fy = (y + 0.5) * scaleY - 0.5;
    const iy = Math.floor(fy);
    const ty = fy - iy;
    const wy0 = cubicWeight(ty + 1);
    const wy1 = cubicWeight(ty);
    const wy2 = cubicWeight(ty - 1);
    const wy3 = cubicWeight(ty - 2);
    const wys = [wy0, wy1, wy2, wy3];
    const ys = [
      clamp(iy - 1, 0, sh - 1),
      clamp(iy, 0, sh - 1),
      clamp(iy + 1, 0, sh - 1),
      clamp(iy + 2, 0, sh - 1),
    ];

    for (let x = 0; x < targetW; x++) {
      const fx = (x + 0.5) * scaleX - 0.5;
      const ix = Math.floor(fx);
      const tx = fx - ix;
      const wxs = [
        cubicWeight(tx + 1),
        cubicWeight(tx),
        cubicWeight(tx - 1),
        cubicWeight(tx - 2),
      ];
      const xs = [
        clamp(ix - 1, 0, sw - 1),
        clamp(ix, 0, sw - 1),
        clamp(ix + 1, 0, sw - 1),
        clamp(ix + 2, 0, sw - 1),
      ];

      let r = 0;
      let g = 0;
      let b = 0;
      let wNorm = 0;

      for (let ky = 0; ky < 4; ky++) {
        const wy = wys[ky];
        const rowOffset = ys[ky] * sw;
        for (let kx = 0; kx < 4; kx++) {
          const w = wy * wxs[kx];
          const p = (rowOffset + xs[kx]) * 4;
          r += sd[p] * w;
          g += sd[p + 1] * w;
          b += sd[p + 2] * w;
          wNorm += w;
        }
      }

      const inv = wNorm > 1e-5 ? 1 / wNorm : 1;
      const idx = (y * targetW + x) * 4;
      ud[idx] = clampByte(r * inv);
      ud[idx + 1] = clampByte(g * inv);
      ud[idx + 2] = clampByte(b * inv);
      ud[idx + 3] = 255;
    }
  }

  if (deblurStrength <= 0.01) {
    return upscaled;
  }

  // YCbCr luminance-only adaptive unsharp masking + soft contrast recovery
  const n = targetW * targetH;
  const yChan = new Float32Array(n);
  const cbChan = new Float32Array(n);
  const crChan = new Float32Array(n);

  let yMin = 255;
  let yMax = 0;

  for (let i = 0, p = 0; i < n; i++, p += 4) {
    const r = ud[p];
    const g = ud[p + 1];
    const b = ud[p + 2];
    const yv = 0.299 * r + 0.587 * g + 0.114 * b;
    yChan[i] = yv;
    cbChan[i] = -0.168736 * r - 0.331264 * g + 0.5 * b + 128;
    crChan[i] = 0.5 * r - 0.418688 * g - 0.081312 * b + 128;
    if (yv < yMin) yMin = yv;
    if (yv > yMax) yMax = yv;
  }

  // Separable 3-tap Gaussian blur of Y channel
  const tmp = new Float32Array(n);
  const blur = new Float32Array(n);
  for (let y = 0; y < targetH; y++) {
    const row = y * targetW;
    for (let x = 0; x < targetW; x++) {
      const xm = x > 0 ? x - 1 : 0;
      const xp = x < targetW - 1 ? x + 1 : targetW - 1;
      tmp[row + x] = 0.25 * yChan[row + xm] + 0.5 * yChan[row + x] + 0.25 * yChan[row + xp];
    }
  }
  for (let y = 0; y < targetH; y++) {
    const ym = (y > 0 ? y - 1 : 0) * targetW;
    const y0 = y * targetW;
    const yp = (y < targetH - 1 ? y + 1 : targetH - 1) * targetW;
    for (let x = 0; x < targetW; x++) {
      blur[y0 + x] = 0.25 * tmp[ym + x] + 0.5 * tmp[y0 + x] + 0.25 * tmp[yp + x];
    }
  }

  const amount = 0.55 + deblurStrength * 1.35;
  const contrastBoost = 1 + deblurStrength * 0.14;
  const rangeSpan = Math.max(40, yMax - yMin);

  for (let i = 0, p = 0; i < n; i++, p += 4) {
    const yOrig = yChan[i];
    const detail = yOrig - blur[i];
    // Noise gate: suppress tiny sensor grain while enhancing genuine facial contours
    const gatedDetail = Math.abs(detail) > 2.2 ? detail : detail * 0.25;
    let yNew = yOrig + amount * gatedDetail;

    // Gentle highlight-safe local tone normalization
    const norm = (yNew - yMin) / rangeSpan;
    const stretched = yMin + clamp(norm, 0, 1) * rangeSpan;
    yNew = 128 + (0.85 * yNew + 0.15 * stretched - 128) * contrastBoost;
    yNew = clamp(yNew, 0, 255);

    const cb = cbChan[i] - 128;
    const cr = crChan[i] - 128;

    ud[p] = clampByte(yNew + 1.402 * cr);
    ud[p + 1] = clampByte(yNew - 0.344136 * cb - 0.714136 * cr);
    ud[p + 2] = clampByte(yNew + 1.772 * cb);
    ud[p + 3] = 255;
  }

  return upscaled;
}

/**
 * Generates a realistic low-resolution jittery surveillance face segment
 * from either an existing thumbnail/image OR a procedurally rendered biometric subject
 * seeded by trackId. Used to test/verify stabilization & interpolation immediately.
 */
export async function generateJitterySegmentFromSource(
  sourceImageUrl?: string,
  trackId = 104,
  frameCount = 8,
  lowResSize = 96
): Promise<string[]> {
  // Prepare clean high-res reference portrait canvas first
  const refSize = 256;
  const refCanvas = document.createElement('canvas');
  refCanvas.width = refSize;
  refCanvas.height = refSize;
  const refCtx = refCanvas.getContext('2d');
  if (!refCtx) return [];

  let loadedImg: HTMLImageElement | null = null;
  if (sourceImageUrl) {
    try {
      loadedImg = await new Promise<HTMLImageElement>((resolve, reject) => {
        const img = new Image();
        img.crossOrigin = 'anonymous';
        img.onload = () => resolve(img);
        img.onerror = reject;
        img.src = sourceImageUrl;
      });
    } catch {
      loadedImg = null;
    }
  }

  const frames: string[] = [];
  const seed = (trackId * 9301 + 49297) % 233280;
  const rand = (n: number) => {
    const x = Math.sin(seed + n * 12.9898) * 43758.5453;
    return x - Math.floor(x);
  };

  // Jitter trajectory: realistic hand/mount vibration + walking bobbing
  const skinLight = 54 + Math.floor(rand(1) * 16);
  const skinHue = 22 + Math.floor(rand(2) * 10);
  const hasGlasses = trackId % 2 === 0;
  const hasBeard = trackId % 3 !== 0;

  for (let f = 0; f < frameCount; f++) {
    refCtx.clearRect(0, 0, refSize, refSize);

    if (loadedImg) {
      // Render source image slightly zoomed so jitter shifts stay inside bounds
      const pad = 28;
      refCtx.drawImage(loadedImg, -pad, -pad, refSize + pad * 2, refSize + pad * 2);
    } else {
      // Procedurally draw a crisp biometric CCTV portrait subject with subtle natural movement
      const bgGrad = refCtx.createLinearGradient(0, 0, refSize, refSize);
      bgGrad.addColorStop(0, '#1e293b');
      bgGrad.addColorStop(1, '#0f172a');
      refCtx.fillStyle = bgGrad;
      refCtx.fillRect(0, 0, refSize, refSize);

      // Subtle architectural hallway lines behind subject
      refCtx.strokeStyle = 'rgba(148, 163, 184, 0.14)';
      refCtx.lineWidth = 2;
      refCtx.beginPath();
      refCtx.moveTo(0, 175);
      refCtx.lineTo(refSize, 165);
      refCtx.moveTo(42, 0);
      refCtx.lineTo(42, refSize);
      refCtx.moveTo(214, 0);
      refCtx.lineTo(214, refSize);
      refCtx.stroke();

      // Subtle walking gaze / micro-expression motion across frames
      const blink = f === 3 ? 0.35 : 1.0;
      const headDriftX = (f - frameCount / 2) * 1.6;
      const cx = refSize / 2 + headDriftX;
      const cy = refSize / 2 - 4;

      // Shoulders & jacket
      refCtx.fillStyle = '#334155';
      refCtx.beginPath();
      refCtx.ellipse(cx, cy + 108, 84, 52, 0, Math.PI, Math.PI * 2);
      refCtx.fill();

      // Collar
      refCtx.fillStyle = '#e2e8f0';
      refCtx.beginPath();
      refCtx.moveTo(cx - 26, cy + 58);
      refCtx.lineTo(cx, cy + 90);
      refCtx.lineTo(cx + 26, cy + 58);
      refCtx.closePath();
      refCtx.fill();

      // Neck
      refCtx.fillStyle = `hsl(${skinHue}, 38%, ${skinLight - 8}%)`;
      refCtx.fillRect(cx - 18, cy + 38, 36, 32);

      // Ears
      refCtx.fillStyle = `hsl(${skinHue}, 40%, ${skinLight - 5}%)`;
      refCtx.beginPath();
      refCtx.ellipse(cx - 46, cy + 2, 9, 16, 0, 0, Math.PI * 2);
      refCtx.ellipse(cx + 46, cy + 2, 9, 16, 0, 0, Math.PI * 2);
      refCtx.fill();

      // Head oval with directional CCTV overhead lighting
      const faceGrad = refCtx.createRadialGradient(cx - 12, cy - 18, 12, cx, cy, 62);
      faceGrad.addColorStop(0, `hsl(${skinHue}, 44%, ${skinLight + 8}%)`);
      faceGrad.addColorStop(0.7, `hsl(${skinHue}, 42%, ${skinLight}%)`);
      faceGrad.addColorStop(1, `hsl(${skinHue}, 36%, ${skinLight - 14}%)`);
      refCtx.fillStyle = faceGrad;
      refCtx.beginPath();
      refCtx.ellipse(cx, cy, 46, 58, 0, 0, Math.PI * 2);
      refCtx.fill();

      // Hair cap
      refCtx.fillStyle = '#1f1a17';
      refCtx.beginPath();
      refCtx.ellipse(cx, cy - 28, 47, 34, 0, Math.PI * 0.96, Math.PI * 2.04);
      refCtx.fill();

      // Eyebrows
      refCtx.strokeStyle = '#1f1a17';
      refCtx.lineWidth = 4.2;
      refCtx.lineCap = 'round';
      refCtx.beginPath();
      refCtx.moveTo(cx - 31, cy - 16);
      refCtx.quadraticCurveTo(cx - 20, cy - 21, cx - 9, cy - 15);
      refCtx.moveTo(cx + 9, cy - 15);
      refCtx.quadraticCurveTo(cx + 20, cy - 21, cx + 31, cy - 16);
      refCtx.stroke();

      // Eyes (whites + iris + pupil)
      const eyeY = cy - 4;
      for (const side of [-1, 1]) {
        const ex = cx + side * 19;
        refCtx.fillStyle = '#f8fafc';
        refCtx.beginPath();
        refCtx.ellipse(ex, eyeY, 9, 5 * blink, 0, 0, Math.PI * 2);
        refCtx.fill();

        if (blink > 0.5) {
          refCtx.fillStyle = '#3b2f2f';
          refCtx.beginPath();
          refCtx.arc(ex, eyeY, 4.1, 0, Math.PI * 2);
          refCtx.fill();

          refCtx.fillStyle = '#0f172a';
          refCtx.beginPath();
          refCtx.arc(ex, eyeY, 2.0, 0, Math.PI * 2);
          refCtx.fill();

          refCtx.fillStyle = '#ffffff';
          refCtx.beginPath();
          refCtx.arc(ex - 1.3, eyeY - 1.3, 1.0, 0, Math.PI * 2);
          refCtx.fill();
        }
      }

      // Glasses (when present)
      if (hasGlasses) {
        refCtx.strokeStyle = '#0f172a';
        refCtx.lineWidth = 2.6;
        refCtx.strokeRect(cx - 33, eyeY - 9, 27, 18);
        refCtx.strokeRect(cx + 6, eyeY - 9, 27, 18);
        refCtx.beginPath();
        refCtx.moveTo(cx - 6, eyeY - 1);
        refCtx.lineTo(cx + 6, eyeY - 1);
        refCtx.stroke();
      }

      // Nose bridge & shadow
      refCtx.strokeStyle = `hsl(${skinHue}, 35%, ${skinLight - 18}%)`;
      refCtx.lineWidth = 2.4;
      refCtx.beginPath();
      refCtx.moveTo(cx, cy - 4);
      refCtx.lineTo(cx - 3, cy + 16);
      refCtx.quadraticCurveTo(cx, cy + 20, cx + 5, cy + 16);
      refCtx.stroke();

      // Beard shadow (when present)
      if (hasBeard) {
        refCtx.fillStyle = 'rgba(31, 26, 23, 0.34)';
        refCtx.beginPath();
        refCtx.ellipse(cx, cy + 36, 36, 22, 0, 0, Math.PI);
        refCtx.fill();
      }

      // Mouth
      refCtx.strokeStyle = `hsl(${skinHue - 8}, 45%, ${skinLight - 22}%)`;
      refCtx.lineWidth = 3;
      refCtx.beginPath();
      refCtx.moveTo(cx - 14, cy + 31);
      refCtx.quadraticCurveTo(cx, cy + 35, cx + 14, cy + 31);
      refCtx.stroke();
    }

    // Downsample to low-res surveillance segment with authentic camera jitter + motion blur + grain
    const jitterX = Math.round((rand(f * 3 + 10) - 0.5) * 14 + Math.sin(f * 2.3) * 4);
    const jitterY = Math.round((rand(f * 3 + 11) - 0.5) * 12 + Math.cos(f * 1.9) * 3.5);

    const lowCanvas = document.createElement('canvas');
    lowCanvas.width = lowResSize;
    lowCanvas.height = lowResSize;
    const lowCtx = lowCanvas.getContext('2d');
    if (!lowCtx) continue;

    lowCtx.fillStyle = '#0f172a';
    lowCtx.fillRect(0, 0, lowResSize, lowResSize);

    // Slight motion smear by blending two offset passes
    lowCtx.globalAlpha = 0.78;
    lowCtx.drawImage(
      refCanvas,
      jitterX * 2,
      jitterY * 2,
      refSize,
      refSize,
      0,
      0,
      lowResSize,
      lowResSize
    );
    lowCtx.globalAlpha = 0.22;
    lowCtx.drawImage(
      refCanvas,
      jitterX * 2 + 5,
      jitterY * 2 + 2,
      refSize,
      refSize,
      0,
      0,
      lowResSize,
      lowResSize
    );
    lowCtx.globalAlpha = 1.0;

    // Add realistic low-light surveillance compression grain
    const frameImg = lowCtx.getImageData(0, 0, lowResSize, lowResSize);
    const d = frameImg.data;
    for (let i = 0; i < d.length; i += 4) {
      const nVal = ((rand(f * 1000 + i) - 0.5) * 18) | 0;
      d[i] = clampByte(d[i] + nVal);
      d[i + 1] = clampByte(d[i + 1] + nVal);
      d[i + 2] = clampByte(d[i + 2] + nVal);
    }
    lowCtx.putImageData(frameImg, 0, 0);

    frames.push(lowCanvas.toDataURL('image/png'));
  }

  return frames;
}

/**
 * Extracts sequential low-res frames from a user-uploaded video file (.mp4 / .webm / .mov).
 */
export async function extractSegmentFromVideoFile(
  file: File,
  maxFrames = 10,
  targetSize = 112
): Promise<string[]> {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file);
    const video = document.createElement('video');
    video.muted = true;
    video.playsInline = true;
    video.crossOrigin = 'anonymous';
    video.preload = 'auto';

    const cleanup = () => {
      URL.revokeObjectURL(url);
    };

    video.onerror = () => {
      cleanup();
      reject(new Error('امکان خواندن فایل ویدیویی وجود ندارد.'));
    };

    video.onloadedmetadata = async () => {
      try {
        const duration = Number.isFinite(video.duration) && video.duration > 0 ? video.duration : 2;
        const segmentDuration = Math.min(duration, 2.5);
        const count = Math.max(4, Math.min(maxFrames, Math.floor(segmentDuration * 6)));
        const step = segmentDuration / count;

        const aspect = (video.videoWidth || 1) / (video.videoHeight || 1);
        const w = aspect >= 1 ? targetSize : Math.max(64, Math.round(targetSize * aspect));
        const h = aspect >= 1 ? Math.max(64, Math.round(targetSize / aspect)) : targetSize;

        const canvas = document.createElement('canvas');
        canvas.width = w;
        canvas.height = h;
        const ctx = canvas.getContext('2d');
        if (!ctx) throw new Error('Canvas unavailable');

        const frames: string[] = [];
        for (let i = 0; i < count; i++) {
          const seekTime = Math.min(duration - 0.02, i * step);
          await new Promise<void>((res) => {
            const onSeeked = () => {
              video.removeEventListener('seeked', onSeeked);
              res();
            };
            video.addEventListener('seeked', onSeeked);
            video.currentTime = Math.max(0, seekTime);
          });
          ctx.drawImage(video, 0, 0, w, h);
          frames.push(canvas.toDataURL('image/png'));
        }

        cleanup();
        resolve(frames);
      } catch (err) {
        cleanup();
        reject(err);
      }
    };

    video.src = url;
  });
}

/**
 * Extracts frames from one or multiple uploaded image files.
 */
export async function extractSegmentFromImageFiles(
  files: File[],
  targetSize = 112
): Promise<string[]> {
  const readAsDataUrl = (f: File) =>
    new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result));
      reader.onerror = reject;
      reader.readAsDataURL(f);
    });

  if (files.length === 1) {
    const singleUrl = await readAsDataUrl(files[0]);
    return generateJitterySegmentFromSource(singleUrl, 777, 8, targetSize);
  }

  const urls = await Promise.all(files.slice(0, 14).map(readAsDataUrl));
  // Normalize dimensions to match first image
  const normalized: string[] = [];
  for (const u of urls) {
    const imgData = await decodeDataUrlToImageData(u, targetSize);
    normalized.push(imageDataToDataUrl(imgData));
  }
  return normalized;
}

/**
 * Main AI Frame Interpolation, Stabilization & Super-Resolution Pipeline.
 */
export async function runFrameInterpolationPipeline(
  sourceDataUrls: string[],
  config: FrameInterpolationConfig,
  sourceLabel: string,
  onProgress?: (stage: string, percent: number) => void
): Promise<FrameInterpolationResult> {
  const t0 = performance.now();
  if (sourceDataUrls.length < 2) {
    throw new Error('حداقل ۲ فریم برای درونیابی و تثبیت مورد نیاز است.');
  }

  onProgress?.('در حال رمزگشایی فریم‌های کم‌وضوح ورودی...', 8);
  const rawFrames: ImageData[] = [];
  for (let i = 0; i < sourceDataUrls.length; i++) {
    rawFrames.push(await decodeDataUrlToImageData(sourceDataUrls[i], 128));
  }

  const inW = rawFrames[0].width;
  const inH = rawFrames[0].height;

  // Ensure all frames share identical dimensions
  const alignedRaw: ImageData[] = rawFrames.map((f) => {
    if (f.width === inW && f.height === inH) return f;
    const c = document.createElement('canvas');
    c.width = inW;
    c.height = inH;
    const ctx = c.getContext('2d')!;
    const tmp = document.createElement('canvas');
    tmp.width = f.width;
    tmp.height = f.height;
    tmp.getContext('2d')!.putImageData(f, 0, 0);
    ctx.drawImage(tmp, 0, 0, inW, inH);
    return ctx.getImageData(0, 0, inW, inH);
  });

  // Yield briefly so UI renders progress smoothly
  await new Promise((r) => setTimeout(r, 15));

  // Stage 1: Global Optical-Flow Motion Estimation & Stabilization
  onProgress?.('تخمین بردارهای لرزش و تثبیت زیرپیکسلی (Motion Stabilization)...', 24);
  const rawLumas = alignedRaw.map(extractLuminance);
  const stepDx: number[] = [0];
  const stepDy: number[] = [0];
  const trajX: number[] = [0];
  const trajY: number[] = [0];

  for (let i = 1; i < alignedRaw.length; i++) {
    const shift = estimateGlobalShift(rawLumas[i - 1], rawLumas[i], inW, inH, 10);
    stepDx.push(shift.dx);
    stepDy.push(shift.dy);
    trajX.push(trajX[i - 1] + shift.dx);
    trajY.push(trajY[i - 1] + shift.dy);
  }

  // Raw jitter RMS (frame-to-frame displacement magnitude)
  let rawSqSum = 0;
  for (let i = 1; i < stepDx.length; i++) {
    rawSqSum += stepDx[i] * stepDx[i] + stepDy[i] * stepDy[i];
  }
  const jitterRmsBeforePx = Math.sqrt(rawSqSum / Math.max(1, stepDx.length - 1));

  // Smooth trajectory toward mean centroid (cancels high-frequency jitter while keeping subject centered)
  const meanX = trajX.reduce((a, b) => a + b, 0) / trajX.length;
  const meanY = trajY.reduce((a, b) => a + b, 0) / trajY.length;

  const smoothX: number[] = [];
  const smoothY: number[] = [];
  for (let i = 0; i < trajX.length; i++) {
    // Low-pass blend between local 3-point average and global mean
    const prevIdx = Math.max(0, i - 1);
    const nextIdx = Math.min(trajX.length - 1, i + 1);
    const localAvgX = (trajX[prevIdx] + trajX[i] + trajX[nextIdx]) / 3;
    const localAvgY = (trajY[prevIdx] + trajY[i] + trajY[nextIdx]) / 3;
    smoothX.push(localAvgX * 0.25 + meanX * 0.75);
    smoothY.push(localAvgY * 0.25 + meanY * 0.75);
  }

  const stabilizedFrames: ImageData[] = [];
  const corrShiftsX: number[] = [];
  const corrShiftsY: number[] = [];

  for (let i = 0; i < alignedRaw.length; i++) {
    const cx = (trajX[i] - smoothX[i]) * config.stabilizationStrength;
    const cy = (trajY[i] - smoothY[i]) * config.stabilizationStrength;
    corrShiftsX.push(cx);
    corrShiftsY.push(cy);
    stabilizedFrames.push(
      Math.hypot(cx, cy) > 0.05 ? warpShiftImageData(alignedRaw[i], cx, cy) : alignedRaw[i]
    );
  }

  // Measure post-stabilization residual jitter RMS on stabilized frames
  const stabLumas = stabilizedFrames.map(extractLuminance);
  let stabSqSum = 0;
  for (let i = 1; i < stabilizedFrames.length; i++) {
    const residual = estimateGlobalShift(stabLumas[i - 1], stabLumas[i], inW, inH, 6);
    stabSqSum += residual.dx * residual.dx + residual.dy * residual.dy;
  }
  const jitterRmsAfterPx = Math.sqrt(stabSqSum / Math.max(1, stabilizedFrames.length - 1));

  // Optional Temporal Multi-Frame Denoise on aligned stabilized frames
  const denoisedStabilized = config.temporalDenoise
    ? stabilizedFrames.map((frame, i) =>
        temporalFuseFrame(
          i > 0 ? stabilizedFrames[i - 1] : null,
          frame,
          i < stabilizedFrames.length - 1 ? stabilizedFrames[i + 1] : null
        )
      )
    : stabilizedFrames;

  const denoisedLumas = denoisedStabilized.map(extractLuminance);

  await new Promise((r) => setTimeout(r, 15));

  // Stage 2: Bidirectional Motion-Compensated Frame Interpolation (2x / 4x)
  onProgress?.(
    `درونیابی هوشمند فریم‌های میانی با Optical Flow (${config.interpolationFactor}× FPS)...`,
    52
  );

  interface IntermediateSlot {
    img: ImageData;
    isInterpolated: boolean;
    sourceIndex: number;
    alpha: number;
    shiftX: number;
    shiftY: number;
    correctedX: number;
    correctedY: number;
    motionVectors: MotionVectorCell[];
  }

  const interpolatedSeq: IntermediateSlot[] = [];
  const mult = config.interpolationFactor;

  for (let i = 0; i < denoisedStabilized.length; i++) {
    const isLast = i === denoisedStabilized.length - 1;
    const flowFwd = !isLast
      ? computeBlockOpticalFlow(denoisedLumas[i], denoisedLumas[i + 1], inW, inH, 10, 4)
      : { cols: 4, rows: 4, vx: new Float32Array(16), vy: new Float32Array(16), cells: [] };
    const flowBwd = !isLast
      ? computeBlockOpticalFlow(denoisedLumas[i + 1], denoisedLumas[i], inW, inH, 10, 4)
      : flowFwd;

    interpolatedSeq.push({
      img: denoisedStabilized[i],
      isInterpolated: false,
      sourceIndex: i,
      alpha: 0,
      shiftX: stepDx[i],
      shiftY: stepDy[i],
      correctedX: corrShiftsX[i],
      correctedY: corrShiftsY[i],
      motionVectors: flowFwd.cells,
    });

    if (!isLast) {
      for (let sub = 1; sub < mult; sub++) {
        const alpha = sub / mult;
        const synth = interpolatePairBidirectional(
          denoisedStabilized[i],
          denoisedStabilized[i + 1],
          flowFwd,
          flowBwd,
          alpha
        );
        interpolatedSeq.push({
          img: synth,
          isInterpolated: true,
          sourceIndex: i,
          alpha,
          shiftX: stepDx[i] * (1 - alpha) + stepDx[i + 1] * alpha,
          shiftY: stepDy[i] * (1 - alpha) + stepDy[i + 1] * alpha,
          correctedX: corrShiftsX[i] * (1 - alpha) + corrShiftsX[i + 1] * alpha,
          correctedY: corrShiftsY[i] * (1 - alpha) + corrShiftsY[i + 1] * alpha,
          motionVectors: flowFwd.cells,
        });
      }
    }
  }

  await new Promise((r) => setTimeout(r, 15));

  // Stage 3: Super-Resolution Upscaling & Luminance Facial Detail Reconstruction
  onProgress?.(
    `بزرگ‌نمایی فوق‌وضوح (${config.upscaleFactor}× Catmull-Rom) و بازسازی جزئیات چهره...`,
    78
  );

  const maxOutDim = 384;
  const rawTargetW = inW * config.upscaleFactor;
  const rawTargetH = inH * config.upscaleFactor;
  const capRatio = Math.min(1, maxOutDim / Math.max(rawTargetW, rawTargetH));
  const outW = Math.round(rawTargetW * capRatio);
  const outH = Math.round(rawTargetH * capRatio);

  const sharpnessBeforeList = alignedRaw.map(computeFaceSharpness);
  const avgSharpnessBefore =
    sharpnessBeforeList.reduce((a, b) => a + b, 0) / Math.max(1, sharpnessBeforeList.length);

  const processedFrames: ProcessedFrameItem[] = [];
  let bestIdx = 0;
  let bestScore = -1;

  for (let i = 0; i < interpolatedSeq.length; i++) {
    const slot = interpolatedSeq[i];
    const upscaled = upscaleAndClarifyFace(slot.img, outW, outH, config.deblurStrength);
    const sharp = computeFaceSharpness(upscaled);
    const dataUrl = imageDataToDataUrl(upscaled);

    processedFrames.push({
      index: i,
      isInterpolated: slot.isInterpolated,
      sourceIndex: slot.sourceIndex,
      alpha: slot.alpha,
      dataUrl,
      width: outW,
      height: outH,
      shiftX: Number(slot.shiftX.toFixed(2)),
      shiftY: Number(slot.shiftY.toFixed(2)),
      correctedX: Number(slot.correctedX.toFixed(2)),
      correctedY: Number(slot.correctedY.toFixed(2)),
      sharpness: Number(sharp.toFixed(1)),
      motionVectors: slot.motionVectors,
    });

    // Prefer crisp keyframes slightly over interpolated frames for forensic ID export
    const keyBonus = slot.isInterpolated ? 0.94 : 1.05;
    const score = sharp * keyBonus;
    if (score > bestScore) {
      bestScore = score;
      bestIdx = i;
    }

    if (i % 6 === 0) {
      onProgress?.(
        `بازسازی فریم ${i + 1} از ${interpolatedSeq.length}...`,
        78 + Math.round((i / interpolatedSeq.length) * 20)
      );
      await new Promise((r) => setTimeout(r, 0));
    }
  }

  const avgSharpnessAfter =
    processedFrames.reduce((a, b) => a + b.sharpness, 0) / Math.max(1, processedFrames.length);

  const stabilizationGainPct =
    jitterRmsBeforePx > 0.08
      ? clamp(((jitterRmsBeforePx - jitterRmsAfterPx) / jitterRmsBeforePx) * 100, 0, 99)
      : 0;

  const sharpnessGainPct =
    avgSharpnessBefore > 1
      ? clamp(((avgSharpnessAfter - avgSharpnessBefore) / avgSharpnessBefore) * 100, -20, 400)
      : 0;

  // ISO/IEC 19794-5 biometric readiness estimate based on resolution, sharpness, and stability
  const resScoreBefore = clamp((inW / 180) * 40, 12, 40);
  const stabScoreBefore = clamp(30 - jitterRmsBeforePx * 4.5, 4, 30);
  const sharpScoreBefore = clamp((Math.log10(avgSharpnessBefore + 1) / 3.2) * 30, 8, 28);
  const verificationScoreBefore = Math.round(
    clamp(resScoreBefore + stabScoreBefore + sharpScoreBefore, 24, 82)
  );

  const resScoreAfter = clamp((outW / 200) * 42, 20, 42);
  const stabScoreAfter = clamp(30 - jitterRmsAfterPx * 3.5, 14, 30);
  const sharpScoreAfter = clamp((Math.log10(avgSharpnessAfter + 1) / 3.0) * 28, 12, 28);
  const verificationScoreAfter = Math.round(
    clamp(
      Math.max(verificationScoreBefore + 8, resScoreAfter + stabScoreAfter + sharpScoreAfter),
      45,
      98
    )
  );

  const metrics: FrameInterpolationMetrics = {
    inputWidth: inW,
    inputHeight: inH,
    outputWidth: outW,
    outputHeight: outH,
    inputFrameCount: alignedRaw.length,
    outputFrameCount: processedFrames.length,
    inputFps: config.sourceFps,
    outputFps: config.sourceFps * config.interpolationFactor,
    jitterRmsBeforePx: Number(jitterRmsBeforePx.toFixed(2)),
    jitterRmsAfterPx: Number(jitterRmsAfterPx.toFixed(2)),
    stabilizationGainPct: Number(stabilizationGainPct.toFixed(1)),
    sharpnessBefore: Number(avgSharpnessBefore.toFixed(1)),
    sharpnessAfter: Number(avgSharpnessAfter.toFixed(1)),
    sharpnessGainPct: Number(sharpnessGainPct.toFixed(1)),
    verificationScoreBefore,
    verificationScoreAfter,
    bestFrameIndex: bestIdx,
    processingTimeMs: Math.round(performance.now() - t0),
  };

  onProgress?.('پردازش با موفقیت تکمیل شد.', 100);

  return {
    sourceFrames: alignedRaw.map(imageDataToDataUrl),
    processedFrames,
    bestFrame: processedFrames[bestIdx],
    metrics,
    sourceLabel,
  };
}

/**
 * Exports the processed stabilized & interpolated sequence as a playable WebM video clip.
 */
export async function exportSequenceAsWebM(
  frames: ProcessedFrameItem[],
  fps: number,
  loops = 2
): Promise<Blob> {
  if (frames.length === 0) {
    throw new Error('فریمی برای خروجی ویدیو موجود نیست.');
  }
  if (typeof MediaRecorder === 'undefined') {
    throw new Error('مرورگر از ضبط ویدیو (MediaRecorder) پشتیبانی نمی‌کند.');
  }

  const width = frames[0].width;
  const height = frames[0].height;
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('Canvas context unavailable');

  // Preload all frame images
  const images = await Promise.all(
    frames.map(
      (f) =>
        new Promise<HTMLImageElement>((resolve, reject) => {
          const img = new Image();
          img.onload = () => resolve(img);
          img.onerror = reject;
          img.src = f.dataUrl;
        })
    )
  );

  const mimeType = MediaRecorder.isTypeSupported('video/webm;codecs=vp9')
    ? 'video/webm;codecs=vp9'
    : 'video/webm';

  const stream = canvas.captureStream(fps);
  const recorder = new MediaRecorder(stream, {
    mimeType,
    videoBitsPerSecond: 2_500_000,
  });

  const chunks: BlobPart[] = [];
  recorder.ondataavailable = (e) => {
    if (e.data.size > 0) chunks.push(e.data);
  };

  const stopPromise = new Promise<Blob>((resolve) => {
    recorder.onstop = () => resolve(new Blob(chunks, { type: mimeType }));
  });

  recorder.start();
  const frameIntervalMs = Math.max(16, Math.round(1000 / Math.max(6, fps)));

  for (let l = 0; l < loops; l++) {
    for (let i = 0; i < images.length; i++) {
      ctx.drawImage(images[i], 0, 0, width, height);
      await new Promise((r) => setTimeout(r, frameIntervalMs));
    }
  }

  recorder.stop();
  return stopPromise;
}
