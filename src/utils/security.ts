/**
 * Security and credential sanitization utilities.
 * Ensures camera credentials and passwords are NEVER exposed in frontend code,
 * screen displays, console logs, or exported test artifacts.
 */

export const CREDENTIAL_ENV_USER = '$CAM_USER';
export const CREDENTIAL_ENV_PASS = '$CAM_PASS';
export const MASKED_PASS = '••••••••';

/**
 * Sanitizes an RTSP URL by replacing the password with asterisks or variable placeholder.
 */
export function sanitizeRtspUrl(
  url: string,
  mode: 'masked' | 'env_var' | 'redacted' = 'masked'
): string {
  if (!url) return '';

  // Match rtsp://[user]:[pass]@[host]:[port]/[path]
  const rtspRegex = /^(rtsp:\/\/)([^:]+):([^@]+)@(.+)$/i;
  const match = url.match(rtspRegex);

  if (!match) {
    return url;
  }

  const [, protocol, user, , rest] = match;

  if (mode === 'masked') {
    return `${protocol}${user}:${MASKED_PASS}@${rest}`;
  } else if (mode === 'env_var') {
    return `${protocol}${CREDENTIAL_ENV_USER}:${CREDENTIAL_ENV_PASS}@${rest}`;
  } else {
    // redacted entirely
    return `${protocol}***:***@${rest}`;
  }
}

/**
 * Sanitizes a full shell command string (FFmpeg, FFprobe, curl, VLC)
 * to prevent plain-text credentials in logs or UI copy elements.
 */
export function sanitizeCommandString(command: string): string {
  if (!command) return '';
  return command
    .replace(/(rtsp:\/\/[a-zA-Z0-9_\-\.]+):([^@\s"']+)@/g, `$1:${MASKED_PASS}@`)
    .replace(/(-pass|-password|--password)\s+([^\s"']+)/gi, `$1 ${MASKED_PASS}`)
    .replace(/(user:)([^@\s"']+)/gi, `$1${MASKED_PASS}`);
}

/**
 * Builds an RTSP URL with standard options.
 */
export function buildRtspUrl(params: {
  host: string;
  port?: number;
  user?: string;
  pass?: string;
  path: string;
  safeForDisplay?: boolean;
}): string {
  const { host, port = 554, user = 'admin', pass = '', path, safeForDisplay = true } = params;
  const cleanPath = path.startsWith('/') ? path : `/${path}`;
  const displayPass = safeForDisplay ? MASKED_PASS : pass || 'password';
  const portPart = port === 554 ? '' : `:${port}`;

  if (!user && !pass) {
    return `rtsp://${host}${portPart}${cleanPath}`;
  }

  return `rtsp://${user}:${displayPass}@${host}${portPart}${cleanPath}`;
}
