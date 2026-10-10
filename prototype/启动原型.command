#!/bin/bash
# 晴天小医院 · A 区候选 — Mac 双击启动脚本
# 启动本地静态服务器并打开浏览器横屏预览
set -u
cd "$(dirname "$0")" || exit 1

VERSION=$(cat VERSION.txt 2>/dev/null || echo "unknown")
PORT=8123

# 端口被占用就往后找一个空闲端口
while lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; do
  echo "端口 ${PORT} 已被占用，改用 $((PORT + 1)) ..."
  PORT=$((PORT + 1))
done

echo "================================================"
echo " 晴天小医院 · A 区候选  版本 ${VERSION}"
echo " 地址 http://127.0.0.1:${PORT}/"
echo " 说明 手机横屏预览；打开房间面板会自动暂停"
echo "================================================"

python3 -m http.server "$PORT" --bind 127.0.0.1 >/dev/null 2>&1 &
SRV_PID=$!
sleep 1
open "http://127.0.0.1:${PORT}/"
echo "服务器 PID: ${SRV_PID}"
echo "关闭本窗口或按 Ctrl+C 即可停止。"
wait $SRV_PID
