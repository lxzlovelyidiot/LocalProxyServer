using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace LocalProxyServer
{
    /// <summary>
    /// Manages the lifecycle of an upstream proxy process.
    /// Uses Windows Job Objects to ensure process termination when parent exits.
    /// </summary>
    public class UpstreamProcessManager : IDisposable
    {
        private readonly UpstreamProcessConfiguration _config;
        private readonly ILogger? _logger;
        private Process? _process;
        private bool _disposed;
        private bool _isStopping;
        private int _restartAttempts;
        private CancellationTokenSource? _monitorCts;
        private Task? _monitorTask;
        private JobObject? _jobObject;

        public UpstreamProcessManager(UpstreamProcessConfiguration config, ILogger? logger = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger;

            // Create job object on Windows to ensure child processes are killed when parent exits
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    _jobObject = new JobObject("LocalProxyServer_UpstreamJob");
                    _logger?.LogDebug("Created Windows Job Object for upstream process management");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to create Job Object. Upstream process may not terminate on parent exit.");
                }
            }
        }

        /// <summary>
        /// Starts the upstream process if configured to auto-start.
        /// </summary>
        public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
        {
            if (!_config.AutoStart)
            {
                _logger?.LogInformation("Upstream process auto-start is disabled");
                return false;
            }

            if (string.IsNullOrEmpty(_config.FileName))
            {
                _logger?.LogError("Cannot start upstream process: FileName is not configured");
                return false;
            }

            try
            {
                // Expand environment variables in configuration
                var fileName = Environment.ExpandEnvironmentVariables(_config.FileName);
                var arguments = string.IsNullOrEmpty(_config.Arguments) 
                    ? "" 
                    : Environment.ExpandEnvironmentVariables(_config.Arguments);
                var workingDirectory = string.IsNullOrEmpty(_config.WorkingDirectory)
                    ? ""
                    : Environment.ExpandEnvironmentVariables(_config.WorkingDirectory);

                _logger?.LogInformation("Starting upstream process: {FileName} {Arguments}", 
                    fileName, arguments);

                if (!string.IsNullOrEmpty(workingDirectory))
                {
                    _logger?.LogDebug("Working directory: {WorkingDirectory}", workingDirectory);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = _config.RedirectOutput,
                    RedirectStandardError = _config.RedirectOutput
                };

                if (!string.IsNullOrEmpty(workingDirectory))
                {
                    startInfo.WorkingDirectory = workingDirectory;
                }

                _process = new Process { StartInfo = startInfo };

                if (_config.RedirectOutput)
                {
                    _process.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            _logger?.LogInformation("[Upstream Output] {Output}", e.Data);
                        }
                    };

                    _process.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            _logger?.LogWarning("[Upstream Error] {Error}", e.Data);
                        }
                    };
                }

                if (!_process.Start())
                {
                    _logger?.LogError("Failed to start upstream process");
                    return false;
                }

                // Add process to job object (Windows only)
                if (_jobObject != null)
                {
                    try
                    {
                        if (_jobObject.AddProcess(_process))
                        {
                            _logger?.LogDebug("Added process {ProcessId} to Job Object. It will be terminated when parent exits.", _process.Id);
                        }
                        else
                        {
                            _logger?.LogWarning("Failed to add process {ProcessId} to Job Object. It may not terminate on parent exit.", _process.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error adding process to Job Object");
                    }
                }

                if (_config.RedirectOutput)
                {
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                }

                _logger?.LogInformation("Upstream process started with PID {ProcessId}", _process.Id);

                // Wait for startup delay
                if (_config.StartupDelayMs > 0)
                {
                    _logger?.LogDebug("Waiting {Delay}ms for upstream process to initialize", 
                        _config.StartupDelayMs);
                    
                    await Task.Delay(_config.StartupDelayMs, cancellationToken);
                }

                // Check if process is still running
                if (_process.HasExited)
                {
                    _logger?.LogError("Upstream process exited immediately with code {ExitCode}", 
                        _process.ExitCode);
                    return false;
                }

                // Start monitoring the process if auto-restart is enabled
                if (_config.AutoRestart)
                {
                    _monitorCts = new CancellationTokenSource();
                    _monitorTask = MonitorProcessAsync(_monitorCts.Token);
                    _logger?.LogInformation("Process monitoring enabled with auto-restart");
                }

                _logger?.LogInformation("Upstream process is ready");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to start upstream process");
                return false;
            }
        }

        /// <summary>
        /// Monitors the upstream process and restarts it if it exits unexpectedly.
        /// </summary>
        private async Task MonitorProcessAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && !_isStopping)
                {
                    if (_process == null || _process.HasExited)
                    {
                        var exitCode = _process?.ExitCode ?? -1;
                        _logger?.LogWarning("Upstream process exited unexpectedly with code {ExitCode}", exitCode);

                        // Check if we should restart
                        if (_config.MaxRestartAttempts > 0 && _restartAttempts >= _config.MaxRestartAttempts)
                        {
                            _logger?.LogError("Maximum restart attempts ({Max}) reached. Giving up", 
                                _config.MaxRestartAttempts);
                            break;
                        }

                        _restartAttempts++;
                        _logger?.LogInformation("Attempting to restart upstream process (attempt {Attempt}/{Max})",
                            _restartAttempts, _config.MaxRestartAttempts > 0 ? _config.MaxRestartAttempts.ToString() : "unlimited");

                        // Wait before restarting
                        await Task.Delay(_config.RestartDelayMs, cancellationToken);

                        // Restart the process
                        if (!await RestartProcessAsync(cancellationToken))
                        {
                            _logger?.LogError("Failed to restart upstream process");
                            break;
                        }

                        _logger?.LogInformation("Upstream process restarted successfully");
                    }
                    else
                    {
                        // Process is running, wait a bit before checking again
                        await Task.Delay(1000, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in process monitor");
            }
        }

        /// <summary>
        /// Restarts the upstream process.
        /// </summary>
        private async Task<bool> RestartProcessAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Dispose old process
                _process?.Dispose();

                // Expand environment variables
                var fileName = Environment.ExpandEnvironmentVariables(_config.FileName!);
                var arguments = string.IsNullOrEmpty(_config.Arguments)
                    ? ""
                    : Environment.ExpandEnvironmentVariables(_config.Arguments);
                var workingDirectory = string.IsNullOrEmpty(_config.WorkingDirectory)
                    ? ""
                    : Environment.ExpandEnvironmentVariables(_config.WorkingDirectory);

                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = _config.RedirectOutput,
                    RedirectStandardError = _config.RedirectOutput
                };

                if (!string.IsNullOrEmpty(workingDirectory))
                {
                    startInfo.WorkingDirectory = workingDirectory;
                }

                _process = new Process { StartInfo = startInfo };

                if (_config.RedirectOutput)
                {
                    _process.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            _logger?.LogInformation("[Upstream Output] {Output}", e.Data);
                        }
                    };

                    _process.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            _logger?.LogWarning("[Upstream Error] {Error}", e.Data);
                        }
                    };
                }

                if (!_process.Start())
                {
                    return false;
                }

                // Add process to job object (Windows only)
                if (_jobObject != null)
                {
                    try
                    {
                        if (_jobObject.AddProcess(_process))
                        {
                            _logger?.LogDebug("Added restarted process {ProcessId} to Job Object", _process.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Error adding restarted process to Job Object");
                    }
                }

                if (_config.RedirectOutput)
                {
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                }

                _logger?.LogInformation("Upstream process restarted with PID {ProcessId}", _process.Id);

                // Wait for startup delay
                if (_config.StartupDelayMs > 0)
                {
                    await Task.Delay(_config.StartupDelayMs, cancellationToken);
                }

                // Check if process is still running
                return !_process.HasExited;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error restarting upstream process");
                return false;
            }
        }

        /// <summary>
        /// Stops the upstream process gracefully.
        /// </summary>
        public void Stop()
        {
            _isStopping = true;

            // Stop monitoring
            _monitorCts?.Cancel();
            try
            {
                _monitorTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            if (_process == null || _process.HasExited)
            {
                return;
            }

            try
            {
                _logger?.LogInformation("Stopping upstream process (PID {ProcessId})", _process.Id);

                // Try graceful shutdown first
                _process.CloseMainWindow();

                if (!_process.WaitForExit(5000))
                {
                    _logger?.LogWarning("Upstream process did not exit gracefully, killing it");
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(2000);
                }

                _logger?.LogInformation("Upstream process stopped with exit code {ExitCode}", 
                    _process.ExitCode);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error stopping upstream process");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _monitorCts?.Dispose();
            _process?.Dispose();

            // Dispose job object - this will kill all processes in the job
            _jobObject?.Dispose();

            _disposed = true;
        }
    }
}
