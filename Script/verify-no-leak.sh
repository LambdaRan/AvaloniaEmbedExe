#!/bin/bash
# 验证外部进程在两条路径下都不会残留：
#   1) 优雅关闭（taskkill 不带 /F → WM_CLOSE）→ 应由 lifetime.Exit 钩子清理
#   2) 强制终止（taskkill /F）→ 应由 Job Object 的 KILL_ON_JOB_CLOSE 由内核回收
APP="E:/lambda/selfcode/AvaloniaEmbedExe/AvaloniaEmbedExe/bin/Debug/net10.0-windows/AvaloniaEmbedExe.exe"

count_calc() { tasklist 2>/dev/null | grep -ci "calc1.exe" || true; }

wait_for_calc() {
  for i in $(seq 1 40); do
    if [ "$(count_calc)" -gt 0 ]; then return 0; fi
    sleep 0.5
  done
  return 1
}

wait_for_app_gone() {
  for i in $(seq 1 40); do
    if ! tasklist 2>/dev/null | grep -qi "AvaloniaEmbedExe.exe"; then return 0; fi
    sleep 0.5
  done
  return 1
}

run_case() {
  local label="$1"; local killflag="$2"
  echo "--------------------------------------------------"
  echo "用例: $label"

  MSYS_NO_PATHCONV=1 taskkill /F /IM calc1.exe >/dev/null 2>&1
  sleep 1
  echo "  起始 calc1 数量: $(count_calc)"

  "$APP" &
  local pid=$!
  if ! wait_for_calc; then
    echo "  结果: 启动后未检测到 calc1（嵌入失败？）"
    MSYS_NO_PATHCONV=1 taskkill /F /IM AvaloniaEmbedExe.exe >/dev/null 2>&1
    return
  fi
  echo "  嵌入成功, calc1 数量: $(count_calc)"
  sleep 3

  if [ "$killflag" = "force" ]; then
    MSYS_NO_PATHCONV=1 taskkill /F /IM AvaloniaEmbedExe.exe >/dev/null 2>&1
  else
    MSYS_NO_PATHCONV=1 taskkill /IM AvaloniaEmbedExe.exe >/dev/null 2>&1
  fi

  if wait_for_app_gone; then echo "  宿主已退出"; else echo "  宿主未退出（超时）"; fi
  sleep 2

  local remaining
  remaining=$(count_calc)
  if [ "$remaining" -eq 0 ]; then
    echo "  ✅ 结果: 无残留 (calc1 = 0)"
  else
    echo "  ❌ 结果: 泄漏 $remaining 个 calc1 进程"
  fi
  wait $pid 2>/dev/null
}

run_case "优雅关闭 (WM_CLOSE)" graceful
run_case "强制终止 (模拟崩溃/强杀)" force
echo "--------------------------------------------------"
echo "最终 calc1 数量: $(count_calc)"
