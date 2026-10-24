#!/bin/bash
cd "C:/Users/Administrator/Documents/pro/Chorus" || exit 1
GH="C:/Users/Administrator/.workbuddy/binaries/gh/bin/gh.exe"
TOKEN=$("$GH" auth token)
PROXY="https://gh.fansa.nyc.mn/https://github.com/fansa92/Chorus.git"
HELPER="!f() { echo username=fansa92; echo password=$TOKEN; }; f"
DIRECT="https://github.com/fansa92/Chorus.git"
SSHIP="140.82.112.3 140.82.121.4 140.82.121.3 140.82.113.3 140.82.114.3 140.82.112.4 140.82.114.4 140.82.113.4 140.82.114.25"

gp() { git -c credential.helper= -c credential.helper="$HELPER" "$@"; }

verified() {
  local r
  r=$(timeout 45 gp ls-remote "$PROXY" refs/tags/v0.1.2 2>/dev/null)
  echo "$r" | grep -q "refs/tags/v0.1.2" && return 0
  r=$(timeout 45 gp ls-remote "$DIRECT" refs/tags/v0.1.2 2>/dev/null)
  echo "$r" | grep -q "refs/tags/v0.1.2" && return 0
  return 1
}

ok=0
for round in $(seq 1 12); do
  # 路径 1：代理
  if curl -s -o /dev/null --max-time 6 https://gh.fansa.nyc.mn/ 2>/dev/null; then
    timeout 90 gp push "$PROXY" refs/tags/v0.1.2 >/dev/null 2>&1
    if verified; then echo "OK via PROXY (round $round)"; ok=1; break; fi
  fi
  # 路径 2：直连 GitHub IP（HTTPS + token）
  for ip in $SSHIP; do
    code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 4 --resolve github.com:443:$ip https://github.com/ 2>/dev/null)
    if [ "$code" = "200" ] || [ "$code" = "302" ]; then
      powershell -NoProfile -ExecutionPolicy Bypass -File scripts/update-gh-host.ps1 -NewIp $ip >/dev/null 2>&1
      ipconfig //flushdns >/dev/null 2>&1
      timeout 75 gp push "$DIRECT" refs/tags/v0.1.2 >/dev/null 2>&1
      if verified; then echo "OK via DIRECT $ip (round $round)"; ok=1; break 2; fi
    fi
  done
  # 路径 3：SSH 部署 key（经 IP 直连）
  for ip in $SSHIP; do
    out=$(timeout 45 git -c core.sshCommand="ssh -i ~/.ssh/id_ed25519_chorus -o IdentitiesOnly=yes -o StrictHostKeyChecking=no -o ConnectTimeout=6" push git@$ip:fansa92/Chorus.git refs/tags/v0.1.2 2>&1)
    if echo "$out" | grep -qE "v0.1.2 -> v0.1.2|up-to-date"; then echo "OK via SSH $ip (round $round)"; ok=1; break 2; fi
  done
  echo "round $round 未成功"; sleep 8
done
[ $ok -eq 0 ] && echo "=== 12 轮全部失败 ===" || echo "=== TAG DONE ==="
