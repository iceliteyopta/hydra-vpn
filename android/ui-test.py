#!/usr/bin/env python3
"""Exercise the signed universal APK through its Android UI and VPN service."""
import json
import re
import subprocess
import time
import xml.etree.ElementTree as ET
from pathlib import Path

PACKAGE = "io.github.iceliteyopta.hydra.vpn"


def adb(*args):
    return subprocess.check_output(["adb", *args], text=True, errors="replace")


def nodes():
    adb("shell", "uiautomator dump /data/local/tmp/hydra-ui.xml")
    return list(ET.fromstring(adb("shell", "cat /data/local/tmp/hydra-ui.xml")).iter("node"))


def wait_for(match, timeout=80):
    until = time.monotonic() + timeout
    visible = []
    while time.monotonic() < until:
        try:
            visible = nodes()
            # A cold Pixel Launcher can time out during emulator boot. Dismiss
            # only its own prompt; keep Hydra errors visible for diagnosis.
            if any("Pixel Launcher isn't responding" in n.get("text", "") for n in visible):
                choice = next((n for n in visible if n.get("text") == "Wait"), None)
                if choice is not None:
                    tap(choice.attrib)
                    adb("shell", "am", "start", "-n", PACKAGE + "/io.nekohasekai.sfa.compose.MainActivity")
                    continue
            for node in visible:
                if match(node.attrib):
                    return node.attrib
        except (ET.ParseError, subprocess.CalledProcessError):
            pass
        time.sleep(1)
    labels = [n.get("text") for n in visible if n.get("text")]
    raise RuntimeError(f"Control missing. Visible labels: {labels}")


