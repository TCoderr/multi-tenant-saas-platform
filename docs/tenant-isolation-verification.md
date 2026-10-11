# Kiracı İzolasyonu Doğrulaması

Bu belge "A şirketinin kullanıcısı B şirketinin verisine ulaşamaz" iddiasının **hangi yüzeyde, hangi testle ve hangi veritabanı rolüyle**
doğrulandığını listeler. Yalnız gerçekten koşan testler yazılmıştır; koşu sonuçları (sayılar, parmak izi) [final-verification.md](final-verification.md) içindedir.

## Yöntem

Aşağıdaki tablolarda aksi yazmadıkça: **rol** = HTTP testlerinde uygulamanın kendi bağlantıları (`kulvar_app`; operatör uçları ve webhook için `kulvar_platform`;
arka plan işleri için `kulvar_worker`), ham SQL testlerinde adı geçen rolün doğrudan bağlantısı. **Sonuç** = final koşuda hepsi geçti (0 başarısız, 0 atlanan).
Her bölümün doğruladığı sınır başlığında, doğrulamadığı şeyler belgenin sonundadır.

- **Gerçek PostgreSQL 18** (Testcontainers; her koşuda tek kullanımlık kapsayıcı). SQLite veya EF InMemory kullanılmaz.
- Uygulama testte de üretimdeki **kısıtlı rollerle** bağlanır: `kulvar_app`, `kulvar_platform`, `kulvar_worker`. Hiçbiri superuser, tablo sahibi
  veya `BYPASSRLS` değildir (`RlsSqlTests.RuntimeRoles_NotSuperuser_NotOwner_NoBypassRls` bunu katalogdan okur).
  Superuser yalnız testin kurulum/teyit sorgularında kullanılır (ör. "B'nin satırı gerçekten değişmedi mi?").
- **İki şirket, kanarya değerler:** A ve B şirketleri ayrı kullanıcılarla kurulur; her birinin müşteri/talep/yorum alanlarına benzersiz kanarya
  dizgiler yazılır. Testler yalnız "404 döndü" demez; yanıt gövdelerinde ve dışa aktarılan dosyalarda **diğer şirketin kanaryasının geçmediğini** de arar.
- **Katmanlar ayrı ayrı sınanır:** uygulama kontrolü atlatıldığında (ham SQL, `IgnoreQueryFilters`) RLS'in, RLS devre dışıyken (superuser) EF filtresinin tek başına tuttuğu gösterilir.
- **Deterministik yarışlar:** sahte saat (`FakeTimeProvider`) ve açık bariyerler; rastgele bekleme yok.
- Test hedefi koruması: testler yalnız açık opt-in + kendi oluşturduğu işaret tablosu + sunucu kimliği eşleşirse yazar
  (`TestDatabaseGuard`; yanlış hedefte sıfır yazma birim testlerle doğrulanır).

## Doğrulanan yüzeyler

### 1. HTTP istekleri (kimlik → üyelik → izin)

| İddia | Test |
|---|---|
| İki kullanıcı, iki şirket: birbirini göremez | `TenantAccessTests.TwoUsers_TwoTenants_CannotSeeEachOther` |
| Üye olunmayan şirket ile var olmayan şirket **aynı** `404` yanıtını verir (varlık sızmaz) | `TenantAccessTests.NonMember_And_UnknownTenant_SameNotFound` |
| Başlık/sorgu/gövdedeki şirket kimliği yetki sayılmaz; gövdede `tenantId` reddedilir | `TenantAccessTests.TenantId_FromHeaderOrQuery_IsNotAuthority`, `CrossTenantRequestTests.TenantId_InBody_IsRejected` |
| Kendi şirketinin URL'sinde başka şirketin kaynak kimliği (yol ve gövde) → `404` | `CrossTenantRequestTests.ForeignIds_InPathAndBody_Return404` |
| Kanarya değerler diğer şirketin hiçbir liste/detay/arama yanıtında görünmez | `CrossTenantRequestTests.CanaryValues_NeverAppear_InOtherTenant` |
| Aynı kullanıcı iki şirkette bağımsız rol taşır | `TenantAccessTests.UserInTwoTenants_HasIndependentRoles` |
| Üyelikten çıkarılan kullanıcının **açık oturumu** bir sonraki istekte `404` alır | `TenantAccessTests.RemovedMember_OldSession_Gets404` |
| Rol düşürme bir sonraki istekte geçerlidir (oturumda önbelleklenmiş yetki yok) | `TenantAccessTests.RoleDowngrade_TakesEffectOnNextRequest` |
| Çıkarma, süren yazmayı bekler; sonraki yazma reddedilir | `MembershipRevocationTests.Removal_WaitsForInFlightWrite_ThenBlocksNext` |

