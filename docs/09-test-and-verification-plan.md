# 09 — Test ve Doğrulama Planı

## 1. Katmanlar

| Kısaltma | Katman | Araç | Ne doğrular |
|---|---|---|---|
| U | Birim | xUnit v3 | Durum makineleri, rol kuralları, hash/imza, para/zaman, CSV, e-posta normalizasyonu |
| R | RLS / doğrudan SQL | xUnit v3 + Npgsql, **runtime rolleri** | Politikalar, yetkiler, bileşik FK, trigger; EF kullanılmaz |
| I | Entegrasyon (HTTP + DB) | xUnit v3 + `WebApplicationFactory` + Testcontainers PostgreSQL 18 | Yetki, transaction, kilit, kota, idempotency, worker |
| C | Bileşen | Vitest + Testing Library | Önbellek anahtarları, durum ekranları, form hataları |
| E | Tarayıcı uçtan uca | Playwright (kurulu Chrome) + axe | Gerçek HTTPS yığını, A/B, paralel sekme, erişilebilirlik |
| S | Yığın duman | PowerShell betiği | Compose: boş kurulum, kalıcılık, DB kesintisi, seed, root olmayan kullanıcı |

## 2. Test veritabanı güvenliği

- Entegrasyon testleri yalnız kendi başlattığı Testcontainers PostgreSQL container'ına yazar. Geliştirme yığınına (`kulvar`) veya komşu
  projelerin veritabanlarına bağlanmaz.
- `TestDatabaseGuard` ilk yazmadan önce üç şeyi birlikte ister: açık onay (`KULVAR_TEST_DB=owned` fixture tarafından verilir), hedefin
  container'ın bildirdiği uç nokta ile aynı olması ve sunucudaki sahiplik işaretinin (`kulvar_test.run_marker`, koşu kimliği) eşleşmesi.
  Ad öneki tek başına yeterli sayılmaz. Yanlış hedefte hiçbir yazma yapılmadığı sahte (spy) bağlantıyla birim testinde doğrulanır.
- Şema kurulumu `kulvar_owner`, rol kurulumu superuser ile yapılır; **test edilen kod yalnız `kulvar_app` / `kulvar_worker` / `kulvar_platform` ile bağlanır.**
- Yarışlar deterministiktir: sahte saat (`FakeTimeProvider`), bariyer/kilit ve açık sıralama. Rastgele `sleep` ile "çoğu koşuda geçti" kanıt sayılmaz.
  Eşzamanlı N istek testleri sonucun her serpiştirmede aynı olması gereken değişmezleri doğrular (ör. tam olarak 1 başarı).
- Hata noktaları (failpoint) yalnız test derlemesindeki DI kaydıdır; üretim kodunda HTTP ile tetiklenen hata ayıklama kapısı yoktur.
- İzolasyon testleri bağımsız A/B veri kümesi ve kanarya değerleri (`CANARY-A-…`, `CANARY-B-…`) kullanır; B kanaryası A'nın hiçbir yüzeyinde bulunmamalıdır.

## 3. Değişmez → test eşlemesi

Sınıf adları `tests/Kulvar.IntegrationTests` (I, R) ve `tests/Kulvar.UnitTests` (U) altındadır.

