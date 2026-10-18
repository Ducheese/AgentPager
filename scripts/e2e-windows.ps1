[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BridgeExecutable
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$pairingUrl = "http://127.0.0.1:49362/pairing"

try {
    Invoke-WebRequest -UseBasicParsing $pairingUrl -TimeoutSec 1 | Out-Null
    throw "Port 49362 is already in use. Exit the running Bridge before E2E."
} catch [System.Net.WebException] {
}

$bridge = Start-Process -FilePath $BridgeExecutable -ArgumentList "--background" -PassThru
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            Invoke-WebRequest -UseBasicParsing $pairingUrl -TimeoutSec 1 | Out-Null
            $ready = $true
            break
        } catch [System.Net.WebException] {
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not $ready) {
        throw "Bridge did not start within 10 seconds."
    }

    $pairing = Invoke-RestMethod $pairingUrl
    $socket = [Net.WebSockets.ClientWebSocket]::new()
    if ($pairing.secret) {
        $socket.Options.SetRequestHeader("X-AgentGrid-Secret", [string]$pairing.secret)
    }
    [void]$socket.ConnectAsync(
        [Uri]"ws://127.0.0.1:$($pairing.port)/agentgrid",
        [Threading.CancellationToken]::None
    ).GetAwaiter().GetResult()

    function Start-Hook {
        param([hashtable]$Payload)
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $BridgeExecutable
        if ($HookSource -eq "claude") {
            $startInfo.Arguments = "--hook --source claude"
        } else {
            $startInfo.Arguments = "--hook"
        }
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($startInfo)
        $process.StandardInput.Write(($Payload | ConvertTo-Json -Compress -Depth 10))
        $process.StandardInput.Close()
        return $process
    }

    function Invoke-Hook {
        param([hashtable]$Payload)
        $process = Start-Hook $Payload
        if (-not $process.WaitForExit(5000)) {
            $process.Kill()
            throw "A regular hook did not exit within 5 seconds."
        }
        if ($process.ExitCode -ne 0) {
            throw "Hook exit code $($process.ExitCode): $($process.StandardError.ReadToEnd())"
        }
    }

    function Receive-Envelope {
        $memory = [IO.MemoryStream]::new()
        $buffer = New-Object byte[] 65536
        $cancellation = [Threading.CancellationTokenSource]::new(
            [TimeSpan]::FromSeconds(15)
        )
        try {
            do {
                $segment = [ArraySegment[byte]]::new($buffer)
                $result = $socket.ReceiveAsync(
                    $segment,
                    $cancellation.Token
                ).GetAwaiter().GetResult()
                if ($result.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) {
                    throw "Bridge closed the WebSocket before E2E completed."
                }
                $memory.Write($buffer, 0, $result.Count)
            } until ($result.EndOfMessage)
            $json = [Text.Encoding]::UTF8.GetString($memory.ToArray())
            return $json | ConvertFrom-Json
        } finally {
            $cancellation.Dispose()
            $memory.Dispose()
        }
    }

    function Wait-Envelope {
        param([scriptblock]$Predicate, [int]$TimeoutSeconds = 10)
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            $envelope = Receive-Envelope
            if (& $Predicate $envelope) {
                return $envelope
            }
        }
        throw "Expected state was not received within $TimeoutSeconds seconds."
    }

    $taskID = "agentpager-windows-e2e-$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())"
    $cwd = $projectRoot
    $HookSource = $env:AGENTPAGER_HOOK_SOURCE
    if (-not $HookSource) { $HookSource = "codex" }
    Invoke-Hook ([ordered]@{
        cwd = $cwd
        hook_event_name = "SessionStart"
        session_id = $taskID
        source = "cli"
        model = "e2e"
    })
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-Hook ([ordered]@{
        cwd = $cwd
        hook_event_name = "PreToolUse"
        session_id = $taskID
        source = "cli"
        tool_name = "apply_patch"
        tool_use_id = "windows-e2e-tool"
        tool_input = [ordered]@{ description = "Windows local end-to-end state verification" }
    })
    $watch.Stop()
    if ($watch.ElapsedMilliseconds -ge 500) {
        throw "Regular hook took $($watch.ElapsedMilliseconds)ms, above the 500ms target."
    }

    Wait-Envelope {
        param($envelope)
        if ($envelope.type -ne "state.snapshot") { return $false }
        $task = $envelope.payload.tasks | Where-Object id -eq $taskID
        return $task.lifecycle -eq "running" -and $task.activity -eq "editing"
    } | Out-Null

    $permission = Start-Hook ([ordered]@{
        cwd = $cwd
        hook_event_name = "PermissionRequest"
        session_id = $taskID
        source = "cli"
        tool_name = "exec_command"
        tool_use_id = "windows-e2e-permission"
        tool_input = [ordered]@{ description = "Verify Windows phone approval flow" }
    })
    Wait-Envelope {
        param($envelope)
        if ($envelope.type -ne "state.snapshot") { return $false }
        $task = $envelope.payload.tasks | Where-Object id -eq $taskID
        return $task.lifecycle -eq "waitingApproval"
    } | Out-Null

    $messageID = [Guid]::NewGuid().ToString().ToLowerInvariant()
    $sentAt = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $deviceID = "agentpager-windows-e2e-device"
    $nonce = [Guid]::NewGuid().ToString()
    $controlPayload = [ordered]@{ action = "approve"; taskID = $taskID }
    $payloadText = $controlPayload | ConvertTo-Json -Compress
    $signingText = @(
        "1", $messageID, "$sentAt", $deviceID, "1", $nonce,
        "control.request", $payloadText
    ) -join "`n"
    $hmac = [Security.Cryptography.HMACSHA256]::new(
        [Convert]::FromBase64String($pairing.secret)
    )
    $signature = [Convert]::ToBase64String(
        $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($signingText))
    )
    $hmac.Dispose()
    $control = [ordered]@{
        version = 1
        messageId = $messageID
        type = "control.request"
        sentAt = $sentAt
        deviceId = $deviceID
        sequence = 1
        nonce = $nonce
        payload = $controlPayload
        signature = $signature
    } | ConvertTo-Json -Compress -Depth 10
    $controlBytes = [Text.Encoding]::UTF8.GetBytes($control)
    [void]$socket.SendAsync(
        [ArraySegment[byte]]::new($controlBytes),
        [Net.WebSockets.WebSocketMessageType]::Text,
        $true,
        [Threading.CancellationToken]::None
    ).GetAwaiter().GetResult()

    Wait-Envelope {
        param($envelope)
        return $envelope.type -eq "control.ack" -and
            $envelope.payload.requestID -eq $messageID -and
            $envelope.payload.result -eq "accepted"
    } | Out-Null
    if (-not $permission.WaitForExit(5000)) {
        $permission.Kill()
        throw "Permission hook did not receive a response."
    }
    $permissionOutput = $permission.StandardOutput.ReadToEnd()
    if (-not ($permissionOutput -match '"decision":"allow"' -or $permissionOutput -match '"behavior":"allow"')) {
        throw "Permission hook did not emit allow: $permissionOutput"
    }

    Invoke-Hook ([ordered]@{
        cwd = $cwd
        hook_event_name = "Stop"
        session_id = $taskID
        source = "cli"
    })
    Wait-Envelope {
        param($envelope)
        if ($envelope.type -ne "state.snapshot") { return $false }
        $task = $envelope.payload.tasks | Where-Object id -eq $taskID
        return $task.lifecycle -eq "succeeded"
    } | Out-Null

    [void]$socket.CloseAsync(
        [Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
        "E2E complete",
        [Threading.CancellationToken]::None
    ).GetAwaiter().GetResult()
    $socket.Dispose()
    Invoke-WebRequest -UseBasicParsing $pairingUrl -TimeoutSec 2 | Out-Null
    Write-Output "Windows E2E passed: state, approval, stop, close-frame liveness; regular hook $($watch.ElapsedMilliseconds)ms"
} finally {
    if (-not $bridge.HasExited) {
        Stop-Process -Id $bridge.Id
        $bridge.WaitForExit()
    }
}
