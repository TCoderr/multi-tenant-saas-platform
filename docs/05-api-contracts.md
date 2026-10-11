# 05 — API Sözleşmeleri

Temel: `https://localhost:8843`. Tüm API `/api` altındadır, JSON (`camelCase`), hatalar `application/problem+json`.
Kimlik: `__Host-kulvar-session` cookie'si (HttpOnly, Secure, SameSite=Lax). Güvensiz yöntemler (`POST/PUT/PATCH/DELETE`)
`X-CSRF-TOKEN` başlığı ister (webhook hariç). Yetkisiz istekte yönlendirme yoktur: `401`.

## 1. Ortak kurallar

| Konu | Kural |
|---|---|
| Şirket bağlamı | Yalnız URL yolundan: `/api/t/{tenantId}/…`. Gövde/başlıktaki tenant değeri yok sayılmaz, **kabul edilmez** (bilinmeyen alan → 400). |
| Üye olmayan / bilinmeyen / silinmiş şirket | `404 tenant_not_found` (gövde aynı; varlık sızdırmaz) |
| Üye ama izni yok | `403 forbidden` (`requiredPermission`) |
| Erişim modu engeli | `403 access_mode_restricted` (`accessMode`, `tenantStatus`, `subscriptionStatus`) |
| Şirket içinde kaynak yok / başka şirketin kimliği | `404 not_found` |
| Doğrulama hatası | `400 validation_failed` (`errors: { alan: [mesaj] }`) |
| Eşzamanlılık | `ETag: "<version>"`; güncellemede `If-Match` zorunlu. Eksik → `428 precondition_required`, eski → `412 precondition_failed` |
| Idempotency | `Idempotency-Key` (1–100 karakter). Aynı şirket+kullanıcı+işlem+anahtar ve aynı gövde → ilk yanıt tekrar döner (`Idempotent-Replay: true`); farklı gövde → `409 idempotency_key_reused` |
| Kota | `403 quota_exceeded` (`metric`, `limit`, `used`, `resetsAt`). `Retry-After` yok. |
| Hız sınırı | `429 rate_limited` + `Retry-After`. Kota ile ilgisizdir. |
| Sayfalama | `?limit=1..100` (varsayılan 25), `?cursor=`. Yanıt: `{ items, nextCursor }`. Sıralama ve filtreler izin listesiyle sınırlıdır. |
| DB kullanılamıyor | `503 dependency_unavailable`; başarı yanıtı asla dönmez |

ProblemDetails gövdesi: `type` (`https://kulvar.example/problems/<code>`), `title`, `status`, `code`, `traceId` ve koda özgü alanlar.
Kişisel veri veya iç hata ayrıntısı içermez.

## 2. Global uçlar (şirket içeriği döndürmez)

| Yöntem | Yol | Kimlik | Açıklama |
|---|---|---|---|
| GET | `/health/live`, `/health/ready` | — | Canlılık / DB hazır |
| GET | `/api/auth/csrf` | — | `{ token, headerName }`; login/logout sonrası yeniden alınır |
| POST | `/api/auth/signup` | — | `{ email, displayName, password }` → `202` (her durumda aynı gövde) |
| POST | `/api/auth/login` | — | `{ email, password }` → `200 { user }` + cookie; hata `401 invalid_credentials` (genel) |
| POST | `/api/auth/logout` | oturum | `204`; oturum sunucuda iptal edilir |
| GET | `/api/me` | oturum | `{ id, email, displayName, emailVerified, isPlatformOperator }` |
| GET | `/api/me/memberships` | oturum | `{ items: [ { tenantId, tenantName, role, tenantStatus } ] }` — yalnız metadata |
| POST | `/api/auth/verify-email` | oturum | `{ token }` → `204` |
| POST | `/api/auth/verify-email/resend` | oturum | `202` |
| GET | `/api/me/mailbox` | oturum | Demo posta kutusu: yalnız kendi e-postasına gelen iletiler |
| GET | `/api/me/mailbox/{id}` | oturum | İleti gövdesi |
| GET | `/api/plans` | — | Public plan/fiyat listesi |
| POST | `/api/tenants` | oturum + doğrulanmış e-posta | `{ name }` → `201 { tenantId }` (Owner + Trial) |
| POST | `/api/invites/lookup` | oturum | `{ token }` → `{ tenantName, role, state, expiresAt, invitedEmail (maskeli), emailMatches, emailVerified }`; bilinmeyen token `404 invite_not_found` |
| POST | `/api/invites/accept` | oturum + doğrulanmış e-posta | `{ token }` → `200 { tenantId }`. Hatalar: `403 email_not_verified`, `403 invite_email_mismatch`, `403 quota_exceeded` (koltuk), `409 invite_not_pending`, `409 already_member`, `409 tenant_not_accepting_members`, `410 invite_expired` |
| POST | `/api/webhooks/billing` | HMAC imzası | Sağlayıcı olayı; cookie/CSRF yok |

