#!/bin/bash
# 晴天小医院原型 — Mac 双击启动脚本
# 启动本地静态服务器并打开浏览器横屏预览
cd "$(dirname "$0")"
PORT=8123
echo "启动晴天小医院原型 → http://localhost:${PORT}/"
# 后台启动 Python 静态服务器
python3 -m http.server ${PORT} >/dev/null 2>&1 &
SRV_PID=$!
sleep 1
open "http://localhost:${PORT}/"
echo "服务器 PID: ${SRV_PID}（关闭终端或按 Ctrl+C 停止）"
wait $SRV_PID
