-- Veritabanı düzeyi, veritabanı başına bir kez (superuser, hedef veritabanına bağlıyken). 01-roles.sql'den sonra çalışır.
-- Tablo yetkileri ve RLS politikaları burada değil, migration'lardadır (şemayla birlikte sürümlenir).

ALTER DATABASE :"DBNAME" OWNER TO kulvar_owner;
REVOKE ALL ON DATABASE :"DBNAME" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"DBNAME" TO kulvar_owner, kulvar_app, kulvar_platform, kulvar_worker;

-- public şeması kullanılmaz; kimse oraya nesne oluşturamaz.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA IF NOT EXISTS app AUTHORIZATION kulvar_owner;       -- ürün tabloları
CREATE SCHEMA IF NOT EXISTS fakepsp AUTHORIZATION kulvar_owner;   -- simüle ödeme sağlayıcısının kendi durumu (dış sistemi temsil eder)
