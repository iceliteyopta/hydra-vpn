#!/usr/bin/env bash
set -euo pipefail
root=$(pwd)
export ANDROID_NDK_HOME="$ANDROID_HOME/ndk/28.0.13004108"
export PATH="$PATH:$(go env GOPATH)/bin"
export CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android24-clang"
git -C mobile apply "$root/android/hydra.patch"
rustup target add aarch64-linux-android
cargo build --manifest-path rust/Cargo.toml --locked --release --target aarch64-linux-android --bin hydra-import
install -Dm755 rust/target/aarch64-linux-android/release/hydra-import mobile/app/src/main/jniLibs/arm64-v8a/libhydra_import.so
(cd engine && make lib_install && go run ./cmd/internal/build_libbox -target android -platform android/arm64)
install -Dm644 engine/libbox.aar mobile/app/libs/libbox.aar
(cd mobile && ./gradlew :app:assembleOtherDebug)
