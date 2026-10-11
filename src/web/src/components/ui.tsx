// Ortak arayüz parçaları. Kullanıcı metni her zaman React metin düğümü olarak basılır (HTML enjeksiyonu yok).

import {
  useEffect,
  useId,
  useRef,
  useState,
  useSyncExternalStore,
  type ChangeEvent,
  type FormEvent,
  type MouseEvent,
  type ReactNode,
  type Ref,
} from 'react';
import { Link, useLocation } from 'react-router-dom';
import { ApiError } from '../api/client';
import { permissionRequirement, type Tone } from '../lib/format';

const PRODUCT = 'Kulvar';

/* ---------- Başlık: belge başlığı + sayfa değişiminde odak ---------- */

export interface PageHeaderProps {
  title: string;
  /** Şirket adı gibi bağlam; belge başlığına ve başlığın üstüne yazılır. */
  context?: string;
  actions?: ReactNode;
  children?: ReactNode;
}

export function PageHeader({ title, context, actions, children }: PageHeaderProps) {
  const heading = useRef<HTMLHeadingElement>(null);
  const location = useLocation();

  useEffect(() => {
    document.title = [title, context, PRODUCT].filter((part) => part !== undefined && part.length > 0).join(' · ');
  }, [title, context]);

  useEffect(() => {
    // İlk yüklemede odak belgenin başında kalır ("içeriğe atla" bağlantısı çalışsın); sonraki her geçişte ana başlığa taşınır.
    if (location.key !== 'default') {
      heading.current?.focus();
    }
  }, [location.key, location.pathname]);

  return (
    <header className="page-head">
      <div className="grow">
        {context !== undefined && <div className="page-head__context">{context}</div>}
        <h1 ref={heading} tabIndex={-1}>
          {title}
        </h1>
        {children}
      </div>
      {actions !== undefined && <div className="row">{actions}</div>}
    </header>
  );
}

/* ---------- Düğmeler ---------- */

type ButtonVariant = 'default' | 'primary' | 'danger' | 'link';

export interface ButtonProps {
  children: ReactNode;
  variant?: ButtonVariant;
  small?: boolean;
  type?: 'button' | 'submit';
  /** Dolu ise düğme devre dışıdır ve neden görünür metin olarak yanında yazar (gizlenmez). */
  disabledReason?: string | null;
  busy?: boolean;
  onClick?: () => void;
  ariaLabel?: string;
  buttonRef?: Ref<HTMLButtonElement>;
}

function buttonClass(variant: ButtonVariant, small: boolean | undefined): string {
  return ['btn', variant !== 'default' ? `btn--${variant}` : '', small === true ? 'btn--small' : ''].filter(Boolean).join(' ');
}

export function Button({ children, variant = 'default', small, type = 'button', disabledReason, busy, onClick, ariaLabel, buttonRef }: ButtonProps) {
  const reasonId = useId();
  const blocked = (disabledReason !== undefined && disabledReason !== null && disabledReason.length > 0) || busy === true;
  const handleClick = (event: MouseEvent<HTMLButtonElement>) => {
    if (blocked) {
      event.preventDefault();
      return;
    }
    onClick?.();
  };
  const button = (
    <button
      ref={buttonRef}
      type={type}
      className={buttonClass(variant, small)}
      aria-disabled={blocked ? 'true' : undefined}
      aria-busy={busy === true ? 'true' : undefined}
      aria-describedby={disabledReason ? reasonId : undefined}
      aria-label={ariaLabel}
      onClick={handleClick}
    >
      {children}
    </button>
  );
  if (!disabledReason) {
    return button;
  }
  return (
    <span className="gate">
      {button}
      <span id={reasonId} className="gate__reason">
        {disabledReason}
      </span>
    </span>
  );
}

export interface LinkButtonProps {
  to: string;
  children: ReactNode;
  variant?: ButtonVariant;
  small?: boolean;
  disabledReason?: string | null;
}

export function LinkButton({ to, children, variant = 'default', small, disabledReason }: LinkButtonProps) {
  if (disabledReason) {
    return (
      <Button variant={variant} small={small} disabledReason={disabledReason}>
        {children}
      </Button>
    );
  }
  return (
    <Link to={to} className={buttonClass(variant, small)}>
      {children}
    </Link>
  );
}

/* ---------- Form alanları ---------- */

export interface FieldProps {
  label: string;
  name: string;
  value: string;
  onChange: (value: string) => void;
  as?: 'input' | 'textarea' | 'select';
  type?: 'text' | 'email' | 'password' | 'tel' | 'search';
  error?: string | null;
  hint?: string;
  required?: boolean;
  disabled?: boolean;
  autoComplete?: string;
  maxLength?: number;
  rows?: number;
  className?: string;
  /** `as="select"` için <option> öğeleri. */
  children?: ReactNode;
}

