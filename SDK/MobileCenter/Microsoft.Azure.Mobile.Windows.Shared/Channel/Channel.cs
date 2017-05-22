﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Mobile.Ingestion.Models;
using Microsoft.Azure.Mobile.Ingestion;
using Microsoft.Azure.Mobile.Storage;
using Microsoft.Azure.Mobile.Utils;
using Microsoft.Azure.Mobile.Utils.Synchronization;

namespace Microsoft.Azure.Mobile.Channel
{
    public sealed class Channel : IChannelUnit
    {
        private const int ClearBatchSize = 100;
        private Ingestion.Models.Device _device;
        private readonly string _appSecret;
        private readonly IStorage _storage;
        private readonly IIngestion _ingestion;
        private readonly IDeviceInformationHelper _deviceInfoHelper = new DeviceInformationHelper();
        private readonly Dictionary<string, List<Log>> _sendingBatches = new Dictionary<string, List<Log>>();
        private readonly int _maxParallelBatches;
        private readonly int _maxLogsPerBatch;
        private long _pendingLogCount;
        private bool _enabled;
        private bool _discardLogs;
        private bool _batchScheduled;
        private TimeSpan _batchTimeInterval;
        private readonly StatefulMutex _mutex;
        private readonly StateKeeper _stateKeeper = new StateKeeper();

        internal Channel(string name, int maxLogsPerBatch, TimeSpan batchTimeInterval, int maxParallelBatches,
            string appSecret, IIngestion ingestion, IStorage storage)
        {
            _mutex = new StatefulMutex(_stateKeeper);
            Name = name;
            _maxParallelBatches = maxParallelBatches;
            _maxLogsPerBatch = maxLogsPerBatch;
            _appSecret = appSecret;
            _ingestion = ingestion;
            _storage = storage;
            _batchTimeInterval = batchTimeInterval;
            _batchScheduled = false;
            _enabled = true;
            DeviceInformationHelper.InformationInvalidated += (sender, e) => InvalidateDeviceCache();
        }

        public async Task SetEnabledAsync(bool enabled)
        {
            await _mutex.LockAsync().ConfigureAwait(false);
            try
            {
                if (_enabled == enabled)
                {
                    return;
                }
                if (enabled)
                {
                    _enabled = true;
                    _discardLogs = false;
                    _stateKeeper.InvalidateState();
                    var stateSnapshot = _stateKeeper.GetStateSnapshot();
                    _mutex.Unlock();
                    var logCount = await _storage.CountLogsAsync(Name).ConfigureAwait(false);
                    await _mutex.LockAsync(stateSnapshot).ConfigureAwait(false);
                    _pendingLogCount = logCount;
                    await CheckPendingLogsAsync(stateSnapshot).ConfigureAwait(false);
                }
                else
                {
                    await SuspendAsync(true, new CancellationException()).ConfigureAwait(false);
                }
            }
            finally
            {
                _mutex.Unlock();
            }
        }

        /// <summary>
        /// Gets value indicating whether the Channel is enabled
        /// </summary>
        public bool IsEnabled
        {
            get
            {
                _mutex.Lock();
                try
                {
                    return _enabled;
                }
                finally
                {
                    _mutex.Unlock();
                }
            }
        }

        /// <summary>
        /// The channel's name
        /// </summary>
        public string Name { get; }

        #region Events
        public event EventHandler<EnqueuingLogEventArgs> EnqueuingLog;
        public event EventHandler<SendingLogEventArgs> SendingLog;
        public event EventHandler<SentLogEventArgs> SentLog;
        public event EventHandler<FailedToSendLogEventArgs> FailedToSendLog;
        #endregion

        public async Task EnqueueAsync(Log log)
        {
            try
            {
                await _mutex.LockAsync().ConfigureAwait(false);
                var stateSnapshot = _stateKeeper.GetStateSnapshot();
                if (_discardLogs)
                {
                    MobileCenterLog.Warn(MobileCenterLog.LogTag, "Channel is disabled; logs are discarded");
                    _mutex.Unlock();
                    SendingLog?.Invoke(this, new SendingLogEventArgs(log));
                    FailedToSendLog?.Invoke(this, new FailedToSendLogEventArgs(log, new CancellationException()));
                }
                _mutex.Unlock();
                EnqueuingLog?.Invoke(this, new EnqueuingLogEventArgs(log));
                await PrepareLogAsync(log, stateSnapshot).ConfigureAwait(false);
                await PersistLogAsync(log, stateSnapshot).ConfigureAwait(false);
            }
            catch (StatefulMutexException e)
            {
                MobileCenterLog.Warn(MobileCenterLog.LogTag, "The Enqueue operation has been cancelled", e);
            }
        }

        private async Task PrepareLogAsync(Log log, State stateSnapshot)
        {
            if (log.Device == null && _device == null)
            {
                var device = await _deviceInfoHelper.GetDeviceInformationAsync().ConfigureAwait(false);
                try
                {
                    await _mutex.LockAsync(stateSnapshot).ConfigureAwait(false);
                    _device = device;
                }
                finally
                {
                    _mutex.Unlock();
                }
            }
            log.Device = log.Device ?? _device;
            if (log.Toffset == 0L)
            {
                log.Toffset = TimeHelper.CurrentTimeInMilliseconds();
            }
        }

