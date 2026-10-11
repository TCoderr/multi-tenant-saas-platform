-- Küme düzeyi, bir kez: migration sahibi rol ve üç çalışma zamanı rolü. Superuser ile, psql değişkenleriyle çalıştırılır:
--   psql -v owner_password=... -v app_password=... -v platform_password=... -v worker_password=... -f 01-roles.sql
-- Parolalar komut satırına/loga yazılmasın diye çağıran betik bunları dosyadan okur.
-- Hiçbir rol superuser, BYPASSRLS, CREATEDB veya CREATEROLE değildir.

-- Şema sahibi: migration'ları uygular. Tablolarda FORCE ROW LEVEL SECURITY olduğu için kiracı satırlarını okuyamaz.
CREATE ROLE kulvar_owner    LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION PASSWORD :'owner_password';
-- API: kullanıcı/şirket kapsamlı istekler.
CREATE ROLE kulvar_app      LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION PASSWORD :'app_password';
-- API: platform operatörü uçları ve webhook alımı. İçerik tablolarında yetkisi yoktur.
CREATE ROLE kulvar_platform LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION PASSWORD :'platform_password';
-- Worker: kuyruk/metadata tablolarını şirketler arası görür; içerik için şirket kapsamı ayarlamak zorundadır.
CREATE ROLE kulvar_worker   LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION PASSWORD :'worker_password';
