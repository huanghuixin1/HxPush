#!/usr/bin/env bash
# HxPushServerWeb Linux 后台运行脚本：start / stop / restart / status / log
# 用法（在发布目录或本脚本所在目录执行）：
#   chmod +x run-linux.sh
#   ./run-linux.sh start
#   ./run-linux.sh stop
#   ./run-linux.sh restart
#   ./run-linux.sh status
#   ./run-linux.sh log

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="${APP_DIR:-$SCRIPT_DIR}"
APP_NAME="HxPushServerWeb"
PID_FILE="${PID_FILE:-$APP_DIR/${APP_NAME}.pid}"
LOG_FILE="${LOG_FILE:-$APP_DIR/${APP_NAME}.log}"
# 默认与 Program.cs / launchSettings 一致；可用环境变量覆盖
URLS="${ASPNETCORE_URLS:-http://0.0.0.0:5212}"
DOTNET_ENV="${ASPNETCORE_ENVIRONMENT:-Production}"

resolve_start_cmd() {
  if [[ -x "$APP_DIR/$APP_NAME" ]]; then
    echo "\"$APP_DIR/$APP_NAME\""
    return
  fi
  if [[ -f "$APP_DIR/$APP_NAME.dll" ]]; then
    if ! command -v dotnet >/dev/null 2>&1; then
      echo "error: 未找到可执行文件 $APP_DIR/$APP_NAME，且系统没有 dotnet 命令" >&2
      exit 1
    fi
    echo "dotnet \"$APP_DIR/$APP_NAME.dll\""
    return
  fi
  echo "error: 在 $APP_DIR 未找到 $APP_NAME 或 $APP_NAME.dll（请先 publish 到该目录）" >&2
  exit 1
}

is_running() {
  if [[ ! -f "$PID_FILE" ]]; then
    return 1
  fi
  local pid
  pid="$(cat "$PID_FILE" 2>/dev/null || true)"
  if [[ -z "${pid:-}" ]]; then
    return 1
  fi
  if kill -0 "$pid" 2>/dev/null; then
    return 0
  fi
  return 1
}

cmd_start() {
  if is_running; then
    echo "already running (pid $(cat "$PID_FILE"))"
    return 0
  fi

  mkdir -p "$APP_DIR/App_Data"
  local start_cmd
  start_cmd="$(resolve_start_cmd)"

  cd "$APP_DIR"
  export ASPNETCORE_URLS="$URLS"
  export ASPNETCORE_ENVIRONMENT="$DOTNET_ENV"
  # nohup + 重定向，使 SSH 断开后仍继续运行
  nohup bash -c "$start_cmd" >>"$LOG_FILE" 2>&1 &
  local pid=$!
  echo "$pid" >"$PID_FILE"
  sleep 1
  if kill -0 "$pid" 2>/dev/null; then
    echo "started $APP_NAME pid=$pid urls=$URLS log=$LOG_FILE"
  else
    rm -f "$PID_FILE"
    echo "error: 进程启动后立即退出，请查看日志: $LOG_FILE" >&2
    exit 1
  fi
}

cmd_stop() {
  if ! is_running; then
    rm -f "$PID_FILE"
    echo "not running"
    return 0
  fi
  local pid
  pid="$(cat "$PID_FILE")"
  kill "$pid" 2>/dev/null || true
  local i=0
  while kill -0 "$pid" 2>/dev/null && [[ $i -lt 20 ]]; do
    sleep 0.5
    i=$((i + 1))
  done
  if kill -0 "$pid" 2>/dev/null; then
    kill -9 "$pid" 2>/dev/null || true
  fi
  rm -f "$PID_FILE"
  echo "stopped $APP_NAME (was pid $pid)"
}

cmd_status() {
  if is_running; then
    echo "running pid=$(cat "$PID_FILE") urls=$URLS log=$LOG_FILE"
  else
    echo "stopped"
    return 1
  fi
}

cmd_log() {
  if [[ ! -f "$LOG_FILE" ]]; then
    echo "log not found: $LOG_FILE"
    exit 1
  fi
  tail -n 100 -f "$LOG_FILE"
}

usage() {
  cat <<EOF
Usage: $0 {start|stop|restart|status|log}

Environment:
  APP_DIR                  应用目录，默认脚本所在目录
  ASPNETCORE_URLS          监听地址，默认 http://0.0.0.0:5212
  ASPNETCORE_ENVIRONMENT   环境，默认 Production
  PID_FILE / LOG_FILE      可覆盖 pid/log 路径
EOF
}

main() {
  local action="${1:-}"
  case "$action" in
    start) cmd_start ;;
    stop) cmd_stop ;;
    restart) cmd_stop; cmd_start ;;
    status) cmd_status ;;
    log) cmd_log ;;
    *) usage; exit 1 ;;
  esac
}

main "$@"