        private async Task PersistLogAsync(Log log, State stateSnapshot)
        {
            try
            {
                await _storage.PutLogAsync(Name, log).ConfigureAwait(false);
            }
            catch (StorageException e)
            {
                MobileCenterLog.Error(MobileCenterLog.LogTag, "Error persisting log", e);
                return;
            }
            try
            {
                await _mutex.LockAsync(stateSnapshot).ConfigureAwait(false);
                _pendingLogCount++;
                if (_enabled)
                {
                    await CheckPendingLogsAsync(stateSnapshot).ConfigureAwait(false);
                    return;
                }
                MobileCenterLog.Warn(MobileCenterLog.LogTag, "Channel is temporarily disabled; log was saved to disk");
            }
            catch (StatefulMutexException e)
            {
                MobileCenterLog.Warn(MobileCenterLog.LogTag, "The PersistLog operation has been cancelled", e);
            }
            finally
            {
                _mutex.Unlock();
            }
        }

        public void InvalidateDeviceCache()
        {
            _mutex.Lock();
            _device = null;
            _mutex.Unlock();
        }

        public async Task ClearAsync()
        {
            var stateSnapshot = _stateKeeper.GetStateSnapshot();
            await _storage.DeleteLogsAsync(Name).ConfigureAwait(false);
            try
            {
                await _mutex.LockAsync().ConfigureAwait(false);
                _pendingLogCount = 0;
            }
            catch (StatefulMutexException e)
            {
                MobileCenterLog.Warn(MobileCenterLog.LogTag, "The Clear operation has been cancelled", e);
            }
            finally
            {
                _mutex.Unlock();
            }
        }

        private async Task SuspendAsync(bool deleteLogs, Exception exception)
        {
            _enabled = false;
            _batchScheduled = false;
            _discardLogs = deleteLogs;
            _stateKeeper.InvalidateState();
            var stateSnapshot = _stateKeeper.GetStateSnapshot();
            try
            {
                if (deleteLogs && FailedToSendLog != null)
                {
                    foreach (var log in _sendingBatches.Values.SelectMany(batch => batch))
                    {
                        _mutex.Unlock();
                        FailedToSendLog?.Invoke(this, new FailedToSendLogEventArgs(log, exception));
                        await _mutex.LockAsync(stateSnapshot).ConfigureAwait(false);
                    }
                }
                try
                {
                    _ingestion.Close();
                }
                catch (IngestionException e)
                {
                    MobileCenterLog.Error(MobileCenterLog.LogTag, "Failed to close ingestion", e);
                }
                if (deleteLogs)
                {
                    _pendingLogCount = 0;
                    _mutex.Unlock();
                    await DeleteLogsOnSuspendedAsync().ConfigureAwait(false);
                    await _mutex.LockAsync(stateSnapshot).ConfigureAwait(false);
                    return;
                }
                _mutex.Unlock();
                await _storage.ClearPendingLogStateAsync(Name).ConfigureAwait(false);
                await _mutex.LockAsync(stateSnapshot).ConfigureAwait(false);
            }
            catch (StatefulMutexException e)
            {
                MobileCenterLog.Warn(MobileCenterLog.LogTag, "The CountFromDisk operation has been cancelled", e);
            }
        }

        private async Task DeleteLogsOnSuspendedAsync()
        {
            try
            {
                if (SendingLog != null || FailedToSendLog != null)
                {
                    await SignalDeletingLogsAsync().ConfigureAwait(false);
                }
            }
            catch (StorageException)
            {
                MobileCenterLog.Warn(MobileCenterLog.LogTag, "Failed to invoke events for logs being deleted.");
                return;
            }
            await _storage.DeleteLogsAsync(Name).ConfigureAwait(false);
        }

        private async Task SignalDeletingLogsAsync()
        {
            var logs = new List<Log>();
            await _storage.GetLogsAsync(Name, ClearBatchSize, logs).ConfigureAwait(false);
            foreach (var log in logs)
            {
                SendingLog?.Invoke(this, new SendingLogEventArgs(log));
                FailedToSendLog?.Invoke(this, new FailedToSendLogEventArgs(log, new CancellationException()));
            }
            if (logs.Count >= ClearBatchSize)
            {
                await SignalDeletingLogsAsync().ConfigureAwait(false);
            }
        }

        private async Task TriggerIngestionAsync(State stateSnapshot)
        {
            if (!_enabled)
            {
                return;
            }
            MobileCenterLog.Debug(MobileCenterLog.LogTag,
                $"triggerIngestion({Name}) pendingLogCount={_pendingLogCount}");
            _batchScheduled = false;
            if (_sendingBatches.Count >= _maxParallelBatches)
            {
                MobileCenterLog.Debug(MobileCenterLog.LogTag,
                    "Already sending " + _maxParallelBatches + " batches of analytics data to the server");
                return;
            }

            // Get a batch from storage
            var logs = new List<Log>();
            _mutex.Unlock();
            var batchId = await _storage.GetLogsAsync(Name, _maxLogsPerBatch, logs).ConfigureAwait(false);
            await _mutex.LockAsync(stateSnapshot).ConfigureAwait(false);
            if (batchId != null)
            {
                _sendingBatches.Add(batchId, logs);
                _pendingLogCount -= logs.Count;
                await TriggerIngestionAsync(logs, stateSnapshot, batchId).ConfigureAwait(false);
                await CheckPendingLogsAsync(stateSnapshot).ConfigureAwait(false);
            }
        }