def tap(node):
    x1, y1, x2, y2 = map(int, re.findall(r"\d+", node["bounds"]))
    adb("shell", "input", "tap", str((x1 + x2) // 2), str((y1 + y2) // 2))


def label(name):
    return wait_for(lambda n: n.get("text") == name and n.get("package") == PACKAGE)


adb("shell", "am", "start", "-n", PACKAGE + "/io.nekohasekai.sfa.compose.MainActivity")
label("Hydra")
tap(label("Enter Hysteria2 link"))
tap(wait_for(lambda n: n.get("class") == "android.widget.EditText"))
uri = "hysteria2://hydratest@10.0.2.2:8443?alpn=h3&fp=chrome&security=tls&sni=&insecure=1#HydraTest"
# Android's synthetic bulk text events can overrun a cold Compose password field.
# Wait for the field to reflect the complete URI before saving the profile.
for attempt in range(3):
    if attempt:
        adb("shell", "input keycombination KEYCODE_CTRL_LEFT KEYCODE_A")
        adb("shell", "input keyevent KEYCODE_DEL")
    adb("shell", " && ".join("input text '" + char + "' && sleep 0.12" for char in uri))
    if any(n.get("class") == "android.widget.EditText" and len(n.get("text", "")) == len(uri) for n in nodes()):
        break
else:
    raise RuntimeError("Android did not enter the complete test profile URI")
adb("shell", "input keyevent KEYCODE_BACK")
tap(label("Save profile"))
label("HydraTest")
# Update in place with a real saved profile. Android verifies the signing lineage.
apk, = Path("input").glob("*.apk")
adb("install", "-r", "-g", str(apk))
adb("shell", "am", "start", "-n", PACKAGE + "/io.nekohasekai.sfa.compose.MainActivity")
label("HydraTest")
tap(label("Connect"))
tap(wait_for(lambda n: n.get("resource-id") == "android:id/button1"))
label("Connected")
with open("hydra-android-connected.png", "wb") as output:
    subprocess.run(["adb", "exec-out", "screencap", "-p"], stdout=output, check=True)
# The HTTP listener exists only on the runner's loopback alias, reachable through
# the test Hysteria2 server. The emulator has no route to it over normal internet.
reply = adb("shell", "(printf 'GET / HTTP/1.0\\r\\n\\r\\n'; sleep 2) | toybox nc -w 10 192.0.2.55 8000")
if "hydra-vpn-test-ok" not in reply:
    raise RuntimeError("VPN did not return the HTTP fixture")
tap(label("Disconnect"))
label("Disconnected")

tap(label("Settings"))
tap(label("Edit active profile"))
# On compact displays the log selector sits below the route and DNS choices.
for _ in range(5):
    info = next((n for n in nodes() if n.get("text") == "info"), None)
    if info is not None:
        break
    adb("shell", "input swipe 520 1300 520 820 400")
tap(label("info"))
tap(label("debug"))
tap(label("Save"))
tap(label("Edit active profile"))
for _ in range(5):
    if any(n.get("text") == "debug" for n in nodes()):
        break
    adb("shell", "input swipe 520 1300 520 820 400")
label("debug")
with open("hydra-android-settings.png", "wb") as output:
    subprocess.run(["adb", "exec-out", "screencap", "-p"], stdout=output, check=True)

# Verify that a mandatory tunnel can be assigned without opening the JSON editor.
def scroll_to(text):
    for _ in range(12):
        visible = nodes()
        node = next((n for n in visible if n.get("text") == text and n.get("package") == PACKAGE), None)
        if node is not None:
            return node.attrib
        area = next((n for n in visible if n.get("scrollable") == "true" and n.get("package") == PACKAGE), None)
        if area is None:
            break
        x1, y1, x2, y2 = map(int, re.findall(r"\d+", area.get("bounds")))
        x = (x1 + x2) // 2
        adb("shell", "input", "swipe", str(x), str(y2 - 20), str(x), str(y1 + 20), "380")
    return label(text)


tap(scroll_to("Require tunnel for an app"))
tap(scroll_to("package_name"))
tap(label("domain_suffix"))
field = wait_for(lambda n: n.get("class") == "android.widget.EditText")
tap(field)
adb("shell", "input text example.org")
adb("shell", "input keyevent KEYCODE_BACK")
# There are two controls with the same label while the rule editor is expanded.
for _ in range(8):
    visible = nodes()
    adds = [n for n in visible if n.get("text") == "Add routing rule"]
    field = next((n for n in visible if n.get("class") == "android.widget.EditText"), None)
    field_y = int(re.findall(r"\d+", field.get("bounds"))[1]) if field is not None else -1
    if adds and int(re.findall(r"\d+", adds[-1].get("bounds"))[1]) > field_y and any(n.get("text") == "VPN" for n in visible):
        tap(adds[-1].attrib)
        break
    adb("shell", "input swipe 520 1750 520 900 400")
tap(label("Save"))
tap(label("Edit active profile"))
tap(scroll_to("Ordered rules (JSON)"))
wait_for(lambda n: "example.org" in n.get("text", "") and "domain_suffix" in n.get("text", ""))
adb("shell", "input keyevent KEYCODE_BACK")
# Give the emulator a deterministic route to public GitHub through the runner.
# Android still performs TLS verification against api.github.com.
import select
import socket
import socketserver
import threading

class GithubConnect(socketserver.StreamRequestHandler):
    def handle(self):
        request = self.rfile.readline(4096).split()
        while self.rfile.readline(4096).strip():
            pass
        if request[:2] != [b"CONNECT", b"api.github.com:443"]:
            self.wfile.write(b"HTTP/1.1 403 Forbidden\r\n\r\n")
            return
        with socket.create_connection(("api.github.com", 443), timeout=10) as upstream:
            self.wfile.write(b"HTTP/1.1 200 Connection established\r\n\r\n")
            peers = [self.connection, upstream]
            while True:
                ready, _, _ = select.select(peers, [], [], 12)
                if not ready:
                    return
                for peer in ready:
                    data = peer.recv(65536)
                    if not data:
                        return
                    peers[1 - peers.index(peer)].sendall(data)

proxy = socketserver.ThreadingTCPServer(("127.0.0.1", 0), GithubConnect)
proxy.daemon_threads = True
threading.Thread(target=proxy.serve_forever, daemon=True).start()
port = str(proxy.server_address[1])
adb("reverse", "tcp:" + port, "tcp:" + port)
adb("shell", "settings", "put", "global", "http_proxy", "127.0.0.1:" + port)
time.sleep(2)
tap(scroll_to("Check for update"))
label("Hydra is up to date.")
tap(label("OK"))
adb("shell", "settings", "put", "global", "http_proxy", ":0")
tap(label("Dashboard"))
# Import a multi-tunnel fixture using Android's file picker. Neither tunnel
# name has any special meaning to the client. Only the first gets a relay.
def tunnel(tag, port):
    return {"type": "hysteria2", "tag": tag, "server": "10.0.2.2",
            "server_port": port, "password": "hydratest", "tls": {"enabled": True, "insecure": True}}

multi = {"inbounds": [{"type": "tun", "tag": "tun-in", "address": ["172.19.0.1/30"],
                       "auto_route": True}],
         "outbounds": [tunnel("Media", 8443), tunnel("Side", 8445)],
         "route": {"auto_detect_interface": True, "rules": [{"port": 8001, "outbound": "Side"}], "final": "Media"}}
fixture = Path("Multitunnel.json")
fixture.write_text(json.dumps(multi))
adb("push", str(fixture), "/sdcard/Download/" + fixture.name)
adb("shell", "am", "broadcast", "-a", "android.intent.action.MEDIA_SCANNER_SCAN_FILE",
    "-d", "file:///sdcard/Download/" + fixture.name)
tap(label("Open profile file"))
picker = wait_for(lambda n: "documentsui" in n.get("package", "") and
                  (n.get("text") == fixture.name or n.get("content-desc") == "Show roots"))
if picker.get("text") != fixture.name:
    tap(picker)
    tap(wait_for(lambda n: n.get("text") == "Downloads" and "documentsui" in n.get("package", "")))
    picker = wait_for(lambda n: n.get("text") == fixture.name and "documentsui" in n.get("package", ""))
tap(picker)
wait_for(lambda n: n.get("package") == PACKAGE and n.get("text", "").startswith("Multitunnel"))
tap(label("Settings"))
tap(scroll_to("Hysteria2 relay"))
label("Tunnel in this profile")
label("Media")
fields = [n for n in nodes() if n.get("class") == "android.widget.EditText"]
assert len(fields) == 2, "missing relay address/port"
tap(fields[0].attrib)
adb("shell", "input", "text", "10.0.2.2")
adb("shell", "input", "keyevent", "KEYCODE_BACK")
tap([n for n in nodes() if n.get("class") == "android.widget.EditText"][1].attrib)
adb("shell", "input", "text", "8444")
adb("shell", "input", "keyevent", "KEYCODE_BACK")
tap(label("Save"))
tap(label("Dashboard"))
# Only the emulator's two test UDP ports are affected. The server logs the
# inbound that actually delivered the private HTTP request on each path.
def drop(port, enabled):
    subprocess.run(["sudo", "iptables", "-I" if enabled else "-D", "INPUT",
                    "-i", "lo", "-p", "udp", "--dport", str(port), "-j", "DROP"], check=True)

def expect_path(tag):
    log = Path("test-server.log")
    position = log.stat().st_size
    deadline = time.monotonic() + 115
    while time.monotonic() < deadline:
        reply = adb("shell", "(printf 'GET / HTTP/1.0\\r\\n\\r\\n'; sleep 1) | toybox nc -w 4 192.0.2.55 8000 || true")
        with log.open("rb") as stream:
            stream.seek(position)
            delivered = any(("[" + tag + "]" in line and "connection to 192.0.2.55:8000" in line)
                            for line in stream.read().decode(errors="replace").splitlines())
        if delivered and "hydra-vpn-test-ok" in reply:
            print("Private HTTP response through " + tag, flush=True)
            return
        time.sleep(2)
    raise RuntimeError("No data delivery through " + tag)

drop(18444, True)
tap(label("Connect"))
label("Connected")
expect_path("primary-fixture")
drop(18444, False)

# One TCP stream stays on the second tunnel throughout both switches.
class Ticks(socketserver.StreamRequestHandler):
    def handle(self):
        for i in range(360):
            self.wfile.write(f"{i}\n".encode())
            time.sleep(1)

ticks = socketserver.ThreadingTCPServer(("192.0.2.55", 8001), Ticks)
ticks.daemon_threads = True
threading.Thread(target=ticks.serve_forever, daemon=True).start()
stream_file = Path("side-stream.txt")
with stream_file.open("wb") as output:
    stream = subprocess.Popen(["adb", "shell", "sleep 360 | toybox nc 192.0.2.55 8001"], stdout=output)
    try:
        for _ in range(20):
            if stream_file.stat().st_size > 3: break
            time.sleep(1)
        assert stream_file.stat().st_size > 3, "side tunnel did not open"
        drop(8443, True)
        try:
            expect_path("relay-fixture")
        finally:
            drop(8443, False)
        drop(18444, True)
        try:
            expect_path("primary-fixture")
        finally:
            drop(18444, False)
        size = stream_file.stat().st_size
        time.sleep(3)
        samples = list(map(int, stream_file.read_text().splitlines()))
        assert stream_file.stat().st_size > size and samples == list(range(len(samples)))
        assert any("[side-fixture]" in line and "connection to 192.0.2.55:8001" in line
                   for line in Path("test-server.log").read_text().splitlines())
        print("Uninterrupted second tunnel:", len(samples), "consecutive samples", flush=True)
    finally:
        stream.terminate()
        stream.wait(timeout=8)
        ticks.shutdown()
label("Connected")
tap(label("Disconnect"))
label("Disconnected")
