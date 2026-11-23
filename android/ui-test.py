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
uri = "hy2://hydratest@10.0.2.2:8443?insecure=1#HydraTest"
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
tap(scroll_to("Check for update"))
label("Hydra is up to date.")
