#!/usr/bin/env bash
set -euo pipefail
engine=sing-box-1.14.4-linux-amd64
curl -fsSL "https://github.com/SagerNet/sing-box/releases/download/v1.14.4/$engine.tar.gz" | tar -xz
openssl req -x509 -newkey rsa:2048 -nodes -keyout test.key -out test.crt -days 1 -subj /CN=hydra-test
cat > test-server.json <<'JSON'
{"inbounds":[{"type":"hysteria2","tag":"side-fixture","listen":"127.0.0.1","listen_port":8445,"users":[{"password":"hydratest"}],"tls":{"enabled":true,"certificate_path":"test.crt","key_path":"test.key"}},{"type":"hysteria2","tag":"primary-fixture","listen":"0.0.0.0","listen_port":8443,"users":[{"password":"hydratest"}],"tls":{"enabled":true,"certificate_path":"test.crt","key_path":"test.key"}},{"type":"hysteria2","tag":"relay-fixture","listen":"127.0.0.1","listen_port":18444,"users":[{"password":"hydratest"}],"tls":{"enabled":true,"certificate_path":"test.crt","key_path":"test.key"}}],"outbounds":[{"type":"direct"}]}
JSON
sudo ip addr add 192.0.2.55/32 dev lo
mkdir -p http-fixture
echo hydra-vpn-test-ok > http-fixture/index.html
python3 -m http.server 8000 --bind 192.0.2.55 --directory http-fixture > fixture.log 2>&1 &
http_pid=$!
sudo iptables -t nat -I OUTPUT -o lo -p udp --dport 8444 -j REDIRECT --to-ports 18444
"./$engine/sing-box" run -c test-server.json --disable-color > test-server.log 2>&1 &
vpn_pid=$!
trap 'kill "$vpn_pid" "$http_pid" 2>/dev/null || true' EXIT
adb shell settings put secure show_first_crash_dialog 0
curl -fsSL --retry 3 https://github.com/iceliteyopta/hydra-vpn/releases/download/android-2501/Hydra-Mobile.apk -o previous.apk
echo '32b157dc48c551a86491cb9cb80a271ca1858b2a674faa73cd6bd76498954be1  previous.apk' | sha256sum -c -
adb install -g previous.apk
adb logcat -c
if ! python3 android/ui-test.py; then
    adb logcat -b crash -d | tail -200
    adb shell dumpsys activity activities | grep -E "mResumed|topResumed" || true
    adb logcat -d -s AndroidRuntime:E DEBUG:E libc:F | tail -150
    exit 1
fi
