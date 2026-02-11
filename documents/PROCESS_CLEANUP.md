# LocalProxyServer - Process Lifecycle Management

## Features

### 1. Automatic Process Cleanup on Exit (Enhanced with Windows Job Objects)

The upstream process will be terminated in all exit scenarios, including when the console window is closed directly.

**Solution:** Implemented multiple cleanup handlers plus Windows Job Objects for guaranteed process termination.

### 2. Automatic Process Restart on Crash

If the upstream process exits unexpectedly, it will be automatically restarted.

## Exit Handling

### Cleanup Mechanisms

#### 1. **Windows Job Objects (Primary Mechanism)**
- **Platform:** Windows only
- **How it works:** All upstream processes are added to a Windows Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` flag
- **Result:** When the parent process exits (for any reason), Windows automatically terminates all processes in the job
- **Advantage:** Works even when:
  - Console window is closed
  - Process is killed via Task Manager
  - System crashes
  - No cleanup code runs

#### 2. **AppDomain.ProcessExit Event**
Catches process termination from:
- Console window being closed
- Task Manager killing the process
- System shutdown
- Application crash

#### 3. **Console.CancelKeyPress Event**
Catches:
- Ctrl+C
- Ctrl+Break

#### 4. **Static Resource Management**
Moved critical resources (`_upstreamProcess`, `_proxy`, `_crlServer`) to static fields so they can be accessed from event handlers.

## Automatic Restart Feature

### How It Works

The `UpstreamProcessManager` continuously monitors the upstream process:

1. **Process Monitoring**: Checks every second if the process is still running
2. **Crash Detection**: When process exits unexpectedly, immediately detects it
3. **Restart Logic**: 
   - Wait for configured delay (`RestartDelayMs`)
   - Attempt to restart the process
   - Increment restart attempt counter
4. **Retry Limit**: Stop trying after `MaxRestartAttempts` (0 = unlimited)
5. **Logging**: All restart attempts and failures are logged

### Configuration

```json
{
  "Process": {
    "AutoStart": true,
    "FileName": "ssh.exe",
    "Arguments": "...",
    "AutoRestart": true,           // Enable auto-restart
    "MaxRestartAttempts": 5,       // Max restart attempts (0 = unlimited)
    "RestartDelayMs": 3000         // Wait 3 seconds before restart
  }
}
```

### Configuration Options

| Setting | Default | Description |
|---------|---------|-------------|
| `AutoRestart` | `true` | Enable automatic restart on crash |
| `MaxRestartAttempts` | `5` | Maximum restart attempts (0 = unlimited) |
| `RestartDelayMs` | `3000` | Delay before restart attempt (milliseconds) |

### Example Scenarios

#### Scenario 1: SSH Connection Drops
```
[Info] Upstream process exited unexpectedly with code 255
[Info] Attempting to restart upstream process (attempt 1/5)
[Info] Waiting 3000ms before restart
[Info] Upstream process restarted with PID 12345
[Info] Upstream process is ready
```

#### Scenario 2: Process Keeps Crashing
```
[Info] Upstream process exited unexpectedly with code 1
[Info] Attempting to restart (attempt 1/5)
[Info] Upstream process restarted with PID 12345
[Warn] Upstream process exited unexpectedly with code 1
[Info] Attempting to restart (attempt 2/5)
...
[Error] Maximum restart attempts (5) reached. Giving up
```

#### Scenario 3: Unlimited Restarts
```json
{
  "MaxRestartAttempts": 0,  // Unlimited
  "RestartDelayMs": 5000
}
```
Process will keep restarting indefinitely until manually stopped.

## Exit Scenarios Covered

| Scenario | Handler | Upstream Process Cleanup | Auto-Restart |
|----------|---------|--------------------------|--------------|
| Ctrl+C | `OnCancelKeyPress` | ? Yes | ? No |
| Console window closed | `OnProcessExit` | ? Yes | ? No |
| Task Manager kill | `OnProcessExit` | ? Yes | ? No |
| System shutdown | `OnProcessExit` | ? Yes | ? No |
| Application exception | `OnProcessExit` | ? Yes | ? No |
| **Upstream process crash** | **Monitor Loop** | **? Restart** | **? Yes** |

## Testing

### Test Exit Handling

1. Start LocalProxyServer with upstream process enabled
2. Verify the upstream process is running (check Task Manager)
3. Close the console window directly (click X button)
4. Check Task Manager - the upstream process should be terminated

### Test Auto-Restart

1. Start LocalProxyServer with `AutoRestart: true`
2. Find the upstream process in Task Manager
3. Kill the upstream process manually
4. Watch the logs - should see restart attempt
5. Verify new process starts with different PID

## Technical Details

### Windows Job Objects

```csharp
// Create job object
_jobObject = new JobObject("LocalProxyServer_UpstreamJob");

// Start process
_process.Start();

// Add to job - process will be killed when job is disposed
_jobObject.AddProcess(_process);
```

**Configuration:**
- `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`: All processes in the job are terminated when the job handle is closed
- Works at OS level, independent of .NET cleanup code
- Automatically handles process trees (kills child processes too)

### Cleanup Process

```csharp
private static void Cleanup()
{
    // 1. Stop proxy server
    _proxy?.Stop();
    
    // 2. Stop CRL server
    _crlServer?.Stop();
    
    // 3. Stop upstream process (disables monitoring and kills process)
    _upstreamProcess?.Stop();
}
```

### Process Monitoring Loop

```csharp
private async Task MonitorProcessAsync(CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        if (_process.HasExited)
        {
            // Log crash
            // Check restart attempts
            // Wait for RestartDelayMs
            // Restart process
        }
        await Task.Delay(1000); // Check every second
    }
}
```

### Upstream Process Termination

The system uses multiple layers of protection:

1. **Job Object Disposal** (Most reliable - Windows only)
   - Disposes job handle
   - OS automatically kills all processes in job
   - Works even if cleanup code doesn't run

2. **Graceful Shutdown** (Best effort)
   - `_isStopping` flag disables auto-restart
   - Cancels monitoring task
   - Tries `CloseMainWindow()` first
   - Waits 5 seconds

3. **Force Kill** (Fallback)
   - If still running, calls `Kill(entireProcessTree: true)`
   - Ensures child processes are also terminated

4. **Cross-platform Support**
   - Job Objects: Windows only
   - Graceful + Force kill: All platforms

## Error Handling

All operations are wrapped in try-catch blocks to ensure:
- One component's failure doesn't prevent others from cleaning up
- Errors are logged but don't crash the cleanup process
- Restart failures are logged and counted
- Process monitoring continues even if one restart fails

## Logging

All lifecycle events are logged:
- ? Process start
- ? Process exit (with exit code)
- ? Restart attempts (with attempt number)
- ? Restart success/failure
- ? Maximum attempts reached
- ? Manual stop
- ? Graceful shutdown vs force kill
