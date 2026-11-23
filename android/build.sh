#!/usr/bin/env bash
set -euo pipefail
root=$(pwd)
export ANDROID_NDK_HOME="$ANDROID_HOME/ndk/28.0.13004108"
export PATH="$PATH:$(go env GOPATH)/bin"
export CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android24-clang"
git -C mobile apply "$root/android/hydra.patch"
cp -R android/overlay/. mobile/app/src/main/
export CARGO_TARGET_X86_64_LINUX_ANDROID_LINKER="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/linux-x86_64/bin/x86_64-linux-android24-clang"
printf '%s' "$HYDRA_ANDROID_KEYSTORE" | base64 -d > "$RUNNER_TEMP/hydra.p12"
alias_name=$(LC_ALL=C keytool -list -v -storetype PKCS12 -keystore "$RUNNER_TEMP/hydra.p12" -storepass:env HYDRA_ANDROID_PASSWORD | sed -n 's/^Alias name: //p' | head -1)
test -n "$alias_name"
keytool -importkeystore -noprompt -srckeystore "$RUNNER_TEMP/hydra.p12" -srcstoretype PKCS12 -srcstorepass:env HYDRA_ANDROID_PASSWORD -srcalias "$alias_name" -destkeystore "$RUNNER_TEMP/hydra-release.jks" -deststoretype JKS -destalias hydra -deststorepass:env HYDRA_ANDROID_PASSWORD -destkeypass:env HYDRA_ANDROID_PASSWORD
install -m600 "$RUNNER_TEMP/hydra-release.jks" mobile/app/release.keystore
export LOCAL_PROPERTIES=$(printf 'KEYSTORE_PASS=%s\nALIAS_NAME=hydra\nALIAS_PASS=%s\n' "$HYDRA_ANDROID_PASSWORD" "$HYDRA_ANDROID_PASSWORD" | base64 -w0)
rustup target add aarch64-linux-android x86_64-linux-android
cargo build --manifest-path rust/Cargo.toml --locked --release --target x86_64-linux-android --lib
install -Dm755 rust/target/x86_64-linux-android/release/libhydra_routing.so mobile/app/src/main/jniLibs/x86_64/libhydra_import.so
cargo build --manifest-path rust/Cargo.toml --locked --release --target aarch64-linux-android --lib
install -Dm755 rust/target/aarch64-linux-android/release/libhydra_routing.so mobile/app/src/main/jniLibs/arm64-v8a/libhydra_import.so
(cd engine && make lib_install && go run ./cmd/internal/build_libbox -target android -platform android/arm64,android/amd64)
install -Dm644 engine/libbox.aar mobile/app/libs/libbox.aar
sed -i 's/^VERSION_CODE=.*/VERSION_CODE=2500/; s/^VERSION_NAME=.*/VERSION_NAME=2.5.0/' mobile/version.properties
(cd mobile && ./gradlew :app:assembleOtherRelease)

apk=$(find mobile/app/build/outputs/apk/other/release -name '*universal*.apk' -print -quit)
aapt=$(find "$ANDROID_HOME/build-tools" -name aapt -type f | sort -V | tail -1)
"$(dirname "$aapt")/apksigner" verify --verbose --print-certs "$apk"
"$aapt" dump badging "$apk" > labels.txt
grep -q "^application-label:'Hydra'$" labels.txt
if grep '^application-label-' labels.txt | grep -v ":'Hydra'$"; then
    echo "Unexpected localized launcher label" >&2
    exit 1
fi