Davet ve doğrulama bağlantıları token'ı URL **parçasında** (`#token=…`) taşır; parça sunucuya gönderilmez ve erişim loguna düşmez.

## 3. Şirket kapsamlı uçlar — `/api/t/{tenantId}`

Tabloda "İzin" sütunu [06](06-security-and-tenant-isolation.md) §3'teki matrise başvurur; "Mod" o izin için gereken erişim modudur.

### Çalışma alanı
| Yöntem | Yol | İzin | Not |
|---|---|---|---|
| GET | `/` | üye | `{ tenant{id,name,status,version,purgeAfter,suspendedReason}, role, permissions[], accessMode, subscription{status,planCode,planName,trialEndsAt,graceEndsAt}, entitlements{seatLimit,monthlyTicketLimit,exportEnabled}, usage{seatsUsed,pendingInvites,ticketsThisPeriod,periodResetsAt} }` |
| PATCH | `/settings` | `tenant.settings` | `{ name }`, `If-Match` |
| POST | `/deletion` | `tenant.delete` | `{ confirmName, password }` → `{ status: "DeletionPending", purgeAfter }`; onay tutmazsa `403 confirmation_failed` |
| DELETE | `/deletion` | `tenant.delete.cancel` | Silmeyi iptal (yalnız bekleme süresi içinde) |

### Müşteriler
| Yöntem | Yol | İzin | Not |
|---|---|---|---|
| GET | `/customers?q=&sort=name\|createdAt&dir=asc\|desc&limit=&cursor=` | `customers.read` | Viewer'a e-posta/telefon maskeli |
| POST | `/customers` | `customers.write` | `{ name, companyTitle?, email?, phone?, notes? }` → `201` + `ETag` |
| GET | `/customers/{id}` | `customers.read` | `ETag` |
| PUT | `/customers/{id}` | `customers.write` | `If-Match` |
| DELETE | `/customers/{id}` | `customers.delete` | Talebi varsa `409 customer_has_tickets` |

### Destek talepleri
| Yöntem | Yol | İzin | Not |
|---|---|---|---|
| GET | `/tickets?status=&priority=&assignee=me\|none\|{userId}&customerId=&q=&sort=createdAt\|updatedAt&dir=&limit=&cursor=` | `tickets.read` | |
| POST | `/tickets` | `tickets.write` | `Idempotency-Key` zorunlu. `{ customerId, title, description, priority, assigneeUserId? }` → `201`. Kota tüketir. |
| GET | `/tickets/{id}` | `tickets.read` | `ETag`, `allowedActions[]` (aktörün rolüne göre) |
| PATCH | `/tickets/{id}` | `tickets.write` | `{ title?, description?, priority? }`, `If-Match` |
| POST | `/tickets/{id}/transition` | `tickets.write` | `{ action: start\|release\|resolve\|close\|reopen }`, `If-Match` |
| PUT | `/tickets/{id}/assignee` | `tickets.write` | `{ userId \| null }`, `If-Match` |
| GET | `/tickets/{id}/timeline` | `tickets.read` | Yorumlar + etkinlikler, zaman sıralı |
| POST | `/tickets/{id}/comments` | `tickets.write` | `{ body }` (düz metin, ≤ 5000) |
| GET | `/assignable-members` | `tickets.read` | Atanabilir aktif üyeler (id, ad, rol) |

### Ekip
| Yöntem | Yol | İzin | Not |
|---|---|---|---|
| GET | `/members` | `members.read` | E-posta yalnız Owner/Admin'e |
| PATCH | `/members/{userId}` | `members.manage` | `{ role }`, `If-Match` |
| DELETE | `/members/{userId}` | `members.manage` | Atamaları boşaltır |
| POST | `/members/leave` | üye | Son Owner ise `409 last_owner` |
| GET | `/invites` | `members.manage` | Token asla dönmez |
| POST | `/invites` | `members.invite` | `{ email, role }` → `201`; koltuk dolu → `403 quota_exceeded (seats)`; `role: Owner` → `400 owner_requires_offer`; `409 already_member` / `invite_already_pending` |
| DELETE | `/invites/{id}` | `members.manage` | İptal |
| GET | `/ownership-offers` | üye | Yalnız teklif eden ve hedef görür |
| POST | `/ownership-offers` | `ownership.offer` | `{ toUserId, demoteSelf }` |
| POST | `/ownership-offers/{id}/accept` | `ownership.accept` + teklifin hedefi olmak | Atomik rol değişimi → `{ role: "Owner", previousOwnerDemoted }` |
| DELETE | `/ownership-offers/{id}` | teklif eden veya hedef | İptal / ret |

