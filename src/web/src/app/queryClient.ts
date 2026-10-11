import { QueryCache, QueryClient } from '@tanstack/react-query';
import { ApiError } from '../api/client';
import { queryKeys, tenantOfKey, userOfKey } from '../api/queryKeys';

/**
 * Sorgu istemcisi. 4xx yanıtları yeniden denenmez (yetki/doğrulama kararı sunucunundur); ağ ve 5xx hataları bir kez denenir.
 * Bir şirket sorgusu "şirket yok" veya "yetki yok" derse o şirketin çalışma alanı özeti yeniden okunur: üyeliği kaldırılan ya da
 * rolü düşürülen kullanıcının ekranı eski yetkilerle kalmaz.
 */
export function createQueryClient(): QueryClient {
  const client: QueryClient = new QueryClient({
    queryCache: new QueryCache({
      onError: (error, query) => {
        if (!(error instanceof ApiError) || (error.code !== 'tenant_not_found' && error.status !== 403)) {
          return;
        }
        const tenantId = tenantOfKey(query.queryKey);
        const userId = userOfKey(query.queryKey);
        if (tenantId === null || userId === null || query.queryKey[4] === 'workspace') {
          return;
        }
        void client.invalidateQueries({ queryKey: queryKeys.workspace(userId, tenantId) });
        void client.invalidateQueries({ queryKey: queryKeys.memberships(userId) });
      },
    }),
    defaultOptions: {
      queries: {
        staleTime: 10_000,
        gcTime: 5 * 60_000,
        refetchOnWindowFocus: true,
        retry: (failureCount, error) => {
          if (error instanceof ApiError && error.status >= 400 && error.status < 500) {
            return false;
          }
          return failureCount < 1;
        },
      },
      mutations: { retry: false },
    },
  });
  return client;
}
