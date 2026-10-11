import { QueryClientProvider, type QueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import { AccountLayout, PublicLayout, RequireAuth, WorkspaceLayout } from './app/layouts';
import { createQueryClient } from './app/queryClient';
import { NoticeProvider } from './components/notice';
import { CompaniesPage, InviteAcceptPage, MailboxPage, VerifyEmailPage } from './pages/account';
import { AuditPage, ExportsPage, SettingsPage } from './pages/admin';
import { CustomerDetailPage, CustomerNewPage, CustomersPage } from './pages/customers';
import { PlanPage } from './pages/plan';
import { PlatformPage } from './pages/platform';
import { LandingPage, LoginPage, NotFoundPage, SignupPage } from './pages/public';
import { TeamPage } from './pages/team';
import { TicketDetailPage, TicketNewPage, TicketsPage } from './pages/tickets';
import { SessionProvider } from './session/SessionProvider';

/** Yollar. Şirket bağlamı yalnız URL'dedir: /t/:tenantId/… */
export function AppRoutes() {
  return (
    <Routes>
      <Route element={<PublicLayout />}>
        <Route index element={<LandingPage />} />
        <Route path="login" element={<LoginPage />} />
        <Route path="signup" element={<SignupPage />} />
      </Route>
      <Route element={<RequireAuth />}>
        <Route element={<AccountLayout />}>
          <Route path="app" element={<CompaniesPage />} />
          <Route path="mail" element={<MailboxPage />} />
          <Route path="mail/:messageId" element={<MailboxPage />} />
          <Route path="verify-email" element={<VerifyEmailPage />} />
          <Route path="invite" element={<InviteAcceptPage />} />
          <Route path="platform" element={<PlatformPage />} />
        </Route>
        <Route path="t/:tenantId" element={<WorkspaceLayout />}>
          <Route index element={<Navigate to="tickets" replace />} />
          <Route path="customers" element={<CustomersPage />} />
          <Route path="customers/new" element={<CustomerNewPage />} />
          <Route path="customers/:customerId" element={<CustomerDetailPage />} />
          <Route path="tickets" element={<TicketsPage />} />
          <Route path="tickets/new" element={<TicketNewPage />} />
          <Route path="tickets/:ticketId" element={<TicketDetailPage />} />
          <Route path="team" element={<TeamPage />} />
          <Route path="plan" element={<PlanPage />} />
          <Route path="audit" element={<AuditPage />} />
          <Route path="exports" element={<ExportsPage />} />
          <Route path="settings" element={<SettingsPage />} />
          <Route path="*" element={<NotFoundPage />} />
        </Route>
      </Route>
      <Route element={<PublicLayout />}>
        <Route path="*" element={<NotFoundPage />} />
      </Route>
    </Routes>
  );
}

/** Sağlayıcılar: sorgu istemcisi → bildirim → oturum. Testler kendi yönlendiricisini ve istemcisini verebilir. */
export function AppProviders({ client, children }: { client: QueryClient; children: React.ReactNode }) {
  return (
    <QueryClientProvider client={client}>
      <NoticeProvider>
        <SessionProvider>{children}</SessionProvider>
      </NoticeProvider>
    </QueryClientProvider>
  );
}

export function App() {
  const [client] = useState(createQueryClient);
  return (
    <AppProviders client={client}>
      <BrowserRouter>
        <AppRoutes />
      </BrowserRouter>
    </AppProviders>
  );
}