### Aşama 1 — Kimlik ve üyelik
| Değişmez | Test | Katman | Beklenen |
|---|---|---|---|
| Signup yanıtı hesap varlığını sızdırmaz | `AuthTests.Signup_SameResponse_ForNewAndExistingEmail` | I | İkisi de 202, aynı gövde; tek kullanıcı satırı |
| Logout sonrası kopyalanmış cookie reddedilir | `AuthTests.Logout_RevokesSession_CopiedCookieRejected` | I | 401 |
| Güvensiz istek CSRF ister | `AuthTests.UnsafeRequest_WithoutCsrfToken_Rejected` | I | 400 `csrf_invalid` |
| Login sonrası eski CSRF token'ı geçersiz | `AuthTests.CsrfToken_FromBeforeLogin_RejectedAfterLogin` | I | 400 |
| Kilit genel yanıt verir | `AuthTests.Login_LocksAfterFailures_GenericResponse` | I | Doğru parola da 401; süre geçince 200 |
| Yetkisiz istekte yönlendirme yok | `AuthTests.Unauthenticated_Gets401ProblemDetails_NoRedirect` | I | 401 problem+json |
| Public signup platform rolü oluşturamaz | `AuthTests.Signup_CannotCreatePlatformOperator` | I+R | Alan yok sayılmaz → 400; app rolü `platform_operators` INSERT → 42501 |
| Bir kullanıcı iki şirkette ayrı rol | `TenantAccessTests.UserInTwoTenants_HasIndependentRoles` | I | A'da Agent izinleri, B'de Admin izinleri |
| Üye olmayan ve bilinmeyen şirket aynı yanıt | `TenantAccessTests.NonMember_And_UnknownTenant_SameNotFound` | I | 404, gövdeler eş |
| Kaldırılan üye eski cookie ile giremez | `TenantAccessTests.RemovedMember_OldSession_Gets404` | I | 404 |
| En az bir Owner | `MembershipTests.LastOwner_CannotLeave_Demote_OrBeRemoved` | I | 409 `last_owner` |
| Admin yükselemez / Owner'a dokunamaz | `MembershipTests.Admin_CannotEscalate_OrManageOwnerOrAdmin` | I | 403 |
| Owner rolü rol değişikliğiyle verilemez | `MembershipTests.RoleChange_ToOwner_Rejected_EvenForOwner` | I | 400 |
| Rol kuralları | `RolePolicyTests.*` | U | Tablo 02 §3 |
| E-posta normalizasyonu (Türkçe I) | `EmailNormalizerTests.*` | U | Kültürden bağımsız |

### Aşama 2 — Veritabanı izolasyonu
| Değişmez | Test | Katman | Beklenen |
|---|---|---|---|
| Kapsamsız ham SQL fail closed | `RlsSqlTests.MissingContext_SelectsNothing_WritesFail` | R | 0 satır; INSERT 42501 (RLS) |
| A kapsamında B için S/I/U/D yok | `RlsSqlTests.TenantA_CannotSelectInsertUpdateDelete_TenantB` (her kiracı tablosu) | R | 0 satır / 0 etkilenen / 42501 |
| `tenant_id` başka şirkete çevrilemez | `RlsSqlTests.Update_TenantId_ToOtherTenant_Rejected` | R | 42501 |
| Bağlantı yeniden kullanımı | `RlsSqlTests.PooledConnection_A_Then_B_Then_Missing_NoLeak` (havuz = 1) | R | Her adım yalnız kendi satırları; son adım 0 |
| Hata/iptal/yeniden deneme | `DbRunnerTests.Scope_DoesNotSurvive_Exception_Cancellation_OrRetry` | I | Sonraki iş kapsamsız başlar; yeniden deneme kapsamı yeniden kurar |
| Kapsam değişmez | `DbRunnerTests.EnterTenant_Twice_WithDifferentTenant_Throws` | I | İstisna |
| Sahip rol de okuyamaz | `RlsSqlTests.OwnerRole_SeesNoTenantRows_BecauseForced` | R | 0 satır |
| Platform rolünün içerik yetkisi yok | `RlsSqlTests.PlatformRole_DeniedOnContentTables` | R | 42501 |
| Runtime rolleri kısıtlı | `RlsSqlTests.RuntimeRoles_NotSuperuser_NotOwner_NoBypassRls` | R | `pg_roles`/`pg_class` doğrular |
| Kullanıcı kapsamı yalnız üyelik/şirket açar | `RlsSqlTests.UserScope_SeesOwnMemberships_ButNoTenantContent` | R | İçerik 0 satır |
| Şema kuralları | `SchemaConventionTests.AllTablesClassified_TenantTablesForceRls` | R | Sınıflandırılmamış tablo yok |
| Bileşik FK | `CompositeFkTests.CrossTenant_Customer_Assignee_Comment_Rejected` | R (owner+app) | 23503 |
| EF filtresi | `EfFilterTests.UnfilteredQuery_ReturnsOnlyScopedTenant` | I | Yalnız A |
| `IgnoreQueryFilters` RLS'e takılır | `EfFilterTests.IgnoreQueryFilters_StillBoundedByRls` | I | Yalnız A |
| Toplu işlemler | `EfFilterTests.ExecuteUpdate_ExecuteDelete_CannotCrossTenant` | I | B satırları değişmez |
| Trigger: en az bir Owner | `OwnerTriggerTests.RawSql_RemovingLastOwner_FailsAtCommit` | R | 23514 |
| Migration: boş DB | `MigrationTests.Applies_ToEmptyDatabase` | I | Başarılı |
| Migration: önceki aşamadan veriyle yükseltme | `MigrationTests.Upgrades_FromEachPreviousMigration_WithData` | I | Veri korunur |
| Model tutarlılığı | `MigrationTests.Model_HasNoPendingChanges` | I | Fark yok |
| Idempotent betik | `MigrationTests.IdempotentScript_AppliesTwice` | I | İkinci uygulama değişiklik yapmaz |
| Guard | `TestDatabaseGuardTests.WrongTarget_PerformsZeroWrites` | U | 0 komut |
| Yığın | `scripts/stack-smoke.ps1` | S | Boş kurulum, kalıcılık, DB kapat/aç → ready 503→200, seed iki kez, non-root |

