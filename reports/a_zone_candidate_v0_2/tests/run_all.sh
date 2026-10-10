#!/bin/sh
# ============================================================================
# run_all.sh — 一键复现全部证据
#   1. 修复前失败复现（针对 origin_frozen/ 的冻结副本）
#   2. 修复后回归（针对 prototype/ 的本次唯一 A 区候选）
#   3. 真实浏览器证据：三视口截图 + 越界/可读性量化 + 开业到检验室运转录屏
#
# 用法：
#   sh tests/run_all.sh              # 全跑（含录屏，约 5~8 分钟）
#   SKIP_RECORD=1 sh tests/run_all.sh   # 跳过录屏，只做截图与量化
# ============================================================================
set -e
HERE=$(cd "$(dirname "$0")" && pwd)
NODE_BIN=${NODE_BIN:-/Users/kker/.workbuddy/binaries/node/versions/22.12.0/bin/node}
NODE_MODULES=${NODE_MODULES:-/Users/kker/.workbuddy/binaries/node/workspace/node_modules}
export NODE_PATH="$NODE_MODULES"

echo "== [1/3] 修复前失败复现（origin_frozen）=="
"$NODE_BIN" "$HERE/pre_fix_repro.js"

echo ""
echo "== [2/3] 修复后回归（prototype）=="
"$NODE_BIN" "$HERE/post_fix_regression.js"

echo ""
echo "== [3/3] 真实浏览器证据（截图 + 量化 + 录屏）=="
"$NODE_BIN" "$HERE/browser_evidence.js"

echo ""
echo "全部完成。产物："
echo "  logs/pre_fix_repro.log"
echo "  logs/post_fix_regression.log"
echo "  logs/browser_evidence.log"
echo "  logs/browser_evidence.json"
echo "  shots/landscape_*.png"
echo "  recording/opening_to_lab_844x390.mp4"
