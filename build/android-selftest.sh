#!/usr/bin/env bash
# Android self-test on the CI emulator: installs Glance, opens it in demo and real mode, dark and light theme,
# checks for crashes, verifies that the previous release updates in place (same signature), saves screenshots.
# Usage: android-selftest.sh <apk> <out-dir>
set -u
APK="$1"; OUT="$2"; PKG=io.github.ddedlish.glance; ACT="$PKG/$PKG.MainActivity"
mkdir -p "$OUT"; REPORT="$OUT/report.txt"; : > "$REPORT"; ERR=0; N=0
note() { echo "$*" | tee -a "$REPORT"; }
fail() { note "ОШИБКА: $*"; ERR=$((ERR+1)); }

note "Glance Android self-test, $(adb shell getprop ro.build.version.release | tr -d '\r') (API $(adb shell getprop ro.build.version.sdk | tr -d '\r'))"
adb shell settings put global window_animation_scale 0 >/dev/null 2>&1
adb shell settings put global transition_animation_scale 0 >/dev/null 2>&1

# 1. Update in place: install the previous Android release first (if one exists), then this build over it.
PREV=""
if [ -n "${GITHUB_REPOSITORY:-}" ]; then
  PREV=$(curl -fsSL -H "Authorization: Bearer ${GH_TOKEN:-}" "https://api.github.com/repos/$GITHUB_REPOSITORY/releases?per_page=30" 2>/dev/null \
    | python3 -c "import json,sys
try:
  for r in json.load(sys.stdin):
    if r.get('tag_name','').startswith('android-v'):
      a=[x['browser_download_url'] for x in r.get('assets',[]) if x['name'].endswith('.apk')]
      if a: print(a[0]); break
except Exception: pass" || true)
fi
if [ -n "$PREV" ] && curl -fsSL -o /tmp/prev.apk "$PREV"; then
  if adb install -r /tmp/prev.apk >/dev/null 2>&1; then
    if adb install -r -g "$APK" 2>&1 | tee -a "$REPORT" | grep -q Success; then note "update: предыдущая версия обновилась на новую без переустановки"
    else fail "новая версия не ставится поверх предыдущей (другая подпись?)"; adb uninstall $PKG >/dev/null 2>&1; fi
  else note "update: предыдущую версию поставить не удалось, пропуск"; fi
else note "update: предыдущей Android-версии нет, пропуск"; fi

adb install -r -g "$APK" 2>&1 | tail -1 | tee -a "$REPORT" | grep -q Success || { fail "установка"; exit 1; }

step() { # name, theme(yes/no), extra am args...
  local name="$1" night="$2"; shift 2
  adb shell cmd uimode night "$night" >/dev/null 2>&1
  adb shell am force-stop $PKG
  adb logcat -c
  local t0=$(date +%s%3N)
  local out; out=$(adb shell am start -W -n "$ACT" "$@" 2>&1)
  sleep 4
  local t1=$(date +%s%3N)
  N=$((N+1)); local file=$(printf "%02d-%s.png" $N "$name")
  adb exec-out screencap -p > "$OUT/$file"
  local total=$(echo "$out" | grep -oE 'TotalTime: [0-9]+' | grep -oE '[0-9]+')
  note "$name: запуск ${total:-?} мс"
  if adb logcat -d | grep -E "FATAL EXCEPTION|AndroidRuntime: Process: $PKG|$PKG.*(crash|ANR)" >/dev/null; then fail "$name: падение"; adb logcat -d | grep -A30 "FATAL EXCEPTION" | head -60 >> "$REPORT"; fi
  if ! adb logcat -d -s Glance:I | grep -q "drawn:"; then fail "$name: экран не нарисован"; fi
  if [ -z "$(adb shell pidof $PKG | tr -d '\r')" ]; then fail "$name: приложение закрылось"; fi
}

step dark-demo yes --ez demo true
step dark-demo-update yes --ez demo true --es fakeupdate 9.9.9
step light-demo no --ez demo true
step dark-real yes
step light-real no
adb logcat -d > "$OUT/logcat.txt" 2>/dev/null
note $([ $ERR -eq 0 ] && echo "ИТОГ: OK" || echo "ИТОГ: ошибок $ERR")
exit $([ $ERR -eq 0 ] && echo 0 || echo 1)