### Aşama 3 — Müşteri ve talepler
| Değişmez | Test | Katman | Beklenen |
|---|---|---|---|
| Başka şirketin müşteri/talep kimliği | `CrossTenantRequestTests.ForeignIds_InPathAndBody_Return404` | I | 404; B verisi değişmez |
| Gövdeden `tenantId` enjeksiyonu | `CrossTenantRequestTests.TenantId_InBody_IsRejected` | I | 400 |
| B kanaryası A yanıtlarında yok | `CrossTenantRequestTests.CanaryValues_NeverAppear_InOtherTenant` | I | Liste/arama/detay/zaman çizelgesi |
| İmleç kapsamı | `PaginationTests.Cursor_FromTenantA_RejectedInTenantB_AndOtherFilter` | I | 400 `invalid_cursor` |
| Sayfalama bütünlüğü | `PaginationTests.Keyset_Pages_AreStable_AndComplete` | I | Tekrar/atlama yok |
| Sıralama/filtre izin listesi | `PaginationTests.UnknownSortOrFilter_Rejected` | I | 400 |
| Eski düzenleme | `ConcurrencyTests.StaleIfMatch_412_MissingIfMatch_428` | I | 412 / 428 |
| Eşzamanlı düzenleme kaybolmaz | `ConcurrencyTests.ParallelUpdates_SameVersion_OnlyOneWins` | I | 1× 200, diğerleri 412 |
| Idempotent oluşturma | `IdempotencyTests.Replay_ReturnsSameTicket_SingleEffect` | I | Tek talep, tek kota |
| Farklı gövde | `IdempotencyTests.SameKey_DifferentPayload_409` | I | 409 |
| Anahtar şirket/kullanıcıya özgü | `IdempotencyTests.SameKey_DifferentTenantOrUser_Independent` | I | İki ayrı talep |
| Paralel aynı anahtar | `IdempotencyTests.ParallelSameKey_SingleTicket` | I | Tek talep |
| Rollback'te kısmi veri yok | `AtomicityTests.TicketCreate_FailureAfterInsert_LeavesNothing` | I | Talep, etkinlik, outbox, kota, anahtar yok |
| Durum makinesi | `TicketWorkflowTests.*` | U | Tablo 02 §2 |
| Rol/atanan kuralları | `TicketAuthorizationTests.Agent_OnlyOwnOrUnassigned_Viewer_ReadOnly` | I | 403 |
| Atanan aktif üye olmalı | `TicketAuthorizationTests.Assignee_MustBeActiveMember_OfSameTenant` | I | 400 / 404 |
| Viewer maskeli PII görür | `CustomerTests.Viewer_SeesMaskedEmailAndPhone_AndCannotSearchByEmail` | I | Maskeli |
| Yorum düz metin | `TicketCommentTests.HtmlBody_StoredAndReturnedAsText` | I | Değişmeden, `text` alanında |
| Şirket içi tekil e-posta | `CustomerTests.Email_UniquePerTenant_NotGlobal` | I | A'da 409, B'de 201 |

