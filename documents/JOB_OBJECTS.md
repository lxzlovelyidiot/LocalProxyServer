# Windows Job Objects for Process Management

## Problem

When closing the console window directly (not using Ctrl+C), the upstream process would continue running even with `AppDomain.ProcessExit` and `Console.CancelKeyPress` event handlers. This is because:

1. Windows doesn't wait for .NET cleanup code to run when forcibly closing
2. Child processes don't automatically terminate when parent is killed
3. Event handlers may not execute in crash scenarios

## Solution: Windows Job Objects

A **Job Object** is a Windows kernel object that groups processes together. When configured with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, all processes in the job are automatically terminated when the job handle is closed.

### Key Advantages

? **Works at OS level** - Independent of .NET cleanup code  
? **Guaranteed termination** - Even when:
  - Console window is closed
  - Process is killed via Task Manager
  - Application crashes
  - No cleanup handlers run

? **Handles process trees** - Kills child processes automatically  
? **Zero overhead** - No polling or monitoring required

## Implementation

### JobObject.cs

```csharp
[SupportedOSPlatform("windows")]
public class JobObject : IDisposable
{
    // Creates a job with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE flag
    public JobObject(string? name = null);
    
    // Adds a process to the job
    public bool AddProcess(Process process);
    
    // Disposing the job kills all processes
    public void Dispose();
}
```

### Usage in UpstreamProcessManager

```csharp
public class UpstreamProcessManager : IDisposable
{
    private JobObject? _jobObject;
    
    public UpstreamProcessManager(...)
    {
        // Create job object on Windows
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _jobObject = new JobObject("LocalProxyServer_UpstreamJob");
        }
    }
    
    public async Task<bool> StartAsync(...)
    {
        _process.Start();
        
        // Add to job - will be killed when job is disposed
        _jobObject?.AddProcess(_process);
        
        return true;
    }
    
    public void Dispose()
    {
        // Disposing job automatically kills all processes
        _jobObject?.Dispose();
    }
}
```

## How It Works

### 1. Job Creation
```csharp
// P/Invoke to create job object
SafeJobHandle jobHandle = CreateJobObject(IntPtr.Zero, name);

// Configure to kill processes on close
var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    }
};

SetInformationJobObject(jobHandle, JobObjectInfoType.ExtendedLimitInformation, ...);
```

### 2. Process Assignment
```csharp
// Assign process to job
AssignProcessToJobObject(jobHandle, processHandle);
```

### 3. Automatic Cleanup
When the job handle is closed (via Dispose or process exit), Windows automatically:
1. Terminates all processes in the job
2. Waits for processes to exit
3. Cleans up resources

## Testing

### Test 1: Close Console Window
```cmd
# Start proxy
dotnet run

# Close console window (X button)
# Result: Upstream process should terminate immediately
```

### Test 2: Kill via Task Manager
```cmd
# Start proxy
dotnet run

# Open Task Manager
# Kill "LocalProxyServer" process
# Result: ssh.exe (upstream) should also disappear
```

### Test 3: Process Tree Verification
```cmd
# Start proxy
dotnet run

# List process tree
wmic process where name='ssh.exe' get ProcessId,ParentProcessId

# Kill parent
taskkill /F /PID <parent_pid>

# Verify child is also killed
tasklist | findstr ssh
# Should return nothing
```

### Test 4: Verify Job Assignment
Check logs for:
```
[Debug] Created Windows Job Object for upstream process management
[Debug] Added process 12345 to Job Object. It will be terminated when parent exits.
```

## Cross-Platform Support

| Platform | Job Objects | Fallback |
|----------|-------------|----------|
| Windows | ? Supported | Kill entire process tree |
| Linux | ? Not available | Kill entire process tree |
| macOS | ? Not available | Kill entire process tree |

On non-Windows platforms, the system falls back to:
1. Event handlers (`ProcessExit`, `CancelKeyPress`)
2. Manual process termination with `Kill(entireProcessTree: true)`

## Limitations

### Job Objects Won't Work If:
1. **Already in a job** - Process is already assigned to another job (Windows limitation)
2. **Insufficient privileges** - Rare, but possible in restricted environments
3. **Non-Windows platform** - Falls back to manual cleanup

### When Job Creation Fails:
```
[Warning] Failed to create Job Object. Upstream process may not terminate on parent exit.
```

The system will still work but relies on event handlers and manual cleanup.

## Technical Details

### P/Invoke APIs Used
- `CreateJobObject` - Create job object
- `SetInformationJobObject` - Configure job limits
- `AssignProcessToJobObject` - Add process to job
- `CloseHandle` - Close job handle (triggers termination)

### Job Object Flags
- `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (0x00002000)` - Kill all processes when job closed

### Process Tree Handling
Job Objects automatically handle child processes:
- If upstream process spawns children, they are also in the job
- All children are killed when job is closed
- No need for manual tree traversal

## References

- [Microsoft Docs: Job Objects](https://docs.microsoft.com/en-us/windows/win32/procthread/job-objects)
- [MSDN: JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE](https://docs.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information)
