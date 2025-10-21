using Antilatency.Alt.Environment.Selector;
using Antilatency.Alt.Tracking;
using Antilatency.DeviceNetwork;
using antilatency_getter;
using HIVE.Commons.Flatbuffers.Generated;
using System.Collections.Concurrent;

internal class Program
{
    private static volatile bool _running = true;
    private static readonly ConcurrentDictionary<ulong, AltData> _altDevices = new();
    private static StreamWriter _writer;
    private static bool _useBlueEnvironment = true;

    static void Main(string[] args)
    {
        Console.WriteLine("Antilatency Tracker Data Collector Starting...");
        Console.WriteLine("Press 'q' to quit, 's' for status, 't' to toggle environment");

        AppDomain.CurrentDomain.ProcessExit += (s, e) => StopApplication();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            StopApplication();
        };

        try
        {
            // Initialize file writer
            _writer = new StreamWriter("nodeData.txt", append: true);
            _writer.WriteLine($"=== Session started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

            DataCollector();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        finally
        {
            StopApplication();
        }
    }

    private static void StopApplication()
    {
        _running = false;

        // Clean up all cotasks
        foreach (var device in _altDevices.Values)
        {
            device.Cotask?.Dispose();
        }
        _altDevices.Clear();

        _writer?.WriteLine($"=== Session ended at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        _writer?.Close();
        _writer?.Dispose();
        Console.WriteLine("Application stopped.");
    }

    private static void DataCollector()
    {
        // Load libraries
        using Antilatency.Alt.Tracking.ILibrary trackingLibrary = Antilatency.Alt.Tracking.Library.load();
        using Antilatency.Alt.Environment.Selector.ILibrary environmentSelectorLibrary = Antilatency.Alt.Environment.Selector.Library.load();

        // Create network and tracking constructor - FIXED THIS LINE
        using INetwork network = AntilatencyHandler.CreateNetwork();
        using ITrackingCotaskConstructor trackingCotaskConstructor = trackingLibrary.createTrackingCotaskConstructor(); // CHANGED TYPE

        // Create environments
        using Antilatency.Alt.Environment.IEnvironment blueEnvironment = AntilatencyHandler.CreateEnvironmentBlue(environmentSelectorLibrary);
        using Antilatency.Alt.Environment.IEnvironment greenEnvironment = AntilatencyHandler.CreateEnvironmentGreen(environmentSelectorLibrary);

        uint previousUpdateId = 0;
        Console.WriteLine("Waiting for tracking data...");

        while (_running)
        {
            // Handle user input
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                switch (key.KeyChar)
                {
                    case 'q':
                    case 'Q':
                        _running = false;
                        break;
                    case 's':
                    case 'S':
                        PrintStatus();
                        break;
                    case 't':
                    case 'T':
                        _useBlueEnvironment = !_useBlueEnvironment;
                        Console.WriteLine($"Environment switched to: {(_useBlueEnvironment ? "BLUE" : "GREEN")}");
                        // Reconnect all devices with new environment
                        ReconnectAllDevices(network, trackingCotaskConstructor,
                            _useBlueEnvironment ? blueEnvironment : greenEnvironment,
                            _useBlueEnvironment);
                        break;
                }
            }

            // Discover new devices when network updates
            uint currentUpdateId = network.getUpdateId();
            if (previousUpdateId != currentUpdateId)
            {
                previousUpdateId = currentUpdateId;
                DiscoverAndAddDevices(network, trackingCotaskConstructor,
                    _useBlueEnvironment ? blueEnvironment : greenEnvironment,
                    _useBlueEnvironment);
            }

            // Process tracking data for all connected devices
            ProcessTrackingData(_useBlueEnvironment);

            Thread.Sleep(16); // ~60Hz
        }
    }

    private static void DiscoverAndAddDevices(
        INetwork network,
        ITrackingCotaskConstructor trackingCotaskConstructor, // CORRECT TYPE
        Antilatency.Alt.Environment.IEnvironment environment,
        bool isBlue)
    {
        var nodes = trackingCotaskConstructor.findSupportedNodes(network);

        if (nodes.Length > 0)
        {
            Console.WriteLine($"Found {nodes.Length} supported nodes");
        }

        foreach (var node in nodes)
        {
            try
            {
                // Get parent node serial number
                var parentNode = network.nodeGetParent(node);
                var serialNo = network.nodeGetStringProperty(parentNode,
                    Antilatency.DeviceNetwork.Interop.Constants.HardwareSerialNumberKey);

                if (string.IsNullOrEmpty(serialNo))
                {
                    Console.WriteLine("Warning: Empty serial number for node");
                    continue;
                }

                var senderId = Convert.ToUInt64(serialNo, 16);

                // Skip if already connected
                if (_altDevices.ContainsKey(senderId))
                {
                    // Update environment if needed
                    if (_altDevices[senderId].IsBlueEnvironment != isBlue)
                    {
                        _altDevices[senderId].Cotask?.Dispose();
                        _altDevices.TryRemove(senderId, out _);
                    }
                    else
                    {
                        continue;
                    }
                }

                // Start tracking task
                var trackingCotask = trackingCotaskConstructor.startTask(network, node, environment);

                var altData = new AltData(senderId, SubscriptionType.None, trackingCotask, node, isBlue);
                _altDevices[senderId] = altData;

                Console.WriteLine($"Connected device: {senderId:X} ({(isBlue ? "BLUE" : "GREEN")})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to connect device: {ex.Message}");
            }
        }
    }

    private static void ReconnectAllDevices(
        INetwork network,
        ITrackingCotaskConstructor trackingCotaskConstructor, // CORRECT TYPE
        Antilatency.Alt.Environment.IEnvironment environment,
        bool isBlue)
    {
        // Dispose all current cotasks
        foreach (var device in _altDevices.Values)
        {
            device.Cotask?.Dispose();
        }
        _altDevices.Clear();

        // Rediscover devices with new environment
        DiscoverAndAddDevices(network, trackingCotaskConstructor, environment, isBlue);
    }

    private static void ProcessTrackingData(bool isBlue)
    {
        var environmentText = isBlue ? "BLUE" : "GREEN";
        var devicesToRemove = new List<ulong>();

        foreach (var altData in _altDevices.Values)
        {
            try
            {
                // Check if cotask is finished
                if (altData.Cotask.IsFinished())
                {
                    Console.WriteLine($"Device {altData.Id:X} task finished, removing...");
                    devicesToRemove.Add(altData.Id);
                    continue;
                }

                // Get tracking state
                if (!altData.Cotask.GetState(out var trackingState, 0.03f))
                {
                    continue;
                }

                // Validate tracking data
                if (trackingState.stability.stage.value != Antilatency.Alt.Tracking.Stage.Tracking6Dof)
                {
                    if (_altDevices.Count <= 2) // Only show status for few devices to avoid spam
                    {
                        Console.WriteLine($"Device {altData.Id:X}: {trackingState.stability.stage.value} (waiting for 6DoF)");
                    }
                    continue;
                }

                var position = trackingState.pose.position;

                // Skip zero positions
                if (position.x == 0 && position.y == 0 && position.z == 0)
                    continue;

                // Write data to file
                var dataLine = $"{DateTime.Now:HH:mm:ss.fff} {altData.Id:X} {environmentText} {position.x:F6} {position.y:F6} {position.z:F6}";
                _writer.WriteLine(dataLine);

                // Display in console (limit output for multiple devices)
                if (_altDevices.Count <= 2)
                {
                    Console.WriteLine($"📍 Device {altData.Id:X} {environmentText}: ({position.x:F3}, {position.y:F3}, {position.z:F3})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing device {altData.Id:X}: {ex.Message}");
                devicesToRemove.Add(altData.Id);
            }
        }

        // Remove failed devices
        foreach (var deviceId in devicesToRemove)
        {
            if (_altDevices.TryRemove(deviceId, out var altData))
            {
                altData.Cotask?.Dispose();
                Console.WriteLine($"Removed device: {deviceId:X}");
            }
        }
    }

    private static void PrintStatus()
    {
        Console.WriteLine($"=== Status ===");
        Console.WriteLine($"Connected devices: {_altDevices.Count}");
        Console.WriteLine($"Current environment: {(_useBlueEnvironment ? "BLUE" : "GREEN")}");
        Console.WriteLine($"Running: {_running}");
        Console.WriteLine($"Data file: nodeData.txt");
        Console.WriteLine("Commands: [Q]uit, [S]tatus, [T]oggle environment");

        foreach (var device in _altDevices.Values)
        {
            Console.WriteLine($"  Device {device.Id:X} ({(device.IsBlueEnvironment ? "BLUE" : "GREEN")})");
        }
    }
}