### Aşama 4 — Davet ve sahiplik
| Değişmez | Test | Katman | Beklenen |
|---|---|---|---|
| Yalnız hash saklanır | `InviteTests.Token_StoredOnlyAsHash_NeverReturnedByApi` | I | DB'de ham token yok; liste yanıtında yok |
| Yanlış e-posta | `InviteTests.Accept_WithDifferentEmail_Rejected` | I | 403 `invite_email_mismatch` |
| Doğrulanmamış e-posta | `InviteTests.Accept_UnverifiedEmail_Rejected` | I | 403 |
| İptal/süre | `InviteTests.Revoked_Or_Expired_Token_Rejected` | I | 409 / 410 |
| Tek kullanım, paralel kabul | `InviteTests.ParallelAccept_SingleMembership` | I | 1 başarı, 1 üyelik |
| Rol gövdeden gelmez | `InviteTests.Accept_RejectsRoleOrTenantInBody_RoleComesFromInvite` | I | Bilinmeyen alan 400 |
| Davet koltuk yarışı | `InviteTests.ParallelInvites_RespectSeatLimit` | I | Tam olarak kalan koltuk kadar başarı |
| Kabulde koltuk yeniden kontrol | `EntitlementTests.Accept_AfterDowngrade_BlockedBySeatLimit` | I | 403 `quota_exceeded` |
| Posta kutusu yalnız alıcıya | `MailboxTests.OnlyRecipient_SeesMessage_AdminDoesNot` | I | Admin 0 ileti |
| Outbox yinelemesi tek ileti | `MailboxTests.RedeliveredOutbox_ProducesSingleMessage` | I | 1 ileti |
| Son Owner paralel | `OwnershipTests.TwoOwners_ParallelLeaveOrDemote_ExactlyOneSucceeds` | I | 1 Owner kalır |
| Teklif kabul gerektirir | `OwnershipTests.Offer_RequiresTargetAcceptance_Atomic` | I | Kabulden önce rol değişmez |
| Paralel kabul/kaldırma | `OwnershipTests.Accept_Races_WithRemoval_NeverZeroOwners` | I | Tutarlı sonuç |
| Rol kaldırma idempotency ile aşılamaz | `MembershipRevocationTests.RemovedMember_CannotReplayIdempotentResponse` | I | 404 |
| Devam eden yazma × kaldırma | `MembershipRevocationTests.Removal_WaitsForInFlightWrite_ThenBlocksNext` | I | Bariyerle sıralı |