### Plan ve kullanım
| Yöntem | Yol | İzin | Not |
|---|---|---|---|
| GET | `/billing` | `billing.read` | Abonelik, plan, kullanım, açık işlem, son işlemler |
| POST | `/billing/plan-change` | `billing.manage` | `Idempotency-Key` zorunlu. `{ planCode, simulatedOutcome? }` → `202 { operationId, status: "Pending" }`; `409 already_on_plan` / `operation_in_progress` (sonucu bekleyen işlem varken) |
| GET | `/billing/operations/{id}` | `billing.read` | İşlem durumu (polling) |
| POST | `/billing/cancel` | `billing.manage` | Abonelik iptali |

`simulatedOutcome` yalnız `Billing:Simulation:AllowOutcomeSelection = true` iken kabul edilir (yalnız Development/Testing); aksi halde `400`.

### Denetim ve export
| Yöntem | Yol | İzin | Not |
|---|---|---|---|
| GET | `/audit?limit=&cursor=` | `audit.read` | |
| GET | `/exports` | `export.manage` | |
| POST | `/exports` | `export.manage` | Plan `exportEnabled` değilse `403 feature_not_in_plan`; süren iş varsa `409 export_in_progress`. → `202` |
| POST | `/exports/{id}/download-link` | `export.manage` + işi isteyen kullanıcı | → `{ url, expiresAt }` (60 sn, tek kullanım) |
| GET | `/exports/{id}/file?token=` | `export.manage` + işi isteyen kullanıcı + token | `application/zip`, `Content-Disposition: attachment`. Hatalar: `403 invalid_download_token` (yanlış/süresi dolmuş/kullanılmış), `409 export_not_ready`, `410 export_expired`. Token istek loguna yazılmaz (yalnız uç şablonu loglanır). |

## 4. Platform operatörü uçları — `/api/platform`

Oturum + `platform_operators` kaydı gerekir; aksi halde `404`. Bu uçlar `kulvar_platform` DB rolüyle çalışır ve içerik tablolarına erişemez.

| Yöntem | Yol | Açıklama |
|---|---|---|
| GET | `/tenants?status=&limit=` | Şirket metadata'sı: id, ad, durum, askı gerekçesi, plan, abonelik durumu, tarihleri (en yeni önce) |
| POST | `/tenants/{id}/suspend` | `{ reason }` (≥ 10 karakter); aktif değilse `409 invalid_tenant_state` |
| POST | `/tenants/{id}/reactivate` | `{ reason }` |
| GET | `/billing/operations?status=NeedsResolution` | Çözüm bekleyen işlemler |
| POST | `/billing/operations/{id}/resolve` | `{ action: refund\|void\|close, note }` (not ≥ 10 karakter). `refund` işlemi iade kuyruğuna alır (worker iade eder); `void`/`close` terminaldir. Çözüm beklemiyorsa `409 operation_not_awaiting_resolution` |
| GET | `/jobs/dead` | Ölü outbox mesajları (tür, deneme, hata kodu; gövde gösterilmez) |
| POST | `/jobs/{id}/replay` | Yeniden kuyruğa al |
| GET | `/audit?limit=` | Platform denetim kaydı (askıya alma, yeniden açma, işlem çözümü, yeniden kuyruğa alma) |
| POST | `/simulation/provider-event` | Yalnız simülasyon açıkken: `{ tenantId, kind: renewal_failed\|renewed }` |

## 5. Webhook sözleşmesi (simüle sağlayıcı → Kulvar)

```
POST /api/webhooks/billing
Kulvar-Signature: t=<unix saniye>,v1=<hex(HMAC-SHA256(secret, "<t>." + ham gövde baytları))>
Content-Type: application/json   (gövde ≤ 64 KiB)

{ "id": "evt_…", "type": "operation.succeeded|operation.declined|subscription.renewal_failed|subscription.renewed",
  "subscriptionId": "psub_…", "sequence": 7, "merchantReference": "mref_…", "planCode": "team", "occurredAt": "…" }
```

| Durum | Yanıt |
|---|---|
| İmza/zaman damgası geçersiz (tolerans ±5 dk) | `401 invalid_signature`; hiçbir şey yazılmaz |
| Gövde çok büyük / bozuk | `413` / `400` |
| Yeni olay | `202`; inbox'a yazılır, worker uygular |
| Aynı `id` + aynı gövde hash'i | `200` (yinelenen; etkisiz) |
| Aynı `id` + farklı gövde | `409 event_payload_conflict`; kayıt `Conflict`, operatöre görünür |

Yönlendirme: işlem olayları `merchantReference` → `billing_operations.tenant_id`, abonelik olayları
`subscriptionId` → `subscriptions.tenant_id` ile bizim kayıtlarımızdan çözülür. Gövdede şirket kimliği alanı yoktur; olsa da kullanılmaz.
Eşleşme yoksa olay `UnknownMapping` kalır (sınırlı süre yeniden denenir, sonra operatöre görünür).
Sıra: abonelik olayları yalnız `sequence > provider_sequence` ise uygulanır; eski olay `Stale` olarak kapanır.
