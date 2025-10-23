using System;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace Shadowsocks.Controller.Service
{
    internal sealed class ConnectivityMonitor : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly TimeSpan _interval;
        private readonly Func<CancellationToken, Task<bool>> _probe;
        private readonly object _syncRoot = new object();

        private Timer _timer;
        private CancellationTokenSource _cts;
        private CancellationTokenSource _currentProbeCts;
        private bool _isDisposed;
        private bool _checkInProgress;
        private bool? _lastResult;

        public event EventHandler<bool> ConnectivityChanged;

        public ConnectivityMonitor(TimeSpan interval, Func<CancellationToken, Task<bool>> probe)
        {
            _interval = interval;
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        }

        public void Start()
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                _lastResult = null;
                if (_timer != null)
                {
                    // Cancel any in-flight probe so mode switches don't wait for the previous check to finish.
                    if (_currentProbeCts != null)
                    {
                        try { _currentProbeCts.Cancel(); } catch { }
                        try { _currentProbeCts.Dispose(); } catch { }
                        _currentProbeCts = null;
                    }

                    if (_cts != null)
                    {
                        try { _cts.Cancel(); } catch { }
                        try { _cts.Dispose(); } catch { }
                    }

                    _cts = new CancellationTokenSource();
                    _checkInProgress = false;
                    _timer.Change(TimeSpan.Zero, _interval);
                    return;
                }
                _cts = new CancellationTokenSource();
                _timer = new Timer(OnTimerTick, null, TimeSpan.Zero, _interval);
            }
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                if (_timer == null)
                {
                    return;
                }

                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                _timer.Dispose();
                _timer = null;

                // Cancel the timer-level CTS so new probes won't start
                try
                {
                    _cts?.Cancel();
                }
                catch { }
                finally
                {
                    _cts?.Dispose();
                    _cts = null;
                }

                // Also cancel any in-flight probe CTS to ensure running probe is cancelled
                try
                {
                    _currentProbeCts?.Cancel();
                }
                catch { }
                finally
                {
                    _currentProbeCts?.Dispose();
                    _currentProbeCts = null;
                }

                _checkInProgress = false;
            }
        }

        private void OnTimerTick(object state)
        {
            CancellationToken token;

            lock (_syncRoot)
            {
                if (_timer == null || _checkInProgress)
                {
                    return;
                }

                if (_cts == null)
                {
                    // no active timer-level CTS, treat as stopped
                    return;
                }

                _checkInProgress = true;

                // Create a linked CTS for this probe so it can be cancelled independently
                _currentProbeCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                token = _currentProbeCts.Token;
            }

            _ = CheckOnceAsync(token);
        }

        private async Task CheckOnceAsync(CancellationToken cancellationToken)
        {
            bool result = false;
            bool succeeded = false;
            try
            {
                result = await _probe(cancellationToken).ConfigureAwait(false);
                succeeded = true;
            }
            catch (OperationCanceledException)
            {
                // expected when stopping; ignore
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Connectivity probe failed");
            }
            finally
            {
                lock (_syncRoot)
                {
                    _checkInProgress = false;
                }
            }

            // dispose per-probe CTS if present
            lock (_syncRoot)
            {
                if (_currentProbeCts != null)
                {
                    try { _currentProbeCts.Dispose(); } catch { }
                    _currentProbeCts = null;
                }
            }

            if (!succeeded)
            {
                result = false;
            }

            bool? previous;
            lock (_syncRoot)
            {
                previous = _lastResult;
                _lastResult = result;
            }

            if (previous != result)
            {
                ConnectivityChanged?.Invoke(this, result);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(ConnectivityMonitor));
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_isDisposed)
                {
                    return;
                }

                Stop();
                _isDisposed = true;
            }
        }
    }
}