export function Field({
  label,
  name,
  value,
  onChange,
  as = 'input',
  type = 'text',
  error,
  hint,
  required,
  disabled,
  autoComplete,
  maxLength,
  rows,
  className,
  children,
}: FieldProps) {
  const id = useId();
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;
  const hasError = error !== undefined && error !== null && error.length > 0;
  const describedBy = [hint !== undefined ? hintId : null, hasError ? errorId : null].filter(Boolean).join(' ') || undefined;
  const common = {
    id,
    name,
    value,
    disabled,
    required,
    'aria-invalid': hasError ? ('true' as const) : undefined,
    'aria-describedby': describedBy,
    onChange: (event: ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>) => onChange(event.target.value),
  };
  return (
    <div className={['field', className].filter(Boolean).join(' ')}>
      <label className="field__label" htmlFor={id}>
        {label}
        {required !== true && as !== 'select' && type !== 'search' && <span className="field__optional"> (isteğe bağlı)</span>}
      </label>
      {as === 'textarea' ? (
        <textarea {...common} className="textarea" rows={rows ?? 5} maxLength={maxLength} />
      ) : as === 'select' ? (
        <select {...common} className="select">
          {children}
        </select>
      ) : (
        <input {...common} className="input" type={type} autoComplete={autoComplete ?? 'off'} maxLength={maxLength} />
      )}
      {hint !== undefined && (
        <span id={hintId} className="field__hint">
          {hint}
        </span>
      )}
      {hasError && (
        <span id={errorId} className="field__error">
          {error}
        </span>
      )}
    </div>
  );
}