### 2. Veritabanı: satır düzeyi güvenlik (RLS) — ham SQL, uygulama kodu olmadan

| İddia | Test |
|---|---|
| Kapsam ayarlanmamışsa okuma boş döner, yazma hata verir (kapalı başarısızlık) | `RlsSqlTests.MissingContext_SelectsNothing_WritesFail`, `…MissingContext_GlobalAndQueueTables_AlsoFailClosed` |
| Bozuk kapsam değeri hata verir, "herkes" anlamına gelmez | `RlsSqlTests.InvalidContextValue_FailsClosed_WithError` |
| A kapsamında B'nin satırı seçilemez, eklenemez, güncellenemez, silinemez (`USING` + `WITH CHECK`) | `RlsSqlTests.TenantA_CannotSelectInsertUpdateDelete_TenantB` |
| Bir satırın `tenant_id`'si başka şirkete çevrilemez | `RlsSqlTests.Update_TenantId_ToOtherTenant_Rejected` |
| Havuzdaki bağlantı A → B → kapsamsız sırayla kullanıldığında sızıntı yok (kapsam işlem ömürlü) | `RlsSqlTests.PooledConnection_A_Then_B_Then_Missing_NoLeak` |
| Tablo sahibi (`kulvar_owner`, migration rolü) bile kiracı satırı göremez (`FORCE`) | `RlsSqlTests.OwnerRole_SeesNoTenantRows_BecauseForced` |
| Kullanıcı kapsamı yalnız kendi üyeliklerini görür; şirket içeriğini, başkasının oturum/token'ını görmez | `RlsSqlTests.UserScope_SeesOwnMemberships_ButNoTenantContent`, `…UserScope_OtherUsersSessionsAndTokens_Invisible` |
| Giriş araması yalnız aranan e-postanın satırını açar | `RlsSqlTests.LoginScope_RevealsOnlyThatEmailRow` |
| **Her** tablo sınıflandırılmıştır; kiracı tablolarının tümünde RLS etkin + zorlanmış | `SchemaConventionTests.AllTablesClassified_TenantTablesForceRls` |
| `SECURITY DEFINER` fonksiyon yok; yardımcılar `search_path` sabitler | `SchemaConventionTests.NoSecurityDefinerFunctions_AndHelpersPinSearchPath` |

### 3. Veritabanı: bileşik yabancı anahtarlar

| İddia | Test |
|---|---|
| A'nın talebi B'nin müşterisine/üyesine, A'nın yorumu B'nin talebine bağlanamaz | `CompositeFkTests.CrossTenant_Customer_Assignee_Comment_Rejected` |
| Satırdaki `tenant_id` doğru olsa bile A kapsamındaki rol B'nin satırına bağlanamaz | `CompositeFkTests.AppRole_InScopeA_CannotBindToTenantB_EvenWithCorrectTenantIdOnRow` |

### 4. EF Core global filtresi ve iş birimi (DbRunner)

