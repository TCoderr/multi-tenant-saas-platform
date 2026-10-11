// Kısa başarı bildirimi: tek bir canlı bölge (aria-live="polite"). Hata bildirimi burada gösterilmez; hatalar yerinde durur.

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';

interface NoticeContextValue {
  notify: (message: string) => void;
}

const NoticeContext = createContext<NoticeContextValue>({ notify: () => undefined });

export function NoticeProvider({ children }: { children: ReactNode }) {
  const [notice, setNotice] = useState<{ id: number; message: string } | null>(null);

  const notify = useCallback((message: string) => {
    setNotice((previous) => ({ id: (previous?.id ?? 0) + 1, message }));
  }, []);

  useEffect(() => {
    if (notice === null) {
      return;
    }
    const timer = window.setTimeout(() => setNotice(null), 6000);
    return () => window.clearTimeout(timer);
  }, [notice]);

  const value = useMemo(() => ({ notify }), [notify]);

  return (
    <NoticeContext.Provider value={value}>
      {children}
      <div className="toast" role="status" aria-live="polite">
        {notice?.message}
      </div>
    </NoticeContext.Provider>
  );
}

export function useNotice(): (message: string) => void {
  return useContext(NoticeContext).notify;
}
