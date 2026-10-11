// Oturum durumu. Kimlik sunucudaki cookie oturumundan gelir; istemci yalnız /api/me yanıtını bellekte tutar.
// Kural: logout, kullanıcı değişimi ve herhangi bir 401'de sorgu önbelleği TAMAMEN temizlenir.

import { useQueryClient } from '@tanstack/react-query';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { ApiError, request, resetCsrf, setUnauthorizedHandler } from '../api/client';
import type { User } from '../api/types';

export type AnonymousReason = 'initial' | 'expired' | 'logout';

export type SessionState =
  | { status: 'loading' }
  | { status: 'error'; error: ApiError }
  | { status: 'anonymous'; reason: AnonymousReason }
  | { status: 'authenticated'; user: User };

export interface SessionContextValue {
  state: SessionState;
  user: User | null;
  login: (email: string, password: string) => Promise<User>;
  logout: () => Promise<void>;
  /** /api/me'yi yeniden okur (ör. e-posta doğrulandıktan sonra). */
  refresh: () => Promise<void>;
}

const SessionContext = createContext<SessionContextValue | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [state, setState] = useState<SessionState>({ status: 'loading' });
  const userIdRef = useRef<string | null>(null);

  const dropEverything = useCallback(
    (reason: AnonymousReason) => {
      userIdRef.current = null;
      resetCsrf();
      queryClient.clear();
      setState({ status: 'anonymous', reason });
    },
    [queryClient],
  );

  const adopt = useCallback(
    (user: User) => {
      // Başka bir kullanıcıya geçildiyse (ör. diğer sekmede giriş) önceki kullanıcının verisi bellekte kalmaz.
      if (userIdRef.current !== null && userIdRef.current !== user.id) {
        resetCsrf();
        queryClient.clear();
      }
      userIdRef.current = user.id;
      setState({ status: 'authenticated', user });
    },
    [queryClient],
  );

  const refresh = useCallback(
    () =>
      request<User>('/api/me').then(
        ({ data }) => adopt(data),
        (error: unknown) => {
          if (error instanceof ApiError && error.status === 401) {
            return; // 401 işleyicisi durumu zaten "anonymous" yaptı.
          }
          if (userIdRef.current === null) {
            setState({ status: 'error', error: error instanceof ApiError ? error : new ApiError(0, {}) });
          }
        },
      ),
    [adopt],
  );

  useEffect(() => {
    setUnauthorizedHandler(() => dropEverything(userIdRef.current === null ? 'initial' : 'expired'));
    void refresh();
    return () => setUnauthorizedHandler(null);
  }, [dropEverything, refresh]);

  useEffect(() => {
    const onVisible = () => {
      if (document.visibilityState === 'visible' && userIdRef.current !== null) {
        void refresh();
      }
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => document.removeEventListener('visibilitychange', onVisible);
  }, [refresh]);

  const login = useCallback(
    async (email: string, password: string) => {
      const { data } = await request<{ user: User }>('/api/auth/login', { method: 'POST', body: { email, password } });
      // Kimlik değişti: CSRF token'ı ve önceki oturuma ait her şey atılır.
      resetCsrf();
      queryClient.clear();
      userIdRef.current = data.user.id;
      setState({ status: 'authenticated', user: data.user });
      return data.user;
    },
    [queryClient],
  );

  const logout = useCallback(async () => {
    try {
      await request<void>('/api/auth/logout', { method: 'POST' });
    } catch (error) {
      if (!(error instanceof ApiError && error.status === 401)) {
        throw error;
      }
    }
    dropEverything('logout');
  }, [dropEverything]);

  const value = useMemo<SessionContextValue>(
    () => ({ state, user: state.status === 'authenticated' ? state.user : null, login, logout, refresh }),
    [state, login, logout, refresh],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionContextValue {
  const value = useContext(SessionContext);
  if (value === null) {
    throw new Error('useSession yalnız SessionProvider içinde kullanılır.');
  }
  return value;
}

/** Oturum zorunlu ekranlarda kullanıcıyı verir (RequireAuth altında çağrılır). */
export function useUser(): User {
  const { user } = useSession();
  if (user === null) {
    throw new Error('Bu bileşen oturum gerektirir.');
  }
  return user;
}