### Aşama 5 — Plan ve kota
| Değişmez | Test | Katman | Beklenen |
|---|---|---|---|
| Son hak | `QuotaTests.LastSlot_25ConcurrentCreates_ExactlyOneSucceeds` | I | 1× 201, 24× 403; sayaç = limit |
| Şirketler bağımsız | `QuotaTests.TwoTenants_HaveIndependentCounters` | I | A dolu, B oluşturur |
| Replay kota tüketmez | `QuotaTests.IdempotentReplay_DoesNotConsumeTwice` | I | Sayaç 1 |
| Rollback sayaç bırakmaz | `QuotaTests.FailedCreate_DoesNotConsume` | I | Sayaç değişmez |
| Dönem sınırı UTC | `QuotaTests.Period_ResetsAtUtcMonthBoundary_NotLocalTime` | I | 31 23:59:59Z dolu; 1 00:00:00Z açık |
| Kapatma/yeniden açma kotayı etkilemez | `QuotaTests.CloseOrReopen_DoesNotRefundOrConsume` | I | Sayaç sabit |
| Kota yanıtı | `QuotaTests.Exceeded_Returns403Problem_WithResetAt_Not429` | I | 403, `resetsAt` |
| Downgrade × oluşturma | `EntitlementTests.Downgrade_Racing_Create_UsesCommittedPlan` | I | Bariyerle: sonuç commit sırasına uygun |
| Limit üstü veri silinmez | `EntitlementTests.Downgrade_OverLimit_KeepsData_BlocksNewSeats` | I | Üyeler durur, davet 403 |
| Özellik kapısı | `ExportTests.Export_NotInPlan_403` | I | `feature_not_in_plan` |
| Erişim modu | `AccessModeTests.Suspended_ReadOnly_OwnerCanRecover_ViewerCanRead` | I | Tablo 02 §5 |
| Erişim modu kuralları | `AccessModeRulesTests.*` | U | Matris |

### Aşama 6 — Faturalama ve webhook
| Değişmez | Test | Katman | Beklenen |
|---|---|---|---|
| İmza/zaman | `WebhookTests.InvalidSignature_OrStaleTimestamp_401_NoWrite` | I | Inbox boş |
| Boyut | `WebhookTests.OversizedBody_And_MalformedEvent_Rejected` | I | 413 |
| Yinelenen | `WebhookTests.DuplicateEvent_SingleEffect` | I | 200; tek uygulama |
| Çelişen gövde | `WebhookTests.SameId_DifferentPayload_409_Conflict` | I | Kayıt `Conflict` |
| Bilinmeyen eşleşme | `WebhookTests.UnknownMapping_NoEffect_OperatorVisible` | I | `UnknownMapping` |
| Gövdedeki tenant yok sayılır | `WebhookTests.TenantIdInBody_DoesNotRoute` | I | Eşleşme yalnız kendi kayıtlardan |
| Sırasız | `WebhookTests.OutOfOrder_OlderSequence_DoesNotRollBack` | I | Eski olay `Stale` |
| Kabul ≠ uygulandı | `PlanChangeTests.Accepted_DoesNotChangeEntitlement_UntilApplied` | I | 202 sonrası limit aynı |
| Ret | `PlanChangeTests.Declined_NoEntitlement` | I | Plan değişmez |
| Yanıt kaybı | `PlanChangeTests.Timeout_BecomesUnknown_ReconcileAppliesOnce` | I | Tek ücret, tek geçiş |
| Yeniden deneme çift ücret üretmez | `PlanChangeTests.Retry_SameMerchantReference_SingleCharge` | I | Sağlayıcıda 1 işlem |
| Geç başarı × iptal | `PlanChangeTests.LateSuccess_AfterCancel_NoReactivation_Refunded` | I | Abonelik `Cancelled`, işlem `Refunded` |
| Geç başarı × silme | `TenantDeletionTests.PendingJobs_DoNotRecreateTenantData_AndLatePaymentIsRefunded` | I | Aynı |
| Terminal çözüm | `PlanChangeTests.UnresolvedOutcome_NeedsResolution_OperatorResolvesTerminal` | I | `NeedsResolution` → `Refunded/Voided/ManuallyClosed`, denetim kaydı |
| Sonucu beklenen işlem varken ikinci istek | `PlanChangeTests.Timeout_BecomesUnknown_ReconcileAppliesOnce` (içinde) | I | `409 operation_in_progress` |
| Yeni istek eskisinin yerini alır | `PlanChangeTests.NewRequest_SupersedesOlder_OlderLateSuccessIsRefunded` | I | Eski işlemin geç başarısı iade edilir |
| İmza/işlem kuralları | `WebhookSignatureTests.*`, `BillingOperationRulesTests.*`, `SubscriptionRulesTests.*` | U | Durum tabloları |