| İddia | Test |
|---|---|
| Filtresiz yazılmış sorgu yalnız kapsamdaki şirketi döndürür | `EfFilterTests.UnfilteredQuery_ReturnsOnlyScopedTenant` |
| EF filtresi RLS olmadan da (superuser bağlantısı) tek başına sınırlar | `EfFilterTests.EfFilter_Alone_ScopesQueries_EvenForSuperuser` |
| `IgnoreQueryFilters` ve kapsam içi ham SQL yine RLS ile sınırlıdır | `EfFilterTests.IgnoreQueryFilters_StillBoundedByRls`, `…RawSql_InScope_StillBoundedByRls` |
| Toplu `ExecuteUpdate/ExecuteDelete` başka şirkete geçemez | `EfFilterTests.ExecuteUpdate_ExecuteDelete_CannotCrossTenant` |
| Başka şirketin kimliğiyle izlenen varlık eklenemez | `EfFilterTests.TrackedEntity_WithForeignTenantId_CannotBeInserted` |
| Kapsam; istisna, iptal ve yeniden denemeden sonra yaşamaz | `DbRunnerTests.Scope_DoesNotSurvive_Exception_Cancellation_OrRetry` |
| Aynı iş biriminde ikinci bir şirkete geçilemez; iş birimi dışında ham SQL reddedilir | `DbRunnerTests.EnterTenant_Twice_WithDifferentTenant_Throws`, `…RawSql_OutsideUnitOfWork_IsRefused` |

### 5. Rol ayrımı (uygulama hatası olsa bile)

| İddia | Test |
|---|---|
| Platform rolünün içerik tablolarında yetkisi yoktur (`42501`) | `RlsSqlTests.PlatformRole_DeniedOnContentTables`, `PlatformTests.Operator_SeesMetadataOnly_NoContentCanaries` |
| API rolü platform tablolarına ve sağlayıcı durumuna erişemez | `RlsSqlTests.AppRole_DeniedOnPlatformTables_AndProviderState` |
| Worker kuyruğu görür ama içerik için şirket kapsamı ayarlamak zorundadır | `RlsSqlTests.WorkerRole_NeedsTenantScope_ForContent_ButSeesQueue`, `HostCompositionTests.WorkerHost_ServiceGraph_Builds_AndProcessesWithWorkerRoleOnly` |
| Platform operatörü: yalnız metadata; şirket uçları `404`; kimliğe bürünme ucu yok; operatör olmayan için tüm platform uçları `404` | `PlatformTests.Operator_SeesMetadataOnly_NoContentCanaries`, `PlatformTests.NonOperator_Gets404_OnEveryPlatformEndpoint` |
| Kayıt akışı platform operatörü üretemez; yalnız kurulum komutu verir | `AuthTests.Signup_CannotCreatePlatformOperator`, `HostCompositionTests.DemoSeed_IsIdempotent_AndOperatorIsGrantedOnlyBySetupCommand` |

### 6. Şirket kapsamlı yardımcı durumlar

| Yüzey | İddia | Test |
|---|---|---|
| Idempotency anahtarı | Aynı anahtar farklı şirket/kullanıcıda bağımsızdır | `IdempotencyTests.SameKey_DifferentTenantOrUser_Independent` |
| Idempotency yanıtı | Çıkarılan üye eski yanıtı tekrar oynatamaz | `MembershipRevocationTests.RemovedMember_CannotReplayIdempotentResponse` |
| Sayfalama imleci | A'dan alınan imleç B'de (ve başka filtrede) reddedilir | `PaginationTests.Cursor_FromTenantA_RejectedInTenantB_AndOtherFilter` |
| Kota sayaçları | İki şirketin sayaçları bağımsızdır | `QuotaTests.TwoTenants_HaveIndependentCounters` |
| Müşteri e-postası tekilliği | Şirket başına; küresel değil (varlık sızmaz) | `CustomerTests.Email_UniquePerTenant_NotGlobal` |
| Davet | Başka şirketin davet kimliğiyle işlem yapılamaz; rol/şirket gövdeden değil davetten gelir | `InviteTests.InviteRules_RoleLimits_Duplicates_AndCrossTenant`, `InviteTests.Accept_RejectsRoleOrTenantInBody_RoleComesFromInvite` |
| Demo posta kutusu | İletiyi yalnız alıcısı görür; şirket yöneticisi göremez | `MailboxTests.OnlyRecipient_SeesMessage_AdminDoesNot` |

