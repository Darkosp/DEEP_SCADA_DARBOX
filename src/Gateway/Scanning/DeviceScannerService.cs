using System.Globalization;
using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Gateway.Scanning;

/// <summary>
/// Polls every configured device on its own schedule and feeds the readings into the tag
/// engine — the driver end of the read path described in the Phase 0 architecture.
/// </summary>
/// <remarks>
/// Each device gets its own scan loop. The set of loops is reconciled against the
/// catalogue whenever configuration changes, so a device added through the UI starts
/// reporting without a gateway restart, which is what Phase 2's "no code required to add
/// a device" actually demands.
/// </remarks>
public sealed class DeviceScannerService : BackgroundService
{
    private readonly TagCatalogSource _catalogSource;
    private readonly ITagEngine _tagEngine;
    private readonly IReadOnlyDictionary<string, IDeviceDriverFactory> _factoriesByKey;
    private readonly IReadOnlyDictionary<string, IPushingDeviceDriverFactory> _pushingFactoriesByKey;
    private readonly IAlarmJournal _journal;
    private readonly TimeProvider _clock;
    private readonly PushedSourceSettings _pushedSources;
    private readonly ILogger<DeviceScannerService> _logger;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, RunningScan> _running = [];

    private CancellationToken _stoppingToken = CancellationToken.None;

