// Çalışma alanı bağlamı. Şirket kimliği YALNIZ URL'den (/t/:tenantId/…) gelir; bellekte "aktif şirket" tutulmaz.
// Yetkiler sunucunun bildirdiği `permissions` listesidir (rol × erişim modu); istemci yalnız gösterim için kullanır.

import { useMutation, useQueryClient, type QueryKey } from '@tanstack/react-query';
import { createContext, useContext, useMemo } from 'react';
import { ApiError } from '../api/client';
import { queryKeys, type TenantKey } from '../api/queryKeys';
import type { PermissionCode, WorkspaceSummary } from '../api/types';
import { deniedReason } from '../lib/format';

export interface WorkspaceContextValue {
  userId: string;
  tenantId: string;
  summary: WorkspaceSummary;
  can: (permission: PermissionCode) => boolean;
  /** Yetki varsa null; yoksa kullanıcıya gösterilecek neden. */
  denied: (permission: PermissionCode) => string | null;
  /** Bu şirket ve bu kullanıcı için sorgu anahtarı: ['u', userId, 't', tenantId, …]. */
  key: (...rest: unknown[]) => TenantKey;
  api: (path: string) => string;
}

export const WorkspaceContext = createContext<WorkspaceContextValue | null>(null);

export function buildWorkspaceValue(userId: string, tenantId: string, summary: WorkspaceSummary): WorkspaceContextValue {
  const granted = new Set<string>(summary.permissions);
  return {
    userId,
    tenantId,
    summary,
    can: (permission) => granted.has(permission),
    denied: (permission) => (granted.has(permission) ? null : deniedReason(permission, summary.role, summary.accessMode)),
    key: (...rest) => queryKeys.tenant(userId, tenantId, ...rest),
    api: (path) => `/api/t/${tenantId}${path}`,
  };
}

export function useWorkspace(): WorkspaceContextValue {
  const value = useContext(WorkspaceContext);
  if (value === null) {
    throw new Error('useWorkspace yalnız çalışma alanı içinde kullanılır.');
  }
  return value;
}

export interface TenantMutationOptions<TData, TVariables> {
  mutationFn: (variables: TVariables) => Promise<TData>;
  /** Başarıdan sonra geçersiz kılınacak anahtarlar (çalışma alanı özeti her zaman yenilenir: sayaçlar sunucudan gelir). */
  invalidate?: QueryKey[];
  onSuccess?: (data: TData, variables: TVariables) => void;
}

/**
 * Şirket kapsamlı değişiklik. İyimser güncelleme yoktur: başarıdan sonra ilgili sorgular geçersiz kılınır.
 * Sunucu "şirket yok / yetki yok" derse çalışma alanı özeti yeniden okunur (rol veya üyelik değişmiş olabilir).
 */
export function useTenantMutation<TData, TVariables = void>(options: TenantMutationOptions<TData, TVariables>) {
  const { userId, tenantId } = useWorkspace();
  const queryClient = useQueryClient();
  const workspaceKey = useMemo(() => queryKeys.workspace(userId, tenantId), [userId, tenantId]);

  return useMutation<TData, unknown, TVariables>({
    mutationFn: options.mutationFn,
    onSuccess: (data, variables) => {
      for (const key of options.invalidate ?? []) {
        void queryClient.invalidateQueries({ queryKey: key });
      }
      void queryClient.invalidateQueries({ queryKey: workspaceKey });
      options.onSuccess?.(data, variables);
    },
    onError: (error) => {
      if (error instanceof ApiError && (error.code === 'tenant_not_found' || error.status === 403)) {
        void queryClient.invalidateQueries({ queryKey: workspaceKey });
        void queryClient.invalidateQueries({ queryKey: queryKeys.memberships(userId) });
      }
    },
  });
}