### 7. Arka plan işleri ve webhook

| İddia | Test |
|---|---|
| Outbox yükünde sahte şirket kimliği başka şirkete ulaşamaz | `OutboxTests.ForgedTenantInPayload_CannotReachOtherTenant` |
| Bir şirketin iş yığını diğerlerini aç bırakmaz (şirket başına parti sınırı) | `OutboxTests.Claim_LimitsPerTenant_SoOthersProgress` |
| Webhook gövdesindeki şirket kimliği yönlendirmede kullanılmaz; eşleme kendi kayıtlarımızdan | `WebhookTests.TenantIdInBody_DoesNotRoute`, `WebhookTests.UnknownMapping_NoEffect_OperatorVisible` |
| Webhook bir şirket ucu değildir; cookie/CSRF ile yetki kazanmaz | `WebhookTests.Webhook_IgnoresCookiesAndCsrf_AndIsNotATenantEndpoint` |

### 8. Dışa aktarma dosyaları

| İddia | Test |
|---|---|
| Dosya yalnız kendi şirketinin verisini içerir (B kanaryası yok); formül hücreleri etkisizleştirilir | `ExportTests.Artifact_ContainsNeutralisedCells_OnlyOwnTenant` |
| İndirme: oturum + üyelik + izin + **işi isteyen kullanıcı** + token; biri eksikse olmaz (B üyesi A'nın URL'siyle indiremez) | `ExportTests.Download_RequiresMember_Permission_Requester_AndToken` |
| Token kısa ömürlü ve tek kullanımlıktır | `ExportTests.DownloadToken_Expires_And_IsSingleUse` |
| Kimlik/token içinde yol geçişi reddedilir (dosya yolu yalnız GUID'lerden kurulur) | `ExportTests.TraversalInIdOrToken_Rejected` |
| Şirket silinince dosyalar silinir ve indirilemez | `TenantDeletionTests.Export_NotDownloadable_AfterDeletion_FilesRemoved_AndPurgeWaitsForRunningExport` |

### 9. Şirket silme

| İddia | Test |
|---|---|
| A temizlenir; B ve iki şirkette ortak kullanıcı etkilenmez | `TenantDeletionTests.Purge_RemovesTenantA_LeavesTenantB_AndSharedUser` |
| Bekleyen işler silinen şirketin verisini yeniden yaratmaz | `TenantDeletionTests.PendingJobs_DoNotRecreateTenantData_AndLatePaymentIsRefunded` |

### 10. Log, iz ve metrik

| İddia | Test |
|---|---|
| Loglarda ve izlerde kişisel veri/sır kanaryası yok (e-posta, ad, telefon, not, parola, oturum cookie'si, davet/doğrulama/indirme token'ı, webhook sırrı) | `ObservabilityTests.Logs_DoNotContain_PiiCanaries`, `ObservabilityTests.Activities_DoNotContain_PiiCanaries` |
| Metrik etiketleri sabit, düşük kardinaliteli kümelerdendir; şirket/kullanıcı/müşteri kimliği etiket değildir | `ObservabilityTests.MetricTags_AreFromFixedSets` |

Log testinin gerçekten yakaladığı ayrıca sınandı: istek loguna sorgu dizesi bilerek eklendiğinde test, sızan indirme token'ını yakalayıp kırıldı; değişiklik geri alındı.

### 11. Tarayıcı / istemci

Bileşen testleri (Vitest, jsdom) ve gerçek tarayıcı testleri (Playwright, kurulu Chrome; izole Docker yığınına karşı):

| İddia | Test |
|---|---|
| Önbellek anahtarı her zaman `['u', kullanıcı, 't', şirket, …]`; kimliksiz anahtar kurulamaz | `queryKeys.test.ts` (5 test) |
| A şirketinin **geç gelen** yanıtı B ekranına düşmez (iptal sinyali yok sayılsa bile) | `tenantSwitch.test.tsx` › "A şirketinin geç gelen yanıtı B şirketinin ekranına düşmez" |
| Şirket yalnız URL'den okunur; bellekte "aktif şirket" yok; yerel form durumu şirketler arasında taşınmaz | `tenantSwitch.test.tsx` |
| Çıkış, 401 ve kullanıcı değişiminde önbellek tamamen temizlenir | `sessionCache.test.tsx` (5 test) |
| Üyelik kaldırılınca içerik ekrandan **ve bellekten** kalkar; rol düşünce önceki rolle alınmış veri (maskesiz e-posta) atılır | `states.test.tsx` |
| İki şirketin üyesi her şirkette yalnız o şirketin verisini görür | `e2e/isolation.spec.ts` |
| Başka şirketin adresi "bulunamadı" verir; kimlik tahmini veri sızdırmaz | `e2e/isolation.spec.ts` |
| İki sekme, iki şirket: yenileme ve gezinme sekmeleri karıştırmaz | `e2e/isolation.spec.ts` |
| Çıkıştan sonra aynı tarayıcıda giren ikinci kullanıcı öncekinin verisini görmez | `e2e/isolation.spec.ts` |
| Rol düşürme ve üyelikten çıkarma **açık oturuma** hemen yansır | `e2e/revocation.spec.ts` |
| Operatör şirket metadata'sını görür; müşteri ve talep içeriğine ulaşamaz; operatör olmayan için platform ekranı yok | `e2e/platform.spec.ts` |
| Viewer kişisel veriyi maskeli görür; roller yalnız izinli eylemleri yapabilir | `e2e/roles.spec.ts` |
| Oturum belirteci tarayıcı deposuna yazılmaz | ESLint kuralı (`localStorage`/`sessionStorage` yasak) — `npm run lint` kapısı |

Bu yüzeyde bulunan ve düzeltilen gerçek hata: rolü Agent'tan Viewer'a düşürülen kullanıcının ekranında, önceki rolle önbelleğe alınmış **maskesiz müşteri e-postası** kalıyordu
(sunucu yeni isteklerde maskeli döndürüyordu; sızıntı yalnız istemci belleğindeydi). Rol, yetki listesi veya erişim modu değişince o şirketin içerik önbelleği artık atılıyor;
hem bileşen testi hem tarayıcı testi bunu kapsıyor.

Sınır: tarayıcıdaki "geç gelen yanıt" testi, şirket değişince isteğin iptal edilmesi nedeniyle gerçekten geç gelen yanıtı üretmez; o durumu bileşen testi kapsar.

### 12. Çalışan Docker yığını

`scripts/stack-smoke.ps1`, derlenmiş imaj ve gerçek Compose yığını üzerinde şunları doğrular: API ve worker'ın bağlandığı roller superuser/sahip değildir,
kapsayıcılar yetkisiz kullanıcıyla ve salt-okunur kök dosya sistemiyle çalışır, imajda sır dosyası yoktur, ve veritabanı kapatılıp açıldığında hazır olma ucu `503 → 200` döner.

## Kapsam dışında kalanlar (dürüst sınırlar)

- Testler mantıksal izolasyonu doğrular; **yük altında** davranış ölçülmedi (kıyaslama yok ⇒ performans/verim iddiası yok).
- RLS politikaları her tablo için sınıflandırma testiyle zorlanır, ancak politika ifadelerinin biçimsel (formal) ispatı yoktur; güvence katmanlı savunmadan ve negatif testlerden gelir.
- Yedekler, replikalar ve log toplama altyapısı bu projede yoktur; dolayısıyla onların izolasyonu doğrulanmamıştır.
- Zamanlama yan kanalları (ör. yanıt süresinden varlık çıkarımı) ölçülmedi; yalnız yanıt gövdesi ve durum kodu eşitliği doğrulandı.
