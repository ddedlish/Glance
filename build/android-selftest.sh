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

shade() { # name — pulls down the notification shade with the always-on «Заряд в шторке» notification
  local name="$1"
  # wait until the notification is really posted (the service coalesces updates for 400 ms)
  for k in $(seq 1 20); do adb shell dumpsys notification 2>/dev/null | grep -qE "pkg=$PKG .*id=1 " && break; sleep 0.5; done
  adb shell dumpsys notification 2>/dev/null | grep -qE "pkg=$PKG .*id=1 " || note "$name: уведомления Glance нет в списке системы"
  sleep 1
  adb shell cmd statusbar expand-notifications >/dev/null 2>&1
  sleep 2
  N=$((N+1)); adb exec-out screencap -p > "$OUT/$(printf "%02d-%s.png" $N "$name")"
  adb shell cmd statusbar collapse >/dev/null 2>&1
  if adb logcat -d -s Glance:I | grep -q "shade:"; then note "$name: уведомление в шторке есть"
  else fail "$name: уведомление в шторке не появилось"; adb logcat -d | grep -iE "glance|ForegroundService" | tail -20 >> "$REPORT"; fi
}

bottom() { # name — scrolls the screen to the end (phone card, «Заряд в шторке» switch)
  local name="$1"
  local size; size=$(adb shell wm size | grep -oE '[0-9]+x[0-9]+' | tail -1)
  local W=${size%x*} H=${size#*x}
  for k in 1 2 3; do adb shell input swipe $((W/2)) $((H*4/5)) $((W/2)) $((H/6)) 250 >/dev/null 2>&1; sleep 0.4; done
  sleep 1.5
  N=$((N+1)); adb exec-out screencap -p > "$OUT/$(printf "%02d-%s.png" $N "$name")"
  note "$name: прокрутка до конца"
}

step dark-demo yes --ez demo true
shade dark-shade
bottom dark-demo-bottom
step dark-demo-update yes --ez demo true --es fakeupdate 9.9.9
step dark-demo-download yes --ez demo true --es fakeupdate 9.9.9 --ef fakeprogress 0.42
step light-demo-installing no --ez demo true --es fakeupdate 9.9.9 --ef fakeprogress 0.7 --es fakestage installing
download() { # name — a real in-app download of the previous release APK: downloaded, checked, refused as not newer
  local name="$1"
  if [ -z "$PREV" ]; then note "$name: предыдущей версии нет, пропуск"; return; fi
  adb shell cmd uimode night yes >/dev/null 2>&1
  adb shell am force-stop $PKG
  adb logcat -c
  adb shell am start -W -n "$ACT" --ez demo true --es fakeupdate 9.9.9 --es fakeurl "$PREV" >/dev/null 2>&1
  for k in $(seq 1 60); do adb logcat -d -s Glance:I | grep -qE "update: (Failed|Ready|NeedPermission)" && break; sleep 0.5; done
  sleep 1
  N=$((N+1)); adb exec-out screencap -p > "$OUT/$(printf "%02d-%s.png" $N "$name")"
  adb logcat -d -s Glance:I | grep "update:" | sed 's/^/  /' | tail -8 >> "$REPORT"
  if adb logcat -d -s Glance:I | grep -q "update: downloaded" && adb logcat -d -s Glance:I | grep -q "update: verify"; then note "$name: APK скачан внутри приложения и проверен"
  else note "$name: скачать APK на эмуляторе не удалось (сеть?)"; fi
  if adb logcat -d | grep -E "FATAL EXCEPTION" >/dev/null; then fail "$name: падение"; adb logcat -d | grep -A30 "FATAL EXCEPTION" | head -60 >> "$REPORT"; fi
}
download dark-demo-download-real
step light-demo no --ez demo true
shade light-shade

settings_back() { # «Настройки»: the system back gesture closes the page and returns to the accessories
  adb shell input keyevent KEYCODE_BACK
  sleep 1.5
  N=$((N+1)); adb exec-out screencap -p > "$OUT/$(printf "%02d-%s.png" $N "$1")"
  if adb logcat -d -s Glance:I | grep -q "settings: closed" && [ -n "$(adb shell pidof $PKG | tr -d '\r')" ]; then note "$1: «Назад» закрывает настройки"
  else fail "$1: «Назад» не закрыл настройки"; adb logcat -d -s Glance:I | grep settings | tail -5 >> "$REPORT"; fi
}
step dark-settings yes --ez demo true --ez settings true
adb logcat -d -s Glance:I | grep -q "drawn: settings" && note "dark-settings: экран настроек нарисован" || fail "dark-settings: экран настроек не нарисован"
bottom dark-settings-bottom
settings_back dark-settings-back
step light-settings no --ez demo true --ez settings true
popup() { # name — the glass card that slides up over any app the moment earbuds connect
  local name="$1"
  adb shell appops set $PKG SYSTEM_ALERT_WINDOW allow >/dev/null 2>&1
  adb shell cmd uimode night yes >/dev/null 2>&1
  adb shell am force-stop $PKG
  adb logcat -c
  adb shell am start -W -n "$ACT" --ez demo true --ez popup true >/dev/null 2>&1
  sleep 0.6
  adb shell input keyevent KEYCODE_HOME
  # wait for the card itself (the service shows it ~1 s after start), then let it slide in
  for k in $(seq 1 30); do adb logcat -d -s Glance:I | grep -q "popup shown:" && break; sleep 0.3; done
  sleep 1.2
  N=$((N+1)); adb exec-out screencap -p > "$OUT/$(printf "%02d-%s.png" $N "$name")"
  local win; win=$(adb shell dumpsys window windows 2>/dev/null | grep -cE "ty=APPLICATION_OVERLAY|TYPE_APPLICATION_OVERLAY|type=2038" )
  sleep 1.5
  N=$((N+1)); adb exec-out screencap -p > "$OUT/$(printf "%02d-%s-2.png" $N "$name")"
  note "$name: окон поверх приложений: ${win:-?}"
  if adb logcat -d -s Glance:I | grep -q "popup shown:"; then note "$name: окно при подключении показано"
  else fail "$name: окно при подключении не появилось"; adb shell appops get $PKG SYSTEM_ALERT_WINDOW >> "$REPORT" 2>&1; fi
  adb logcat -d -s Glance:I | grep -E "popup|announce|sound" | sed 's/^/  /' | tail -12 >> "$REPORT"
  if adb logcat -d -s Glance:I | grep -qE "sound: Connect (played|skipped)"; then note "$name: звук подключения обработан"
  else fail "$name: звук подключения не обработан"; fi
  if adb logcat -d -s Glance:E | grep -q "sound failed"; then fail "$name: звук не воспроизвёлся"; adb logcat -d -s Glance:E | grep "sound failed" | head -3 >> "$REPORT"; fi
  if adb logcat -d | grep -E "FATAL EXCEPTION" >/dev/null; then fail "$name: падение"; adb logcat -d | grep -A30 "FATAL EXCEPTION" | head -60 >> "$REPORT"; fi
}

notices() { # name — «пора зарядить» and «заряжено полностью» notifications (demo: Galaxy Watch6 18 %, Keychron charging)
  local name="$1"
  adb shell cmd uimode night yes >/dev/null 2>&1
  adb shell am force-stop $PKG
  adb logcat -c
  adb shell am start -W -n "$ACT" --ez demo true --ez notice true >/dev/null 2>&1
  for k in $(seq 1 30); do adb logcat -d -s Glance:I | grep -q "notice: low" && break; sleep 0.3; done
  adb shell input keyevent KEYCODE_HOME
  sleep 1
  adb shell cmd statusbar expand-notifications >/dev/null 2>&1
  sleep 2
  N=$((N+1)); adb exec-out screencap -p > "$OUT/$(printf "%02d-%s.png" $N "$name")"
  adb shell cmd statusbar collapse >/dev/null 2>&1
  adb logcat -d -s Glance:I | grep -E "notice" | sed 's/^/  /' | tail -6 >> "$REPORT"
  if adb logcat -d -s Glance:I | grep -q "notice: low" && adb logcat -d -s Glance:I | grep -q "notice: full"; then note "$name: уведомления о низком заряде и полной зарядке показаны"
  else fail "$name: уведомления о заряде не появились"; adb logcat -d -s Glance:E | grep -i notice | head -5 >> "$REPORT"; fi
  if adb logcat -d | grep -E "FATAL EXCEPTION" >/dev/null; then fail "$name: падение"; adb logcat -d | grep -A30 "FATAL EXCEPTION" | head -60 >> "$REPORT"; fi
}

notices dark-notices
popup dark-popup
step dark-real yes
step light-real no
adb logcat -d > "$OUT/logcat.txt" 2>/dev/null
note $([ $ERR -eq 0 ] && echo "ИТОГ: OK" || echo "ИТОГ: ошибок $ERR")
exit $([ $ERR -eq 0 ] && echo 0 || echo 1)
