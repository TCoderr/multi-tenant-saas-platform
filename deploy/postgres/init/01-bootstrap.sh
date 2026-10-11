#!/bin/sh
# PostgreSQL ilk başlatmada (boş volume) bir kez çalışır: roller ve veritabanı düzeyi kurulum.
# Asıl SQL db/bootstrap altındadır (testler de aynı dosyaları kullanır). Parolalar Compose secret dosyalarından okunur;
# psql değişkeni olarak geçer, komut satırına veya loga yazılmaz.
set -eu

owner_password="$(cat "$KULVAR_OWNER_PASSWORD_FILE")"
app_password="$(cat "$KULVAR_APP_PASSWORD_FILE")"
platform_password="$(cat "$KULVAR_PLATFORM_PASSWORD_FILE")"
worker_password="$(cat "$KULVAR_WORKER_PASSWORD_FILE")"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
     -v owner_password="$owner_password" -v app_password="$app_password" \
     -v platform_password="$platform_password" -v worker_password="$worker_password" \
     -f /kulvar-bootstrap/01-roles.sql

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -f /kulvar-bootstrap/02-database.sql
