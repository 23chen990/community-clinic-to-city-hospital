#!/bin/sh
cd "$(dirname "$0")/.."
python3 -m http.server 8080 --directory h5-demo
