#!/usr/bin/env python3
"""Exercise the same arm64 APK shipped to the user, via its visible controls."""
import re
import subprocess
import time
import xml.etree.ElementTree as ET

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
adb("shell", "input text 'hy2://hydratest@10.0.2.2:8443?insecure=1#HydraTest'")
adb("shell", "input keyevent KEYCODE_BACK")
tap(label("Save profile"))
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
