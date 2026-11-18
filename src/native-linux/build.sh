#!/bin/bash
set -e
cd "$(dirname "$0")"
cargo build --release
cp target/release/libclippy_linux.so ../../dist/libclippy_linux.so 2>/dev/null || true
cp target/release/libclippy_linux.so ../../publish-linux/libclippy_linux.so 2>/dev/null || true
echo "Build complete: target/release/libclippy_linux.so"
