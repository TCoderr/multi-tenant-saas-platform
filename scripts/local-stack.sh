#!/usr/bin/env bash
# Kulvar yerel Docker yığını (Linux / macOS / Git Bash). local-stack.ps1 ile aynı davranış.
#   init [--seed-demo]            secret'lar, bağlantı dizeleri, HTTPS sertifikası (+ demo parolaları). Değerler ekrana yazılmaz.
#   up [--seed-demo] [--simulation]  build + up (sağlıklı olana kadar bekler); --seed-demo ile demo verisi (yinelenebilir).
#   seed                          yalnız demo verisi.
#   down                          durdurur; volume'lar KALIR.
#   status | logs
#   reset --confirm-reset <proje>   YIKICI: yalnız bu ürünün etiketini taşıyan volume'ları siler.
# Ortak seçenekler: --project <ad> (varsayılan kulvar), --local-dir <dizin> (.local), --port <n> (8843)
set -euo pipefail

action="${1:-}"; shift || true
project="kulvar"; local_dir=".local"; port="8843"; seed_demo="false"; simulation="false"; confirm_reset=""
while [ $# -gt 0 ]; do
  case "$1" in
    --project) project="$2"; shift 2 ;;
    --local-dir) local_dir="$2"; shift 2 ;;
    --port) port="$2"; shift 2 ;;
    --seed-demo) seed_demo="true"; shift ;;
    --simulation) simulation="true"; shift ;;
    --confirm-reset) confirm_reset="$2"; shift 2 ;;
    *) echo "Bilinmeyen seçenek: $1" >&2; exit 2 ;;
  esac
done

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
local_path="$root/$local_dir"
demo_users="ayse.owner ali.admin asli.agent arda.viewer burak.owner belma.agent cem.ortak operator"

new_secret() { head -c "${1:-24}" /dev/urandom | base64 | tr '+/' 'AB' | tr -d '=\n'; }
# Compose, dosya tabanlı secret'ları ve bağlanan dizinleri host'taki sahiplik ve izinlerle bağlar; container süreçleri (postgres, uygulama
# image'ının root olmayan kullanıcısı) host kullanıcısından farklı bir uid ile çalışır ve Linux'ta 0600 dosyayı okuyamaz (Windows/macOS
# bağlamaları izinleri uygulamadığı için orada görünmez). Bu yüzden dosyalar 0644'tür; host'taki diğer kullanıcılara karşı koruma,
# yerel dizinin kökünün 0700 olmasıdır (protect_local_dir).
write_secret() { printf '%s' "$2" > "$1"; chmod 644 "$1" 2>/dev/null || true; }   # sonda satır sonu yok
protect_local_dir() {
  chmod 700 "$local_path" 2>/dev/null || true
  chmod 755 "$local_path/secrets" "$local_path/certs" "$local_path/seed" 2>/dev/null || true
  find "$local_path/secrets" "$local_path/certs" "$local_path/seed" -type f -exec chmod 644 {} + 2>/dev/null || true
}
ensure_secret() { local path="$local_path/secrets/$1"; [ -f "$path" ] || write_secret "$path" "$(new_secret "${2:-24}")"; cat "$path"; }
compose_env() { export KULVAR_LOCAL_DIR="./${local_dir#./}" KULVAR_HTTPS_PORT="$port" KULVAR_SIMULATION="$simulation"; }
assert_initialized() { [ -f "$local_path/secrets/app_connection" ] || { echo "Önce: scripts/local-stack.sh init --local-dir $local_dir" >&2; exit 1; }; }
run_seed() {
  [ -f "$local_path/seed/Seed__Passwords__operator" ] || { echo "Demo parolaları yok. Önce: scripts/local-stack.sh init --seed-demo" >&2; exit 1; }
  docker compose -p "$project" --profile seed run --rm seed
  echo "Demo verisi hazır. Hesap/şirket eşlemesi README'de; parolalar $local_dir/seed altında (özel)."
}

