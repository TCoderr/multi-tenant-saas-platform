import { useCallback, useEffect, useId, useState } from 'react';
import { formErrors } from './ui';

export interface FormState<T extends Record<string, string>> {
  values: T;
  setValues: (values: T) => void;
  set: (name: keyof T) => (value: string) => void;
  errors: Record<string, string>;
  summary: string | null;
  /** Hata özetini ve alan hatalarını gösterir; odak ilk hatalı alana taşınır. */
  fail: (summary: string, fields?: Record<string, string>) => void;
  /** Sunucu hatasını (ProblemDetails) özet + alan hatalarına çevirir. */
  failWith: (error: unknown) => void;
  clear: () => void;
  /** Formun (veya alanları saran öğenin) id'si: hata sonrası odak ilk hatalı alana buradan bulunur. */
  formId: string;
}

/** Küçük form durumu: değerler, alan hataları, hata özeti. Hata durumunda girilen metin korunur. */
export function useFormState<T extends Record<string, string>>(initial: T): FormState<T> {
  const [values, setValues] = useState<T>(initial);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [summary, setSummary] = useState<string | null>(null);
  const [failures, setFailures] = useState(0);
  const formId = useId();

  useEffect(() => {
    if (failures > 0) {
      document.getElementById(formId)?.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus();
    }
  }, [failures, formId]);

  const set = useCallback((name: keyof T) => (value: string) => setValues((previous) => ({ ...previous, [name]: value })), []);

  const fail = useCallback((message: string, fields: Record<string, string> = {}) => {
    setSummary(message);
    setErrors(fields);
    setFailures((count) => count + 1);
  }, []);

  const failWith = useCallback(
    (error: unknown) => {
      const parsed = formErrors(error);
      fail(parsed.summary, parsed.fields);
    },
    [fail],
  );

  const clear = useCallback(() => {
    setSummary(null);
    setErrors({});
  }, []);

  return { values, setValues, set, errors, summary, fail, failWith, clear, formId };
}

/** Boş metni null'a çevirir (isteğe bağlı alanlar sunucuya null gider). */
export function orNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}

/** Yalnız uygulama içi yol: "/…" ile başlar, "//" ile başlamaz (açık yönlendirme yok). */
export function safeInternalPath(value: unknown): string | null {
  return typeof value === 'string' && /^\/(?![/\\])/.test(value) ? value : null;
}

export function hashToken(hash: string): string | null {
  const token = new URLSearchParams(hash.replace(/^#/, '')).get('token');
  return token !== null && token.length > 0 ? token : null;
}
