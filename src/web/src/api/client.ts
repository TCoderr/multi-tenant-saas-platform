// API istemcisi: aynı origin, cookie oturumu, CSRF başlığı, ProblemDetails ayrıştırma, ETag/If-Match ve Idempotency-Key.
// Tarayıcı deposu kullanılmaz; CSRF token'ı yalnız bellekte tutulur ve kimlik değişince (login/logout) yeniden alınır.

export const CSRF_HEADER = 'X-CSRF-TOKEN';

export interface ProblemBody {
  type?: string;
  title?: string;
  status?: number;
  code?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
  [key: string]: unknown;
}

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly title: string;
  readonly traceId: string | null;
  readonly fieldErrors: Record<string, string[]>;
  readonly extra: Record<string, unknown>;
  readonly retryAfterSeconds: number | null;

  constructor(status: number, problem: ProblemBody, retryAfterSeconds: number | null = null) {
    const code = typeof problem.code === 'string' ? problem.code : status === 0 ? 'network_error' : 'unknown_error';
    const title = typeof problem.title === 'string' ? problem.title : 'İstek tamamlanamadı.';
    super(`${status} ${code}`);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.title = title;
    this.traceId = typeof problem.traceId === 'string' ? problem.traceId : null;
    this.fieldErrors = isFieldErrors(problem.errors) ? problem.errors : {};
    this.extra = problem;
    this.retryAfterSeconds = retryAfterSeconds;
  }
}

function isFieldErrors(value: unknown): value is Record<string, string[]> {
  return (
    typeof value === 'object' &&
    value !== null &&
    Object.values(value).every((entry) => Array.isArray(entry) && entry.every((item) => typeof item === 'string'))
  );
}

export function isApiError(error: unknown, code?: string): error is ApiError {
  return error instanceof ApiError && (code === undefined || error.code === code);
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  body?: unknown;
  /** Kaydın bilinen sürümü; If-Match başlığına tırnaklı olarak yazılır. */
  ifMatch?: number | null;
  idempotencyKey?: string;
  signal?: AbortSignal;
}

export interface ApiResponse<T> {
  data: T;
  /** Yanıtın ETag başlığındaki sürüm (yoksa null). */
  version: number | null;
  status: number;
}

type UnauthorizedHandler = () => void;

let csrfToken: string | null = null;
let csrfPending: Promise<string> | null = null;
let unauthorizedHandler: UnauthorizedHandler | null = null;

/** Oturum sağlayıcı kaydeder: herhangi bir istekte 401 gelirse önbellek temizlenir ve girişe gidilir. */
export function setUnauthorizedHandler(handler: UnauthorizedHandler | null): void {
  unauthorizedHandler = handler;
}

/** CSRF token'ı kimliğe bağlıdır: login ve logout sonrasında atılır, sonraki yazma isteğinde yeniden alınır. */
export function resetCsrf(): void {
  csrfToken = null;
  csrfPending = null;
}

async function fetchCsrf(): Promise<string> {
  const response = await fetch('/api/auth/csrf', { credentials: 'same-origin', headers: { Accept: 'application/json' } });
  if (!response.ok) {
    throw await toError(response);
  }
  const body = (await response.json()) as { token?: string };
  if (typeof body.token !== 'string' || body.token.length === 0) {
    throw new ApiError(response.status, { code: 'csrf_unavailable', title: 'Güvenlik belirteci alınamadı.' });
  }
  return body.token;
}

function ensureCsrf(): Promise<string> {
  if (csrfToken !== null) {
    return Promise.resolve(csrfToken);
  }
  csrfPending ??= fetchCsrf().then(
    (token) => {
      csrfToken = token;
      csrfPending = null;
      return token;
    },
    (error: unknown) => {
      csrfPending = null;
      throw error;
    },
  );
  return csrfPending;
}

async function toError(response: Response): Promise<ApiError> {
  let problem: ProblemBody = {};
  const contentType = response.headers.get('Content-Type') ?? '';
  if (contentType.includes('json')) {
    try {
      problem = (await response.json()) as ProblemBody;
    } catch {
      problem = {};
    }
  }
  const retryAfterRaw = response.headers.get('Retry-After');
  const retryAfter = retryAfterRaw !== null && /^\d+$/.test(retryAfterRaw) ? Number(retryAfterRaw) : null;
  return new ApiError(response.status, problem, retryAfter);
}

export function parseETag(raw: string | null): number | null {
  if (raw === null) {
    return null;
  }
  const match = /^(?:W\/)?"(\d+)"$/.exec(raw.trim());
  return match?.[1] !== undefined ? Number(match[1]) : null;
}

async function send(path: string, options: RequestOptions, token: string | null): Promise<Response> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }
  if (token !== null) {
    headers[CSRF_HEADER] = token;
  }
  if (options.ifMatch !== undefined && options.ifMatch !== null) {
    headers['If-Match'] = `"${options.ifMatch}"`;
  }
  if (options.idempotencyKey !== undefined) {
    headers['Idempotency-Key'] = options.idempotencyKey;
  }
  try {
    return await fetch(path, {
      method: options.method ?? 'GET',
      credentials: 'same-origin',
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
    });
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error;
    }
    throw new ApiError(0, { code: 'network_error', title: 'Sunucuya ulaşılamadı. Bağlantınızı denetleyin.' });
  }
}

/**
 * Tek giriş noktası. Güvensiz yöntemlerde CSRF token'ı eklenir; sunucu "csrf_invalid" derse token bir kez yenilenip
 * istek aynı gövde ve aynı Idempotency-Key ile yinelenir. 401 "unauthenticated" oturum sağlayıcıya bildirilir.
 */
export async function request<T>(path: string, options: RequestOptions = {}): Promise<ApiResponse<T>> {
  const method = options.method ?? 'GET';
  const unsafe = method !== 'GET';
  let response = await send(path, options, unsafe ? await ensureCsrf() : null);

  if (unsafe && response.status === 400) {
    const first = await toError(response);
    if (first.code !== 'csrf_invalid') {
      throw first;
    }
    resetCsrf();
    response = await send(path, options, await ensureCsrf());
  }

  if (!response.ok) {
    const error = await toError(response);
    if (error.status === 401 && error.code !== 'invalid_credentials') {
      unauthorizedHandler?.();
    }
    throw error;
  }

  const version = parseETag(response.headers.get('ETag'));
  if (response.status === 204 || response.status === 205) {
    return { data: undefined as T, version, status: response.status };
  }
  const contentType = response.headers.get('Content-Type') ?? '';
  if (!contentType.includes('json')) {
    return { data: undefined as T, version, status: response.status };
  }
  const text = await response.text();
  return { data: (text.length === 0 ? undefined : JSON.parse(text)) as T, version, status: response.status };
}

export async function get<T>(path: string, signal?: AbortSignal): Promise<T> {
  return (await request<T>(path, { signal })).data;
}

export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}

/** Sorgu dizesi kurar; boş değerler atılır (sunucu bilinmeyen/boş parametreyi reddeder). */
export function withQuery(path: string, params: Record<string, string | number | null | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== null && value !== undefined && value !== '') {
      search.set(key, String(value));
    }
  }
  const query = search.toString();
  return query.length === 0 ? path : `${path}?${query}`;
}
