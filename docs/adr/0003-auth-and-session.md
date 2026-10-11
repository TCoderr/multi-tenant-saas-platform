# ADR-0003 — Kimlik doğrulama ve oturum

Durum: Kabul edildi (2026-10-10)

## Bağlam
İstemci yalnız aynı origin'den sunulan React SPA'dır. Gereksinimler: logout ve üyelik kaldırmanın **anında** etkili olması,
tarayıcıda çalınabilir token tutmamak, CSRF koruması, login yönlendirmesi yerine ProblemDetails.

## Karar
- Aynı origin, `__Host-` önekli HttpOnly + Secure + SameSite=Lax oturum cookie'si. Cookie yalnız oturum ve kullanıcı kimliğini taşır.
- Sunucu tarafı `sessions` tablosu; her istekte okunur. Logout satırı iptal eder.
- Rol/üyelik cookie'de değil, her istekte veritabanında.
- CSRF: ASP.NET Core Antiforgery; token kimliğe bağlıdır, login/logout sonrası yenilenir.
- Parola: ASP.NET Core Identity'nin `PasswordHasher<T>` sınıfı (PBKDF2). Kullanıcı deposu, kilit ve oturum mantığı bu ürüne özgü, küçük ve testlidir.

## JWT alternatifine karşı gerekçe

| | Cookie + sunucu oturumu (seçilen) | Tarayıcıda JWT |
|---|---|---|
| Saklama | HttpOnly; JS okuyamaz | `localStorage` (XSS ile çalınır) veya yine cookie (o zaman avantaj kalmaz) |
| İptal | Satır iptali, anında | Süre dolana kadar geçerli; anında iptal için yine sunucu tarafı liste gerekir |
| Rol değişimi | Her istekte DB | Token yenilenene kadar eski claim |
| CSRF | Gerekli (Antiforgery ile çözüldü) | Başlıkta taşınırsa gerekmez |
| İstek başına maliyet | 1 birincil anahtar okuması | İmza doğrulama (DB yok) |
| Üçüncü taraf/mobil istemci | Uygun değil | Uygun |

Tek istemci aynı origin SPA olduğu ve anında iptal şart olduğu için cookie seçildi. İstek başına oturum okumasının maliyeti kabul edildi; ölçülmedi.

## Tam ASP.NET Core Identity neden değil
`UserManager`/EF store'ları global rol/claim tablolarıyla gelir; bu üründe rol şirkete özgüdür ve kimlik tabloları RLS altındadır.
Gereken parça (parola hash'i) çerçeveden kullanılır. Bedeli: kilit ve oturum mantığı elle yazılıp test edilir.

## Kapsam dışı (ayrı IAM ürünü geliştirilmez)
MFA, parola sıfırlama, harici giriş (OIDC/SAML), cihaz yönetimi, hesap silme.

## Ne zaman değişir
Mobil/üçüncü taraf istemci veya kurumsal SSO gereksinimi → harici IdP + kısa ömürlü token; oturum tablosu yenileme token'ı deposuna dönüşür.
