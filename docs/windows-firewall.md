# Windows 防火墙规则：累积与清理（2026-10-20）

Bridge 监听 `0.0.0.0:49362`（手机端）与 `127.0.0.1:49361`（本机 hook）。首次监听时 Windows
防火墙会为**该 exe 的完整路径**建一条入站允许规则；每换一个发布目录（`dist\opencode-main-baseline-vXXX\`）
就多一对规则（TCP + UDP），旧目录删了规则也不会自动消失。

---

## 一、查看（不需要管理员）

`Get-NetFirewallRule` 在非管理员下会报 `Access is denied`；配合 `-ErrorAction SilentlyContinue`
会**静默返回 0 条**，看起来像"没有规则"——这是最容易误判的地方。读规则请用 `netsh`。

```powershell
# 统计累计条数
(netsh advfirewall firewall show rule name=all verbose |
    Select-String '^Rule Name:\s+AgentPagerBridge\s*$').Count

# 列出每条规则对应的 exe 路径
$out = netsh advfirewall firewall show rule name=all verbose | Out-String
$out -split '(?m)^Rule Name:' |
    Where-Object { $_ -match 'AgentPagerBridge' } |
    ForEach-Object { ([regex]::Match($_,'(?m)^Program:\s+(.+)$')).Groups[1].Value.Trim() }
```

---

## 二、删除全部 bridge 规则（需管理员终端）

```powershell
# 删掉所有同名规则（十二类版本一次清完；不影响其它程序）
netsh advfirewall firewall delete rule name="AgentPagerBridge"
```

从非管理员会话执行时，用 UAC 提权：

```powershell
Start-Process -FilePath "netsh.exe" `
    -ArgumentList 'advfirewall','firewall','delete','rule','name=AgentPagerBridge' `
    -Verb RunAs -PassThru -Wait
```

PowerShell 等价写法（管理员）：

```powershell
# 按显示名删
Get-NetFirewallRule -DisplayName 'AgentPagerBridge' | Remove-NetFirewallRule

# 或只删 baseline 目录的规则，不碰其它同名项
Get-NetFirewallApplicationFilter |
    Where-Object { $_.Program -like '*opencode-main-baseline*' } |
    Get-NetFirewallRule | Remove-NetFirewallRule
```

---

## 三、验证

```powershell
(netsh advfirewall firewall show rule name=all verbose |
    Select-String '^Rule Name:\s+AgentPagerBridge\s*$').Count   # 期望 0
```

删除不影响当前运行：已建立的连接不会断开；下次以**新路径**启动时 Windows 可能重新弹允许框。

---

## 四、避免再次累积

Windows 按 **exe 完整路径**建规则。想不重复，就让 Bridge 永远从同一个路径运行，例如：

```
F:\Git\tool-dev\fork\AgentPager\dist\AgentPagerBridge.exe
```

每版发布到 `dist\opencode-main-baseline-vXXX\` 后只把 exe 复制到这个固定位置再启动，
防火墙就只会维护那一条规则。

---

## 五、实测记录

2026-10-20 首次清理：累计 **12 条**（v96 / v105 / v106 / v107 / v108 / v109 各 TCP + UDP），
全部为「入站 / 允许 / Public」。执行删除后复核为 **0 条**。