case "$action" in
  init)
    mkdir -p "$local_path/secrets" "$local_path/certs" "$local_path/seed"
    ensure_secret postgres_superuser_password >/dev/null
    owner="$(ensure_secret kulvar_owner_password)"; app="$(ensure_secret kulvar_app_password)"
    platform="$(ensure_secret kulvar_platform_password)"; worker="$(ensure_secret kulvar_worker_password)"
    ensure_secret webhook_secret 36 >/dev/null
    common="Host=postgres;Port=5432;Database=kulvar;Timeout=5;Command Timeout=20;Gss Encryption Mode=Disable"
    write_secret "$local_path/secrets/migrator_connection" "$common;Username=kulvar_owner;Password=$owner"
    write_secret "$local_path/secrets/app_connection" "$common;Username=kulvar_app;Password=$app"
    write_secret "$local_path/secrets/platform_connection" "$common;Username=kulvar_platform;Password=$platform;Maximum Pool Size=10"
    write_secret "$local_path/secrets/worker_connection" "$common;Username=kulvar_worker;Password=$worker;Maximum Pool Size=10"
    if [ ! -f "$local_path/certs/kulvar-dev.pfx" ]; then
      pfx_password="$(ensure_secret https_certificate_password)"
      dotnet dev-certs https --export-path "$local_path/certs/kulvar-dev.pfx" --password "$pfx_password" --format Pfx >/dev/null
      [ -f "$local_path/certs/kulvar-dev.pfx" ] || { echo "Geliştirici sertifikası dışa aktarılamadı (dotnet dev-certs https --check)." >&2; exit 1; }
    fi
    if [ "$seed_demo" = "true" ]; then
      for user in $demo_users; do
        path="$local_path/seed/Seed__Passwords__$user"
        [ -f "$path" ] || write_secret "$path" "Kv!$(new_secret 15)aA9"
      done
    fi
    protect_local_dir   # önceki bir init'in 0600 bıraktığı dosyaları ve dışa aktarılan sertifikayı da kapsar
    echo "Hazır: $local_dir. Değerler ekrana yazılmadı."
    ;;
  up)
    assert_initialized; compose_env
    docker compose -p "$project" up -d --build --wait
    docker compose -p "$project" ps
    [ "$seed_demo" = "true" ] && run_seed
    echo "Kulvar: https://localhost:$port"
    ;;
  seed) assert_initialized; compose_env; run_seed ;;
  down) compose_env; docker compose -p "$project" --profile seed down --remove-orphans; echo "Durduruldu. Volume'lar korundu." ;;
  status) compose_env; docker compose -p "$project" ps -a ;;
  logs) compose_env; docker compose -p "$project" logs --tail 200 ;;
  reset)
    [ "$confirm_reset" = "$project" ] || { echo "Yıkıcı işlem: onay için --confirm-reset $project verin. Hiçbir şey silinmedi." >&2; exit 1; }
    # Hedef koruması: silinecek her volume bu ürünün etiketini taşımalı.
    volumes="$(docker volume ls --filter "label=com.docker.compose.project=$project" --format '{{.Name}}')"
    for volume in $volumes; do
      label="$(docker volume inspect "$volume" --format '{{ index .Labels "dev.kulvar.stack" }}')"
      [ "$label" = "true" ] || { echo "Volume '$volume' Kulvar etiketi taşımıyor; '$project' bu ürünün yığını değil. Hiçbir şey silinmedi." >&2; exit 1; }
    done
    compose_env
    docker compose -p "$project" --profile seed down -v --remove-orphans
    echo "Sıfırlandı: '$project'. Yerel sırlar ($local_dir) korunur."
    ;;
  *) echo "Kullanım: scripts/local-stack.sh {init|up|seed|down|status|logs|reset} [seçenekler]" >&2; exit 2 ;;
esac
