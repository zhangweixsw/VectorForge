$ErrorActionPreference = 'Stop'
function global:ssh {
    & ssh.exe -o 'ProxyCommand="C:/Program Files/Git/mingw64/bin/connect.exe" -5 -S 127.0.0.1:10808 %h %p' @args
}
& 'C:/Users/zhangweixsw/.codex/skills/mootop-publish-project/scripts/preflight.ps1'