export function ErrorSummary({ title, items }: { title: string; items?: string[] }) {
  return (
    <div className="note note--critical" role="alert">
      <p className="note__title">{title}</p>
      {items !== undefined && items.length > 0 && (
        <ul>
          {items.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** Sunucu hatasını form için ayrıştırır: alan hataları + özet metni. */
export function formErrors(error: unknown): { summary: string; fields: Record<string, string> } {
  if (!(error instanceof ApiError)) {
    return { summary: 'Beklenmeyen bir hata oluştu.', fields: {} };
  }
  const fields: Record<string, string> = {};
  for (const [field, messages] of Object.entries(error.fieldErrors)) {
    if (messages[0] !== undefined) {
      fields[field] = messages[0];
    }
  }
  return { summary: problemText(error), fields };
}

/** 403 access_mode_restricted: rol yeterli ama şirketin durumu (abonelik, silme, askı) işleme izin vermiyor. */
function accessModeText(mode: unknown): string {
  switch (mode) {
    case 'Recovery':
      return 'Abonelik askıda veya iptal edilmiş: içerik salt-okunur. Yazmaya devam etmek için bir plan seçilmelidir.';
    case 'ReadOnly':
      return 'Şirket için silme isteği bekliyor: içerik salt-okunur.';
    case 'Locked':
      return 'Şirket platform tarafından askıya alındı; içerik kapalı.';
    default:
      return 'Şirketin durumu bu işleme izin vermiyor.';
  }
}

export function problemText(error: unknown): string {
  if (!(error instanceof ApiError)) {
    return 'Beklenmeyen bir hata oluştu.';
  }
  if (error.code === 'rate_limited') {
    return error.retryAfterSeconds !== null
      ? `Çok fazla istek gönderildi. ${error.retryAfterSeconds} saniye sonra yeniden deneyin.`
      : 'Çok fazla istek gönderildi. Biraz sonra yeniden deneyin.';
  }
  if (error.code === 'access_mode_restricted') {
    return accessModeText(error.extra.accessMode);
  }
  if (error.code === 'forbidden') {
    const required = error.extra.requiredPermission;
    return typeof required === 'string' ? `Bu işlem için ${permissionRequirement(required)} gerekir.` : error.title;
  }
  if (error.code === 'tenant_not_found') {
    return 'Şirket bulunamadı veya bu şirkete erişiminiz kaldırıldı.';
  }
  return error.title;
}

/* ---------- Rozet, not, durumlar ---------- */

export function Badge({ tone = 'plain', children }: { tone?: Tone; children: ReactNode }) {
  return <span className={tone === 'plain' ? 'badge' : `badge badge--${tone}`}>{children}</span>;
}

export interface NoteProps {
  tone?: 'ok' | 'warn' | 'critical' | 'info';
  title?: string;
  children?: ReactNode;
  role?: 'alert' | 'status';
}

export function Note({ tone, title, children, role }: NoteProps) {
  return (
    <div className={tone !== undefined ? `note note--${tone}` : 'note'} role={role}>
      {title !== undefined && <p className="note__title">{title}</p>}
      {children}
    </div>
  );
}

/** İskelet 300 ms'den kısa yüklemelerde hiç görünmez; `aria-busy` baştan itibaren bildirilir. */
export function Loading({ label = 'Yükleniyor…' }: { label?: string }) {
  const [visible, setVisible] = useState(false);
  useEffect(() => {
    const timer = window.setTimeout(() => setVisible(true), 300);
    return () => window.clearTimeout(timer);
  }, []);
  return (
    <div className="skeleton" aria-busy="true" aria-live="polite">
      <span className="visually-hidden">{label}</span>
      {visible && (
        <>
          <div className="skeleton__row" />
          <div className="skeleton__row" />
          <div className="skeleton__row" />
        </>
      )}
    </div>
  );
}

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="state">
      <p className="state__title">{title}</p>
      {children}
    </div>
  );
}

export interface ErrorStateProps {
  error: unknown;
  onRetry?: () => void;
  /** 404 için özel metin ("Müşteri bulunamadı" gibi). */
  notFoundTitle?: string;
}

export function ErrorState({ error, onRetry, notFoundTitle }: ErrorStateProps) {
  const api = error instanceof ApiError ? error : null;
  let title = 'Bir şeyler ters gitti';
  let detail = api?.title ?? 'Beklenmeyen bir hata oluştu.';
  let canRetry = true;

  if (api?.code === 'access_mode_restricted') {
    title = 'Şirket şu anda bu işleme kapalı';
    detail = accessModeText(api.extra.accessMode);
    canRetry = false;
  } else if (api?.status === 403) {
    title = 'Bu içerik için yetkiniz yok';
    const required = api.extra.requiredPermission;
    detail = typeof required === 'string' ? `Gereken yetki: ${permissionRequirement(required)}.` : api.title;
    canRetry = false;
  } else if (api?.status === 404) {
    title = notFoundTitle ?? 'Kayıt bulunamadı';
    detail = api.code === 'tenant_not_found' ? 'Şirket bulunamadı veya bu şirkete erişiminiz yok.' : 'Aradığınız kayıt yok ya da silinmiş.';
    canRetry = false;
  } else if (api?.status === 429) {
    title = 'Çok fazla istek';
    detail = problemText(api);
  } else if (api?.status === 503) {
    title = 'Hizmet geçici olarak kullanılamıyor';
    detail = 'Verileriniz güvende. Biraz sonra yeniden deneyin.';
  } else if (api?.status === 0) {
    title = 'Sunucuya ulaşılamadı';
    detail = 'Bağlantınızı denetleyip yeniden deneyin.';
  }

  return (
    <div className="state" role="alert">
      <p className="state__title">{title}</p>
      <p>{detail}</p>
      {canRetry && onRetry !== undefined && <Button onClick={onRetry}>Yeniden dene</Button>}
      {api?.traceId != null && <p className="state__trace">İz kimliği: {api.traceId}</p>}
    </div>
  );
}

/* ---------- İletişim kutusu ---------- */

export interface DialogProps {
  open: boolean;
  title: string;
  children: ReactNode;
  onClose: () => void;
  onSubmit: () => void;
  confirmLabel: string;
  danger?: boolean;
  busy?: boolean;
  error?: string | null;
}

/** Yerel <dialog>: odak tuzağı, Esc ile kapanma ve odağın geri dönüşü tarayıcı tarafından sağlanır. */
export function Dialog({ open, title, children, onClose, onSubmit, confirmLabel, danger, busy, error }: DialogProps) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const element = ref.current;
    if (element === null) {
      return;
    }
    if (open && !element.open) {
      if (typeof element.showModal === 'function') {
        element.showModal();
      } else {
        element.setAttribute('open', '');
      }
    } else if (!open && element.open) {
      if (typeof element.close === 'function') {
        element.close();
      } else {
        element.removeAttribute('open');
      }
    }
  }, [open]);

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault();
    if (busy !== true) {
      onSubmit();
    }
  };

  return (
    <dialog
      ref={ref}
      className="dialog"
      aria-labelledby={titleId}
      onClose={onClose}
      onCancel={(event) => {
        event.preventDefault();
        onClose();
      }}
    >
      {open && (
        <form className="stack" onSubmit={handleSubmit} noValidate>
          <h2 id={titleId}>{title}</h2>
          {children}
          {error != null && error.length > 0 && <ErrorSummary title={error} />}
          <div className="row row--end">
            <Button onClick={onClose}>Vazgeç</Button>
            <Button type="submit" variant={danger === true ? 'danger' : 'primary'} busy={busy}>
              {confirmLabel}
            </Button>
          </div>
        </form>
      )}
    </dialog>
  );
}

