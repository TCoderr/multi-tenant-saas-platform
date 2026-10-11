import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { request } from '../api/client';
import { queryKeys, tenantOfKey } from '../api/queryKeys';
import { useNotice } from '../components/notice';
import { Button, Dialog, problemText } from '../components/ui';
import { useTenantMutation, useWorkspace } from './context';

/** Şirketten ayrılma. Son Owner ayrılamaz (sunucu 409 last_owner döndürür); ayrılınca şirketin verisi bellekten atılır. */
export function LeaveCompany() {
  const ws = useWorkspace();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const notify = useNotice();
  const [open, setOpen] = useState(false);

  const leave = useTenantMutation<void>({
    mutationFn: async () => {
      await request<void>(ws.api('/members/leave'), { method: 'POST' });
    },
    onSuccess: () => {
      queryClient.removeQueries({ predicate: (cached) => tenantOfKey(cached.queryKey) === ws.tenantId });
      void queryClient.invalidateQueries({ queryKey: queryKeys.memberships(ws.userId) });
      notify(`${ws.summary.tenant.name} şirketinden ayrıldınız.`);
      void navigate('/app');
    },
  });

  return (
    <>
      <Button variant="danger" onClick={() => setOpen(true)} disabledReason={ws.denied('members.leave')}>
        Şirketten ayrıl
      </Button>
      <Dialog
        open={open}
        title="Şirketten ayrıl"
        confirmLabel="Ayrıl"
        danger
        busy={leave.isPending}
        error={leave.isError ? problemText(leave.error) : null}
        onClose={() => {
          setOpen(false);
          leave.reset();
        }}
        onSubmit={() => leave.mutate()}
      >
        <p>
          <strong>{ws.summary.tenant.name}</strong> şirketindeki üyeliğiniz sona erecek ve şirketin içeriğine erişiminiz kalkacak. Üzerinizdeki
          talepler boşa alınır. Geri dönmek için yeniden davet edilmeniz gerekir.
        </p>
      </Dialog>
    </>
  );
}