        private async Task TriggerIngestionAsync(IList<Log> logs, State stateSnapshot, string batchId)
        {
            // Before sending logs, trigger the sending event for this channel
            if (SendingLog != null)
            {
                foreach (var eventArgs in logs.Select(log => new SendingLogEventArgs(log)))
                {
                    _mutex.Unlock();
                    SendingLog?.Invoke(this, eventArgs);
                    await CheckPendingLogsAsync(stateSnapshot).ConfigureAwait(false);
                }
            }
            // If the optional Install ID has no value, default to using empty GUID
            var installId = MobileCenter.InstallId.HasValue ? MobileCenter.InstallId.Value : Guid.Empty;
            using (var serviceCall = _ingestion.PrepareServiceCall(_appSecret, installId, logs))
            {
                try
                {
                    _mutex.Unlock();
                    try
                    {
                        await serviceCall.ExecuteAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        await CheckPendingLogsAsync(stateSnapshot).ConfigureAwait(false);
                    }
                }
                catch (IngestionException exception)
                {
                    await HandleSendingFailureAsync(batchId, exception).ConfigureAwait(false);
                    return;
                }
            }
            if (!_stateKeeper.IsCurrent(stateSnapshot))
            {
                return;
            }
            try
            {
                _mutex.Unlock();
                await _storage.DeleteLogsAsync(Name, batchId).ConfigureAwait(false);
            }
            catch (StorageException e)
            {
                MobileCenterLog.Warn(MobileCenterLog.LogTag, $"Could not delete logs for batch {batchId}", e);
            }
            finally
            {
                await CheckPendingLogsAsync(stateSnapshot).ConfigureAwait(false);
            }
            var removedLogs = _sendingBatches[batchId];
            _sendingBatches.Remove(batchId);
            if (SentLog != null)
            {
                foreach (var log in removedLogs)
                {
                    _mutex.Unlock();
                    SentLog?.Invoke(this, new SentLogEventArgs(log));
                    await CheckPendingLogsAsync(stateSnapshot).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleSendingFailureAsync(string batchId, IngestionException e)
        {
            var isRecoverable = e?.IsRecoverable ?? false;
            MobileCenterLog.Error(MobileCenterLog.LogTag, $"Sending logs for channel '{Name}', batch '{batchId}' failed", e);
            var removedLogs = _sendingBatches[batchId];
            _sendingBatches.Remove(batchId);
            if (!isRecoverable && FailedToSendLog != null)
            {
                foreach (var log in removedLogs)
                {
                    FailedToSendLog?.Invoke(this, new FailedToSendLogEventArgs(log, e));
                }
            }
            await SuspendAsync(!isRecoverable, e).ConfigureAwait(false);
            if (isRecoverable)
            {
                _pendingLogCount += removedLogs.Count;
            }
        }

        private async Task CheckPendingLogsAsync(State stateSnapshot)
        {
            if (!_enabled)
            {
                MobileCenterLog.Info(MobileCenterLog.LogTag, "The service has been disabled. Stop processing logs.");
            }

            MobileCenterLog.Debug(MobileCenterLog.LogTag, $"CheckPendingLogs({Name}) pending log count: {_pendingLogCount}");
            if (_pendingLogCount >= _maxLogsPerBatch)
            {
                await TriggerIngestionAsync(stateSnapshot).ConfigureAwait(false);
            }
            else if (_pendingLogCount > 0 && !_batchScheduled)
            {
                _batchScheduled = true;

                // No need wait _batchTimeInterval here.
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                Task.Run(async () =>
                {
                    await Task.Delay((int)_batchTimeInterval.TotalMilliseconds).ConfigureAwait(false);
                    if (_batchScheduled)
                    {
                        try
                        {
                            await _mutex.LockAsync().ConfigureAwait(false);
                            await TriggerIngestionAsync(_stateKeeper.GetStateSnapshot()).ConfigureAwait(false);
                        }
                        catch (StatefulMutexException e)
                        {
                            MobileCenterLog.Warn(MobileCenterLog.LogTag, "The TriggerIngestion operation has been cancelled", e);
                        }
                        finally
                        {
                            _mutex.Unlock();
                        }
                    }
                });
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            }
        }

        public async Task ShutdownAsync()
        {
            await _mutex.LockAsync().ConfigureAwait(false);
            try
            {
                await SuspendAsync(false, new CancellationException()).ConfigureAwait(false);
            }
            finally
            {
                _mutex.Unlock();
            }
        }

        public void Dispose()
        {
            _mutex.Dispose();
        }
    }
}
