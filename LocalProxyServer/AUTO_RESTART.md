# Upstream Process Auto-Restart Feature

## Overview

Now when the upstream process exits unexpectedly, **it will be automatically restarted**.

## Main Features

### ✅ Automatic Crash Detection
- Checks process status every second
- Immediately detects process exit

### ✅ Automatic Restart
- Automatically restarts after crash detection
- Configurable wait time before restart
- Configurable maximum retry attempts
- Detailed logging on failure

### ✅ Prevent Infinite Restarts
- Default maximum of 5 retry attempts
- Can be set to 0 for unlimited retries
- Stops trying after reaching limit

## Configuration

```json
{
  "Process": {
    "AutoStart": true,
    "FileName": "ssh.exe",
    "Arguments": "...",

    "AutoRestart": true,           // Enable auto-restart
    "MaxRestartAttempts": 5,       // Max 5 restart attempts (0=unlimited)
    "RestartDelayMs": 3000         // Wait 3 seconds before restart
  }
}
```

## Usage Scenarios

### Scenario 1: SSH Connection Dropped
```
Situation: Network fluctuation causes SSH disconnect
Result: Auto-reconnect, proxy service restored after 3 seconds
Logs:
  [Info] Upstream process exited unexpectedly with code 255
  [Info] Attempting to restart (attempt 1/5)
  [Info] Upstream process restarted successfully
```

### Scenario 2: Process Crash
```
Situation: Tool like V2Ray crashes due to config error
Result: Attempts restart, logs failure count
Logs:
  [Warn] Upstream process exited with code 1
  [Info] Attempting to restart (attempt 1/5)
  ...
  [Error] Maximum restart attempts (5) reached
```

### Scenario 3: Long-term Operation
```
Configuration: MaxRestartAttempts = 0
Result: Unlimited retries, suitable for 24/7 operation scenarios
```

## Configuration Recommendations

| Use Case | AutoRestart | MaxRestartAttempts | RestartDelayMs |
|----------|-------------|-------------------|----------------|
| SSH Tunnel | `true` | `10` | `5000` |
| V2Ray | `true` | `5` | `3000` |
| Temporary Testing | `false` | - | - |
| Production (24/7) | `true` | `0` | `10000` |

## Log Examples

```
[12:00:00 INF] Upstream process started with PID 12345
[12:00:00 INF] Process monitoring enabled with auto-restart

[12:05:30 WRN] Upstream process exited unexpectedly with code 255
[12:05:30 INF] Attempting to restart upstream process (attempt 1/5)
[12:05:33 INF] Upstream process restarted with PID 12346
[12:05:33 INF] Upstream process is ready

[12:10:15 WRN] Upstream process exited unexpectedly with code 1
[12:10:15 INF] Attempting to restart upstream process (attempt 2/5)
[12:10:18 INF] Upstream process restarted with PID 12347
```

## Manual Control

### Disable Auto-restart
```json
{
  "AutoRestart": false
}
```

### Unlimited Retries
```json
{
  "MaxRestartAttempts": 0
}
```

### Long Wait Time
```json
{
  "RestartDelayMs": 30000  // Wait 30 seconds
}
```

## Troubleshooting

### Issue: Process Keeps Crashing
1. Check exit code in logs
2. Check process Error output
3. Verify configuration file and parameters
4. Try manual process startup test

### Issue: Reached Retry Limit
1. Increase `MaxRestartAttempts`
2. Or set to `0` (unlimited)
3. Check why process frequently crashes

### Issue: Restarting Too Frequently
1. Increase `RestartDelayMs`
2. Give process more startup time
3. Check if `StartupDelayMs` is sufficient

## Relationship with Exit Cleanup

| Event | Behavior | Auto-restart |
|-------|----------|--------------|
| Process exits unexpectedly | Monitor detects → Auto-restart | ✅ |
| Ctrl+C | Cleanup → Stop monitoring → Kill process | ❌ |
| Close window | Cleanup → Stop monitoring → Kill process | ❌ |
| Program normal exit | Cleanup all resources | ❌ |

**Note:** Only restarts when process exits **unexpectedly**. Won't restart when program actively stops.