    public DeviceScannerService(
        TagCatalogSource catalogSource,
        ITagEngine tagEngine,
        IEnumerable<IDeviceDriverFactory> driverFactories,
        IEnumerable<IPushingDeviceDriverFactory> pushingDriverFactories,
        IAlarmJournal journal,
        TimeProvider clock,
        PushedSourceSettings pushedSources,
        ILogger<DeviceScannerService> logger)
    {
        _catalogSource = catalogSource;
        _tagEngine = tagEngine;
        _journal = journal;
        _clock = clock;
        _pushedSources = pushedSources;
        _logger = logger;

        // Compile-time composition: the factories are whatever the composition root
        // registered, matched to a device only by its opaque driver key (ADR-0002).
        _factoriesByKey = driverFactories.ToDictionary(f => f.DriverKey, StringComparer.OrdinalIgnoreCase);
        _pushingFactoriesByKey = pushingDriverFactories.ToDictionary(f => f.DriverKey, StringComparer.OrdinalIgnoreCase);

        // One key, one shape (ADR-0016). A key registered as both would make whether a device
        // is polled or pushing depend on which dictionary was asked first.
        var both = _factoriesByKey.Keys.Intersect(_pushingFactoriesByKey.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        if (both.Count > 0)
        {
            throw new InvalidOperationException(
                $"Driver key(s) {string.Join(", ", both)} are registered as both polled and pushing.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _catalogSource.Changed += OnCatalogChanged;

        try
        {
            Reconcile(_catalogSource.Current);

            // The scan loops do the work; this task only waits for shutdown.
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            _catalogSource.Changed -= OnCatalogChanged;
            await StopAllAsync().ConfigureAwait(false);
        }
    }

    private void OnCatalogChanged(object? sender, TagCatalog catalog) => Reconcile(catalog);

    /// <summary>
    /// Brings the running scan loops in line with <paramref name="catalog"/>: starts loops
    /// for new devices, stops them for removed ones, and restarts a device whose
    /// configuration changed.
    /// </summary>
    private void Reconcile(TagCatalog catalog)
    {
        lock (_gate)
        {
            if (_stoppingToken.IsCancellationRequested)
            {
                return;
            }

            // A device an edge reads is not polled here (ADR-0019): the edge reads it, and its
            // values arrive over that edge's link. Everything else the Gateway acquires itself,
            // which is what leaves the on-premises topology unchanged.
            var desired = catalog.Devices
                .Where(device => catalog.EdgeOfDevice(device.Id) is null)
                .ToDictionary(device => device.Id);

            foreach (var (deviceId, scan) in _running.ToList())
            {
                var stillWanted = desired.TryGetValue(deviceId, out var device)
                                  && SignatureOf(device, catalog) == scan.Signature;

                if (stillWanted)
                {
                    continue;
                }

                // Restarting on any change is deliberately blunt. A driver holds a live
                // connection built from the device's settings, so reusing it after an edit
                // would mean reasoning about which fields can be changed underneath an
                // open socket.
                scan.Cancellation.Cancel();
                _running.Remove(deviceId);
            }

            foreach (var device in desired.Values)
            {
                if (_running.ContainsKey(device.Id))
                {
                    continue;
                }

                // Everything this device delivers: its own tags, and — for a device that carries
                // an edge's link — the tags of every device assigned to an edge that names it
                // (ADR-0019). A device an edge reads is not polled here, so that link is the only
                // thing feeding its tags, and a pushing driver refuses a sample naming a tag it
                // was not handed — the wider list is what lets it deliver them at all.
                var tags = catalog.TagsCarriedBy(device.Id)
                    .Select(tag => new DriverTag(tag.Id, tag.SourceAddress, tag.ValueKind))
                    .ToList();

                if (tags.Count == 0)
                {
                    // A device with no tags has nothing to poll. It reappears here as soon
                    // as its first tag is configured, because that changes the catalogue.
                    continue;
                }

                Func<CancellationToken, Task> run;
                if (_factoriesByKey.TryGetValue(device.DriverKey, out var factory))
                {
                    run = token => ScanDeviceAsync(device, factory, tags, token);
                }
                else if (_pushingFactoriesByKey.TryGetValue(device.DriverKey, out var pushingFactory))
                {
                    run = token => RunPushingDeviceAsync(device, pushingFactory, tags, token);
                }
                else
                {
                    _logger.LogError(
                        "Device {DeviceName} needs driver '{DriverKey}', which is not part of this build.",
                        device.Name,
                        device.DriverKey);
                    continue;
                }

                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
                var task = Task.Run(() => run(cancellation.Token), CancellationToken.None);

                _running[device.Id] = new RunningScan(cancellation, task, SignatureOf(device, catalog));
            }
        }
    }

    /// <summary>
    /// A value that changes whenever anything the scan loop depends on changes.
    /// </summary>
    private static string SignatureOf(Device device, TagCatalog catalog)
    {
        var settings = string.Join(
            ';',
            device.ConnectionSettings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

        // What the device is handed, so that a change to the set — including a device being
        // assigned to the edge this link carries (ADR-0019) — restarts the loop, and the driver
        // is handed the new list rather than keeping the one it started with.
        var tags = string.Join(
            ';',
            catalog.TagsCarriedBy(device.Id)
                .OrderBy(tag => tag.Id)
                .Select(tag => $"{tag.Id}:{tag.SourceAddress}:{tag.ValueKind}"));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{device.DriverKey}|{settings}|{device.ScanInterval.TotalMilliseconds}|{tags}");
    }

    private async Task ScanDeviceAsync(
        Device device,
        IDeviceDriverFactory factory,
        IReadOnlyList<DriverTag> driverTags,
        CancellationToken cancellationToken)
    {
        await using var driver = factory.Create(device);
        var connected = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!connected)
            {
                try
                {
                    await driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    connected = true;
                    _logger.LogInformation("Connected to device {DeviceName}.", device.Name);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Deliberately not rethrown, and deliberately not skipped either: the scan
                    // continues so the driver reports Bad quality for its tags. A device that is
                    // unreachable must show as unreadable, not as stale-but-fine (ADR-0003).
                    _logger.LogWarning(
                        exception,
                        "Cannot reach device {DeviceName}; its tags will report Bad quality.",
                        device.Name);
                }
            }

            try
            {
                var readings = await driver.ReadAsync(driverTags, cancellationToken).ConfigureAwait(false);
                await _tagEngine.IngestAsync(readings, cancellationToken).ConfigureAwait(false);

                // Any Bad reading means the link is suspect: drop it so the next cycle
                // reconnects rather than polling a dead socket forever.
                if (connected && readings.Any(r => r.Quality == Quality.Bad))
                {
                    connected = false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                connected = false;
                _logger.LogError(exception, "Scan of device {DeviceName} failed.", device.Name);
            }

            try
            {
                await Task.Delay(device.ScanInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs one pushing device (ADR-0016): the driver hands samples over as they arrive, and a
    /// watch beside it reads silence past the driver's limit as loss.
    /// </summary>
    /// <remarks>
    /// The watch runs whatever the driver does. A driver that fails, hangs or never connects
    /// hands nothing over — which is exactly the silence the watch exists to report — so the
    /// watch must not depend on the driver staying healthy.
    /// </remarks>
    private async Task RunPushingDeviceAsync(
        Device device,
        IPushingDeviceDriverFactory factory,
        IReadOnlyList<DriverTag> driverTags,
        CancellationToken cancellationToken)
    {
        await using var driver = factory.Create(device);
        var tagIds = driverTags.Select(tag => tag.TagId).ToList();

        // From here on, a tag that receives nothing is "no data since now", not merely unknown.
        _tagEngine.BeginListening(tagIds);
        var watch = WatchForSilenceAsync(device, tagIds, driver.StalenessLimit, cancellationToken);
        var recorder = new PushedSourceRecorder(
            device,
            _catalogSource.Current.DevicePathOf(device.Id),
            _journal,
            _clock,
            _pushedSources.ClockSkewTolerance);
        var sink = new TagEngineSink(_tagEngine, recorder, device, _logger);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await driver.RunAsync(driverTags, sink, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Pushing driver for device {DeviceName} failed; its tags read Bad once silent for {Limit}.",
                    device.Name,
                    driver.StalenessLimit);
            }

            try
            {
                await Task.Delay(PushingRestartDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await watch.ConfigureAwait(false);
    }

    /// <summary>How long a pushing driver that returned or failed waits before it is run again.</summary>
    private static readonly TimeSpan PushingRestartDelay = TimeSpan.FromSeconds(5);

    private async Task WatchForSilenceAsync(
        Device device,
        IReadOnlyCollection<Guid> tagIds,
        TimeSpan stalenessLimit,
        CancellationToken cancellationToken)
    {
        // Often enough that loss shows within a fraction of the limit past it; never so often
        // that a long limit means a busy loop, nor so rarely that a short one is missed by seconds.
        var interval = TimeSpan.FromTicks(Math.Clamp(
            stalenessLimit.Ticks / 4,
            TimeSpan.FromMilliseconds(250).Ticks,
            TimeSpan.FromSeconds(5).Ticks));

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                await _tagEngine.MarkSilentTagsAsync(tagIds, stalenessLimit, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Checking device {DeviceName} for silence failed.", device.Name);
            }
        }
    }

    /// <summary>
    /// Where a pushing driver's samples go — the tag engine's pushed path, never the polled one —
    /// and what it reports about its source goes to the journal.
    /// </summary>
    private sealed class TagEngineSink(
        ITagEngine engine,
        PushedSourceRecorder recorder,
        Device device,
        ILogger logger) : IPushedSampleSink
    {
        public Task AcceptAsync(IReadOnlyList<TagReading> samples, CancellationToken cancellationToken) =>
            engine.AcceptPushedAsync(samples, cancellationToken);

        public async Task ReportLossAsync(SourceLoss loss, CancellationToken cancellationToken)
        {
            if (await recorder.RecordLossAsync(loss, cancellationToken).ConfigureAwait(false))
            {
                logger.LogWarning(
                    "Device {DeviceName}: the source dropped {Count} sample(s) measured between {From:O} and {To:O}; journalled.",
                    device.Name,
                    loss.Count,
                    loss.FromSourceUtc,
                    loss.ToSourceUtc);
            }
        }

        public async Task ReportSourceClockAsync(DateTimeOffset sourceClockUtc, CancellationToken cancellationToken)
        {
            if (await recorder.ObserveSourceClockAsync(sourceClockUtc, cancellationToken).ConfigureAwait(false) is { } skew)
            {
                logger.LogWarning(
                    "Device {DeviceName}: the source's clock is {Seconds:F0} s {Direction} the Gateway's; journalled. Its samples keep the times it gave them.",
                    device.Name,
                    skew.Duration().TotalSeconds,
                    skew > TimeSpan.Zero ? "ahead of" : "behind");
            }
        }
    }

    private async Task StopAllAsync()
    {
        List<RunningScan> scans;

        lock (_gate)
        {
            scans = _running.Values.ToList();
            _running.Clear();
        }

        foreach (var scan in scans)
        {
            await scan.Cancellation.CancelAsync().ConfigureAwait(false);
        }

        // Let the loops unwind before the host tears the process down, so a driver gets
        // to close its connection rather than having it dropped.
        await Task.WhenAll(scans.Select(scan => scan.Task)).ConfigureAwait(false);

        foreach (var scan in scans)
        {
            scan.Cancellation.Dispose();
        }
    }

    private sealed record RunningScan(CancellationTokenSource Cancellation, Task Task, string Signature);
}
