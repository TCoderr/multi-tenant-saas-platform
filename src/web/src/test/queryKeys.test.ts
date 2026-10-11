import { describe, expect, it } from 'vitest';
import { queryKeys, tenantOfKey, userOfKey } from '../api/queryKeys';
import { TENANT_A, TENANT_B, USER_1, USER_2 } from './helpers';

describe('sorgu anahtarları', () => {
  it('şirket verisi her zaman [u, kullanıcı, t, şirket, …] altında durur', () => {
    const keys = [
      queryKeys.workspace(USER_1, TENANT_A),
      queryKeys.customers(USER_1, TENANT_A),
      queryKeys.customers(USER_1, TENANT_A, { q: 'deniz' }),
      queryKeys.customer(USER_1, TENANT_A, 'c1'),
      queryKeys.tickets(USER_1, TENANT_A, { status: 'Open' }),
      queryKeys.ticket(USER_1, TENANT_A, 't1'),
      queryKeys.timeline(USER_1, TENANT_A, 't1'),
      queryKeys.assignable(USER_1, TENANT_A),
      queryKeys.members(USER_1, TENANT_A),
      queryKeys.invites(USER_1, TENANT_A),
      queryKeys.offers(USER_1, TENANT_A),
      queryKeys.billing(USER_1, TENANT_A),
      queryKeys.billingOperation(USER_1, TENANT_A, 'op1'),
      queryKeys.audit(USER_1, TENANT_A),
      queryKeys.exports(USER_1, TENANT_A),
    ];
    for (const key of keys) {
      expect(key.slice(0, 4)).toEqual(['u', USER_1, 't', TENANT_A]);
      expect(tenantOfKey(key)).toBe(TENANT_A);
      expect(userOfKey(key)).toBe(USER_1);
    }
  });

  it('aynı kaynağın anahtarı şirketten şirkete ve kullanıcıdan kullanıcıya farklıdır', () => {
    const filter = { q: 'aksoy' };
    expect(queryKeys.customers(USER_1, TENANT_A, filter)).not.toEqual(queryKeys.customers(USER_1, TENANT_B, filter));
    expect(queryKeys.customers(USER_1, TENANT_A, filter)).not.toEqual(queryKeys.customers(USER_2, TENANT_A, filter));
    expect(queryKeys.ticket(USER_1, TENANT_A, 'x')).not.toEqual(queryKeys.ticket(USER_1, TENANT_B, 'x'));
  });

  it('kullanıcı kapsamlı anahtarlar kullanıcıyı taşır ama şirket taşımaz', () => {
    for (const key of [queryKeys.memberships(USER_1), queryKeys.mailbox(USER_1), queryKeys.mailboxMessage(USER_1, 'm1'), queryKeys.platform(USER_1, 'tenants')]) {
      expect(userOfKey(key)).toBe(USER_1);
      expect(tenantOfKey(key)).toBeNull();
    }
    expect(userOfKey(queryKeys.publicPlans)).toBeNull();
  });

  it('kimliksiz anahtar kurulamaz (boş kimlik başka kapsamla çakışmasın)', () => {
    expect(() => queryKeys.tenant(USER_1, '')).toThrow();
    expect(() => queryKeys.tenant('', TENANT_A)).toThrow();
    expect(() => queryKeys.user('')).toThrow();
  });

  it('liste anahtarı, şirketin tüm listelerini geçersiz kılacak önekle başlar', () => {
    const list = queryKeys.customers(USER_1, TENANT_A, { q: 'a' });
    const prefix = queryKeys.customers(USER_1, TENANT_A);
    expect(list.slice(0, prefix.length)).toEqual([...prefix]);
  });
});