### Aşama 7 — Worker, export, silme
| Değişmez | Test | Katman | Beklenen |
|---|---|---|---|
| Etki + outbox atomik | `OutboxTests.Rollback_LeavesNoMessage_Commit_LeavesOne` | I | — |
| Kiralama dolunca devralma | `OutboxTests.ExpiredLease_IsTakenOver_SingleEffect` | I | Tek etki |
| Fencing | `OutboxTests.StaleWorker_CompleteWithOldToken_IsRejected` | I | Eski worker etkisi geri alınır |
| Yeniden deneme / DLQ / replay | `OutboxTests.Failing_Handler_BacksOff_ThenDead_ThenReplay` | I | Sınırda `Dead`; replay sonrası `Done` |
| Kapatma | `OutboxTests.Cancellation_StopsBetweenMessages_NoLoss` | I | Kalan mesaj `Pending` |
| Gövdedeki tenant yetki üretmez | `OutboxTests.ForgedTenantInPayload_CannotReachOtherTenant` | I | "konu yok" |
| Export yetki/sahiplik | `ExportTests.Download_RequiresMember_Permission_Requester_AndToken` | I | 404/403 |
| Başka şirket URL'si | `ExportTests.Download_RequiresMember_Permission_Requester_AndToken` | I | 404 |
| Token süre/tek kullanım | `ExportTests.DownloadToken_Expires_And_IsSingleUse` | I | 403 |
| Path traversal | `ExportTests.TraversalInIdOrToken_Rejected` | I | 404/400; dosya okunmaz |
| Formül enjeksiyonu | `CsvWriterTests.FormulaCells_AreNeutralised`, `ExportTests.Artifact_ContainsNeutralisedCells_OnlyOwnTenant` | U+I | `'=` öneki; B kanaryası yok |
| Artefakt ömrü | `ExportTests.Artifact_Expires_FileRemoved` | I | 410; dosya yok |
| Silme onayı | `TenantDeletionTests.Request_RequiresOwner_NameAndPassword` | I | 403/400 |
| Yazma durur / iptal | `TenantDeletionTests.Pending_StopsWrites_CancelRestores` | I | 403 → 200 |
| Temizleme izolasyonu | `TenantDeletionTests.Purge_RemovesTenantA_LeavesTenantB_AndSharedUser` | I | B satır sayıları ve ortak kullanıcı aynı |
| Temizleme tekrar edilebilir | `TenantDeletionTests.Purge_Interrupted_ThenRepeated_Completes` | I | `Deleted`, artık satır yok |
| Silme × bekleyen iş | `TenantDeletionTests.PendingJobs_DoNotRecreateTenantData_AndLatePaymentIsRefunded` | I | İşler "konu yok" |
| Silme × export | `TenantDeletionTests.Export_NotDownloadable_AfterDeletion_FilesRemoved_AndPurgeWaitsForRunningExport` | I | 404; dizin yok |
| Adillik | `OutboxTests.Claim_LimitsPerTenant_SoOthersProgress` | I | Parti karışık şirketlerden |

