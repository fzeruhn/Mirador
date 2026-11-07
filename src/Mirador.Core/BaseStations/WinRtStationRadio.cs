using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;

namespace Mirador.Core.BaseStations;

/// <summary>The Windows Bluetooth radio. The only class that calls Windows' Bluetooth APIs.</summary>
public sealed class WinRtStationRadio : IStationRadio
{
    private readonly object _gate = new();
    private readonly List<Watching> _watching = [];
    private BluetoothState _state = BluetoothState.Ready;

    // Kept in fields so Windows can't release the radio, which would end its StateChanged subscription.
    private BluetoothAdapter? _adapter;
    private Radio? _radio;

    /// <inheritdoc />
    public BluetoothState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public event Action? StateChanged;

    /// <inheritdoc />
    public async Task StartAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter is null || !adapter.IsLowEnergySupported)
            {
                SetState(BluetoothState.Missing);
                return;
            }

            var radio = await adapter.GetRadioAsync();
            if (radio is null)
            {
                SetState(BluetoothState.Missing);
                return;
            }

            _adapter = adapter;
            _radio = radio;
            radio.StateChanged += OnRadioStateChanged;
            SetState(Map(radio.State));
        }
        catch (Exception)
        {
            // Windows could not tell us about Bluetooth at all; treat it as not there.
            SetState(BluetoothState.Missing);
        }
    }

    /// <inheritdoc />
    public IDisposable Watch(Action<Advert> heard)
    {
        var watching = new Watching(this, heard);
        lock (_gate)
        {
            _watching.Add(watching);
        }

        watching.Start();
        return watching;
    }

    /// <inheritdoc />
    public async Task<IStationLink> ConnectAsync(ulong address, bool randomAddress, CancellationToken ct)
    {
        BluetoothLEDevice? device = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            device = await BluetoothLEDevice
                .FromBluetoothAddressAsync(address, randomAddress ? BluetoothAddressType.Random : BluetoothAddressType.Public)
                .AsTask(ct);
            if (device is null)
            {
                throw new StationException("Windows couldn't find the base station over Bluetooth.");
            }

            return new Link(device);
        }
        catch (StationException)
        {
            device?.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            device?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            device?.Dispose();
            throw new StationException("Windows couldn't connect to the base station over Bluetooth.", ex);
        }
    }

    private static BluetoothState Map(RadioState state) => state == RadioState.On ? BluetoothState.Ready : BluetoothState.Off;

    private void SetState(BluetoothState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception)
        {
            // A listener's failure is not the radio's.
        }

        if (state == BluetoothState.Ready)
        {
            Watching[] all;
            lock (_gate)
            {
                all = [.. _watching];
            }

            foreach (var watching in all)
            {
                watching.Resume();
            }
        }
    }

    private void OnRadioStateChanged(Radio sender, object args)
    {
        try
        {
            SetState(Map(sender.State));
        }
        catch (Exception)
        {
            // Never throw out of a Windows event.
        }
    }

    private void Remove(Watching watching)
    {
        lock (_gate)
        {
            _watching.Remove(watching);
        }
    }

    private sealed class Watching(WinRtStationRadio owner, Action<Advert> heard) : IDisposable
    {
        private const int MaxRestarts = 3;
        private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

        private readonly object _lock = new();
        private BluetoothLEAdvertisementWatcher? _watcher;
        private int _restarts;
        private bool _disposed;

        /// <summary>Bluetooth came back (a real radio transition): allows restarts again and starts a watcher if none is listening.</summary>
        public void Resume()
        {
            lock (_lock)
            {
                _restarts = 0;
            }

            Start();
        }

        /// <summary>Starts a watcher, unless one is already listening or this has been disposed.</summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                if (_watcher is { Status: BluetoothLEAdvertisementWatcherStatus.Started or BluetoothLEAdvertisementWatcherStatus.Created })
                {
                    return;
                }

                Detach();
                try
                {
                    var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
                    watcher.Received += OnReceived;
                    watcher.Stopped += OnStopped;
                    _watcher = watcher;
                    watcher.Start();
                }
                catch (Exception)
                {
                    // Bluetooth is off or going away; drop the watcher so the radio's next state change starts a new one.
                    Detach();
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Detach();
            }

            owner.Remove(this);
        }

        private void Detach()
        {
            if (_watcher is null)
            {
                return;
            }

            _watcher.Received -= OnReceived;
            _watcher.Stopped -= OnStopped;
            try
            {
                _watcher.Stop();
            }
            catch (Exception)
            {
                // Already stopped.
            }

            _watcher = null;
        }

        private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            try
            {
                lock (_lock)
                {
                    // Only the current watcher counts, and only a few times until the radio really changes state.
                    if (_disposed || !ReferenceEquals(sender, _watcher) || _restarts >= MaxRestarts)
                    {
                        return;
                    }

                    _restarts++;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(RestartDelay);
                        lock (_lock)
                        {
                            if (!ReferenceEquals(sender, _watcher))
                            {
                                return;
                            }
                        }

                        Start();
                    }
                    catch (Exception)
                    {
                        // Nothing to report; the next radio change tries again.
                    }
                });
            }
            catch (Exception)
            {
                // Never throw out of a Windows event.
            }
        }

        private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            try
            {
                lock (_lock)
                {
                    if (_disposed)
                    {
                        return;
                    }
                }

                var name = args.Advertisement.LocalName;
                if (StationProtocol.VersionOf(name) is null)
                {
                    return;
                }

                byte[]? valve = null;
                foreach (var section in args.Advertisement.ManufacturerData)
                {
                    if (section.CompanyId == StationProtocol.ValveCompany)
                    {
                        valve = section.Data.ToArray();
                        break;
                    }
                }

                heard(new Advert(args.BluetoothAddress, args.BluetoothAddressType == BluetoothAddressType.Random, name, valve));
            }
            catch (Exception)
            {
                // A failing callback must not take the Windows thread down; the advert is dropped.
            }
        }
    }

    private sealed class Link(BluetoothLEDevice device) : IStationLink
    {
        private readonly object _lock = new();
        private readonly List<GattDeviceService> _services = [];
        private bool _disposed;

        public async Task WriteAsync(Guid service, Guid characteristic, byte[] value, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var services = await device.GetGattServicesForUuidAsync(service, BluetoothCacheMode.Uncached).AsTask(ct);
                Keep(services.Services);
                if (services.Status != GattCommunicationStatus.Success)
                {
                    throw new StationException($"The base station didn't answer ({services.Status}).");
                }

                if (services.Services.Count == 0)
                {
                    throw new StationException("The base station doesn't have the service Mirador needs.");
                }

                var found = await services.Services[0].GetCharacteristicsForUuidAsync(characteristic, BluetoothCacheMode.Uncached).AsTask(ct);
                if (found.Status != GattCommunicationStatus.Success)
                {
                    throw new StationException($"The base station didn't answer ({found.Status}).");
                }

                if (found.Characteristics.Count == 0)
                {
                    throw new StationException("The base station doesn't have the setting Mirador needs.");
                }

                var result = await found.Characteristics[0]
                    .WriteValueWithResultAsync(value.AsBuffer(), GattWriteOption.WriteWithResponse)
                    .AsTask(ct);
                if (result.Status != GattCommunicationStatus.Success)
                {
                    var detail = result.ProtocolError is { } code ? $", error {code}" : string.Empty;
                    throw new StationException($"The base station refused the command ({result.Status}{detail}).");
                }
            }
            catch (StationException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new StationException("The base station stopped answering over Bluetooth.", ex);
            }
        }

        public ValueTask DisposeAsync()
        {
            GattDeviceService[] services;
            lock (_lock)
            {
                if (_disposed)
                {
                    return ValueTask.CompletedTask;
                }

                _disposed = true;
                services = [.. _services];
                _services.Clear();
            }

            foreach (var service in services)
            {
                service.Dispose();
            }

            device.Dispose();
            return ValueTask.CompletedTask;
        }

        private void Keep(IReadOnlyList<GattDeviceService> opened)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _services.AddRange(opened);
                    return;
                }
            }

            foreach (var service in opened)
            {
                service.Dispose();
            }
        }
    }
}
