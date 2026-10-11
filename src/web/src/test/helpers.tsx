// Bileşen testleri için yardımcılar: sahte fetch yönlendiricisi, örnek veriler ve uygulamayı bellek yönlendiricisiyle kurma.

import type { QueryClient } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { useEffect } from 'react';
import { MemoryRouter, useNavigate, type NavigateFunction } from 'react-router-dom';
import { vi } from 'vitest';
import { AppProviders, AppRoutes } from '../App';
import { resetCsrf } from '../api/client';
import type { Customer, PermissionCode, Ticket, User, WorkspaceSummary } from '../api/types';
import { createQueryClient } from '../app/queryClient';

export const TENANT_A = '11111111-1111-4111-8111-111111111111';
export const TENANT_B = '22222222-2222-4222-8222-222222222222';
export const USER_1 = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
export const USER_2 = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';

export interface RecordedCall {
  method: string;
  path: string;
  search: string;
  headers: Record<string, string>;
  body: unknown;
}

export type Handler = (call: RecordedCall) => Response | Promise<Response>;

export function json(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });
}

export function noContent(): Response {
  return new Response(null, { status: 204 });
}

export function problem(status: number, code: string, extra: Record<string, unknown> = {}, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify({ title: `Sorun: ${code}`, status, code, traceId: `trace-${code}`, ...extra }), {
    status,
    headers: { 'Content-Type': 'application/problem+json', ...headers },
  });
}

export interface MockApi {
  calls: RecordedCall[];
  /** Yolu değiştirir veya ekler ("METHOD /yol"). */
  on: (route: string, handler: Handler) => void;
  callsTo: (route: string) => RecordedCall[];
}

/** `fetch`'i sahte yönlendiriciyle değiştirir. Tanımsız yol 500 `unmocked` döndürür (test sessizce geçmesin). */
export function mockApi(routes: Record<string, Handler>): MockApi {
  const table = new Map<string, Handler>(Object.entries({ 'GET /api/auth/csrf': () => json(200, { token: 'csrf-1', headerName: 'X-CSRF-TOKEN' }), ...routes }));
  const calls: RecordedCall[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url, 'https://kulvar.test');
    const method = (init?.method ?? 'GET').toUpperCase();
    const headers: Record<string, string> = {};
    new Headers(init?.headers).forEach((value, key) => {
      headers[key.toLowerCase()] = value;
    });
    const call: RecordedCall = {
      method,
      path: url.pathname,
      search: url.search,
      headers,
      body: typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : undefined,
    };
    calls.push(call);
    const handler = table.get(`${method} ${url.pathname}`);
    return handler ? handler(call) : problem(500, 'unmocked', { route: `${method} ${url.pathname}` });
  });
  vi.stubGlobal('fetch', fetchMock);
  resetCsrf();
  return {
    calls,
    on: (route, handler) => table.set(route, handler),
    callsTo: (route) => calls.filter((call) => `${call.method} ${call.path}` === route),
  };
}

export function deferred<T>(): { promise: Promise<T>; resolve: (value: T) => void } {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

export function makeUser(overrides: Partial<User> = {}): User {
  return { id: USER_1, email: 'ayse@ornek.test', displayName: 'Ayşe Yıldız', emailVerified: true, isPlatformOperator: false, ...overrides };
}

const ROLE_PERMISSIONS: Record<string, PermissionCode[]> = {
  Owner: [
    'workspace.read', 'members.leave', 'tenant.settings', 'tenant.delete', 'customers.read', 'customers.pii', 'customers.write', 'customers.delete',
    'tickets.read', 'tickets.write', 'members.read', 'members.pii', 'members.invite', 'members.manage', 'ownership.offer', 'ownership.accept',
    'billing.read', 'billing.manage', 'audit.read', 'export.manage',
  ],
  Agent: ['workspace.read', 'members.leave', 'customers.read', 'customers.pii', 'customers.write', 'tickets.read', 'tickets.write', 'members.read', 'ownership.accept'],
  Viewer: ['workspace.read', 'members.leave', 'customers.read', 'tickets.read', 'members.read', 'ownership.accept'],
};

export function makeWorkspace(tenantId: string, name: string, overrides: Partial<WorkspaceSummary> = {}): WorkspaceSummary {
  const role = overrides.role ?? 'Owner';
  return {
    tenant: { id: tenantId, name, status: 'Active', version: 1, purgeAfter: null, suspendedReason: null },
    userId: USER_1,
    role,
    permissions: ROLE_PERMISSIONS[role] ?? [],
    accessMode: 'Full',
    subscription: { status: 'Active', planCode: 'team', planName: 'Team', trialEndsAt: null, graceEndsAt: null },
    entitlements: { seatLimit: 25, monthlyTicketLimit: 2000, exportEnabled: true },
    usage: { seatsUsed: 5, pendingInvites: 0, ticketsThisPeriod: 7, periodResetsAt: '2026-11-01T00:00:00Z' },
    ...overrides,
  };
}

export function makeCustomer(id: string, name: string, overrides: Partial<Customer> = {}): Customer {
  return {
    id,
    name,
    companyTitle: null,
    email: `${id.slice(0, 4)}@musteri.test`,
    phone: '+90 555 010 01 01',
    notes: null,
    piiMasked: false,
    createdAt: '2026-10-01T09:00:00Z',
    updatedAt: '2026-10-01T09:00:00Z',
    version: 1,
    ...overrides,
  };
}

export function makeTicket(id: string, overrides: Partial<Ticket> = {}): Ticket {
  return {
    id,
    number: 12,
    title: 'Teslimat gecikti',
    description: 'Sipariş üç gündür kargoda.',
    status: 'Open',
    priority: 'Normal',
    customerId: 'c0000000-0000-4000-8000-000000000001',
    customerName: 'Deniz Arslan',
    assigneeUserId: null,
    assigneeName: null,
    createdByUserId: USER_1,
    createdAt: '2026-10-01T09:00:00Z',
    updatedAt: '2026-10-01T09:00:00Z',
    resolvedAt: null,
    closedAt: null,
    reopenCount: 0,
    version: 3,
    allowedActions: ['Start', 'Resolve'],
    canModify: true,
    ...overrides,
  };
}

export interface RenderedApp {
  client: QueryClient;
  navigate: NavigateFunction;
}

/** Uygulamanın tamamını (sağlayıcılar + yollar) verilen adreste kurar. `navigate` ile test içinden yol değiştirilir. */
export function renderApp(path: string): RenderedApp {
  const client = createQueryClient();
  const handle: { navigate: NavigateFunction | null } = { navigate: null };
  function Capture() {
    const navigate = useNavigate();
    useEffect(() => {
      handle.navigate = navigate;
    }, [navigate]);
    return null;
  }
  render(
    <AppProviders client={client}>
      <MemoryRouter initialEntries={[path]}>
        <Capture />
        <AppRoutes />
      </MemoryRouter>
    </AppProviders>,
  );
  return {
    client,
    navigate: ((...args: Parameters<NavigateFunction>) => handle.navigate!(...args)) as NavigateFunction,
  };
}