/* ---------- Tablo / satır kartı ---------- */

function subscribeNarrow(callback: () => void): () => void {
  if (typeof window.matchMedia !== 'function') {
    return () => undefined;
  }
  const query = window.matchMedia('(max-width: 480px)');
  query.addEventListener('change', callback);
  return () => query.removeEventListener('change', callback);
}

function isNarrow(): boolean {
  return typeof window.matchMedia === 'function' && window.matchMedia('(max-width: 480px)').matches;
}

/** 480 px ve altında tablolar satır kartına (tanım listesi) döner. */
export function useNarrow(): boolean {
  return useSyncExternalStore(subscribeNarrow, isNarrow, () => false);
}

export interface Column<T> {
  key: string;
  header: string;
  cell: (row: T) => ReactNode;
  /** Satır başlığı hücresi (<th scope="row">). */
  rowHeader?: boolean;
  tight?: boolean;
}

export interface DataListProps<T> {
  caption: string;
  columns: Column<T>[];
  rows: T[];
  rowKey: (row: T) => string;
}

export function DataList<T>({ caption, columns, rows, rowKey }: DataListProps<T>) {
  const narrow = useNarrow();
  if (narrow) {
    return (
      <ul className="cards" aria-label={caption}>
        {rows.map((row) => (
          <li key={rowKey(row)} className="card">
            <dl>
              {columns.map((column) => (
                <div key={column.key} className="card__pair">
                  <dt>{column.header}</dt>
                  <dd>{column.cell(row)}</dd>
                </div>
              ))}
            </dl>
          </li>
        ))}
      </ul>
    );
  }
  return (
    <div className="table-wrap">
      <table className="table">
        <caption className="visually-hidden">{caption}</caption>
        <thead>
          <tr>
            {columns.map((column) => (
              <th key={column.key} scope="col">
                {column.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={rowKey(row)}>
              {columns.map((column) =>
                column.rowHeader === true ? (
                  <th key={column.key} scope="row">
                    {column.cell(row)}
                  </th>
                ) : (
                  <td key={column.key} className={column.tight === true ? 'cell--tight' : undefined}>
                    {column.cell(row)}
                  </td>
                ),
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function LoadMore({ hasMore, loading, onLoad }: { hasMore: boolean; loading: boolean; onLoad: () => void }) {
  if (!hasMore) {
    return null;
  }
  return (
    <div className="row">
      <Button onClick={onLoad} busy={loading}>
        {loading ? 'Yükleniyor…' : 'Daha fazla göster'}
      </Button>
    </div>
  );
}

/* ---------- Kullanım çubuğu ---------- */

export function UsageMeter({ label, used, limit, unit }: { label: string; used: number; limit: number; unit: string }) {
  const id = useId();
  const full = used >= limit;
  return (
    <div className="meter">
      <div className="meter__label">
        <span id={id}>{label}</span>
        <span className="num">
          {used} / {limit} {unit}
        </span>
      </div>
      <meter aria-labelledby={id} min={0} max={Math.max(limit, 1)} low={limit * 0.8} high={Math.max(limit - 0.001, 0)} optimum={0} value={Math.min(used, limit)}>
        {used} / {limit}
      </meter>
      {full && <span className="field__error">Limit doldu.</span>}
    </div>
  );
}

/* ---------- Çevrimdışı bandı ---------- */

function subscribeOnline(callback: () => void): () => void {
  window.addEventListener('online', callback);
  window.addEventListener('offline', callback);
  return () => {
    window.removeEventListener('online', callback);
    window.removeEventListener('offline', callback);
  };
}

export function useOnline(): boolean {
  return useSyncExternalStore(
    subscribeOnline,
    () => navigator.onLine,
    () => true,
  );
}

export function OfflineBand() {
  const online = useOnline();
  if (online) {
    return null;
  }
  return (
    <div className="band band--offline" role="status">
      <span className="band__icon" aria-hidden="true">
        ⚠
      </span>
      Bağlantı yok. Değişiklikler gönderilmedi; formdaki metniniz korunuyor.
    </div>
  );
}

export function DemoTag({ label = 'Demo' }: { label?: string }) {
  return <span className="demo-tag">{label}</span>;
}