### Aşama 8 — Arayüz
| Değişmez | Test | Katman | Beklenen |
|---|---|---|---|
| Önbellek anahtarı şirket+kullanıcı içerir | `queryKeys.test.ts` | C | Anahtar şekli |
| Geç gelen A yanıtı B ekranına yerleşmez | `tenantSwitch.test.tsx` | C | B ekranı yalnız B verisi |
| Logout/401 önbelleği temizler | `sessionCache.test.tsx` | C | Boş önbellek |
| Durum ekranları | `states.test.tsx` (kota, 412, askı, silme bekliyor, boş, hata) | C | Metin + eylem |
| Form hataları alana bağlı | `forms.test.tsx` | C | `aria-describedby` |
| A/B geçişi ve paralel sekme | `e2e/isolation.spec.ts` | E | Her sekme kendi şirketi; kanarya sızmaz |
| Rol görünürlüğü ve sunucu reddi | `e2e/roles.spec.ts` | E | Viewer/Agent yasak eylem 403 |
| Demo akışı | `e2e/journey.spec.ts` | E | Signup → doğrulama → şirket → davet → müşteri → talep → kota → plan değişikliği |
| Rol iptali | `e2e/revocation.spec.ts` | E | Açık sekmede sonraki istek erişimi kapatır |
| Operatör yalnız metadata | `e2e/platform.spec.ts` | E | İçerik yok |
| Erişilebilirlik | `e2e/a11y.spec.ts` (360/768/1440) | E | axe ciddi/kritik ihlal 0; klavye turu |

### Aşama 9 — Sağlamlaştırma
| Değişmez | Test | Katman | Beklenen |
|---|---|---|---|
| Logda kişisel veri yok | `ObservabilityTests.Logs_DoNotContain_PiiCanaries` | I | Kanarya e-posta/telefon/ad/token yok |
| İzde kişisel veri yok | `ObservabilityTests.Activities_DoNotContain_PiiCanaries` | I | Aynı |
| Düşük kardinalite | `ObservabilityTests.MetricTags_AreFromFixedSets` | I | `tenant`/`user`/`customer` etiketi yok |
| Operatör yalnız metadata | `PlatformTests.Operator_SeesMetadataOnly_NoContentCanaries` | I | Kanarya yok; içerik uçları 404; `kulvar_platform` rolü içerik tablolarında `42501` |
| Askıya alma | `PlatformTests.Suspend_LocksContent_MembersSeeStatusOnly_ReactivateRestores_Audited` | I | `Locked` mod; diğer şirketler etkilenmez; platform denetim kaydı |
| Operatör olmayan platformu göremez | `PlatformTests.NonOperator_Gets404_OnEveryPlatformEndpoint` | I | 404 |
| DB kesintisinde sahte başarı yok | `OutageAndTransportTests.DatabaseUnreachable_Returns503_NeverSuccess` | I | 503 (sahte 200/401 yok); toparlanma (503→200) yığın duman testinde |
| Hız sınırı | `OutageAndTransportTests.Login_Throttled_429_WithRetryAfter_DistinctFromQuota` | I | 429 |
| Güvenlik başlıkları | `OutageAndTransportTests.Responses_CarrySecurityHeaders_AndCookiesAreHardened` | I | Başlıklar |
| Üretim korumaları | `HostCompositionTests.Production_WithDemoSwitch_RefusesToStart` | I | İstisna |

## 4. Final kapıları

[08](08-implementation-plan.md) "Kapı komutları" + bu belgedeki tüm kritik testler aynı kaynak parmak izi üzerinde geçmelidir.
Sonuçlar `docs/final-verification.md` içinde komut, exit kodu, keşfedilen/geçen/başarısız/atlanan sayıları, süre ve log yolu ile kaydedilir.
İlk başarısızlık, kök neden, düzeltme ve son geçiş ayrı satırlardır.

Dürüstlük kuralları:
- Uzak CI çalıştırılmadıysa "tanımlı, uzakta çalıştırılmadı" yazılır.
- Ekran okuyucu ile elle test yapılmadıysa yazılır; axe sonucu "erişilebilir" iddiası değildir.
- Kıyaslama (benchmark) yoksa performans/verim iddiası yoktur; eşzamanlılık testi ölçeklenebilirlik kanıtı değildir.
- Tarayıcı bulgu vermemesi "güvenli" anlamına gelmez; tarama kapsamı, araç ve log yolu belirtilir.
