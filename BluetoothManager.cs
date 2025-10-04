using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Bluetooth;
using Java.Util;

namespace PenguinMonitor
{
    public class BluetoothManager : IDisposable
    {
        // Bluetooth components
        private BluetoothSocket? _bluetoothSocket;
        private Stream? _inputStream;
        private Stream? _outputStream;
        private bool _isConnected = false;
        private const string READER_BLUETOOTH_ADDRESS = "00:07:80:E6:95:52";
        private static string Serial_Port_Profile_UUID = "00001101-0000-1000-8000-00805F9B34FB"; 

        // Connection management
        private CancellationTokenSource? _connectionCancellation;
        private bool _isConnecting = false;
        private bool _shouldAutoReconnect = true;

        // Connection parameters
        private const int CONNECTION_TIMEOUT_MS = 10000; // 10 seconds timeout
        private const int INITIAL_RETRY_DELAY_MS = 2000; // Start with 2 seconds
        private const int MAX_RETRY_DELAY_MS = 30000; // Cap at 30 seconds
        private const double BACKOFF_MULTIPLIER = 1.5; // Gradual increase

        // Events for communicating with MainActivity
        public event Action<string>? StatusChanged;
        public event Action<string>? EidDataReceived;

        public bool IsConnected => _isConnected;
        public bool IsConnecting => _isConnecting;

        public async Task StartConnectionAsync()
        {
            if (_isConnecting || _isConnected)
            {
                return; // Already connecting or connected
            }

            _shouldAutoReconnect = true;
            _connectionCancellation = new CancellationTokenSource();
            
            await ConnectToReaderBluetoothAsync();
        }

        private async Task ConnectToReaderBluetoothAsync()
        {
            _isConnecting = true;
            var attempt = 0;
            var currentRetryDelay = INITIAL_RETRY_DELAY_MS;

            try
            {
                var bluetoothAdapter = BluetoothAdapter.DefaultAdapter;
                if (bluetoothAdapter?.IsEnabled != true)
                {
                    StatusChanged?.Invoke("❌ Bluetooth not available");
                    return;
                }

                // Keep trying until connected or cancelled
                while (!_isConnected && !_connectionCancellation?.Token.IsCancellationRequested == true)
                {
                    attempt++;
                    
                    try
                    {
                        if (attempt > 1)
                        {
                            StatusChanged?.Invoke($"🔄 Retry {attempt} (waiting {currentRetryDelay/1000}s)...");
                            await Task.Delay(currentRetryDelay, _connectionCancellation.Token);
                        }
                        else
                        {
                            StatusChanged?.Invoke("🔗 Connecting to HR5...");
                        }

                        var device = bluetoothAdapter.GetRemoteDevice(READER_BLUETOOTH_ADDRESS);
                        if (device == null)
                        {
                            throw new Exception("HR5 device not found");
                        }

                        // Check if device is paired
                        if (device.BondState != Bond.Bonded)
                        {
                            StatusChanged?.Invoke("⚠️ HR5 not paired - check Android Bluetooth settings");
                            await Task.Delay(5000, _connectionCancellation.Token);
                            continue;
                        }

                        StatusChanged?.Invoke($"🔗 Connecting to {device.Name ?? "HR5"} (attempt {attempt})...");

                        var uuid = UUID.FromString(Serial_Port_Profile_UUID);
                        
                        // Clean up any existing socket first
                        CleanupConnection();
                        
                        _bluetoothSocket = device.CreateRfcommSocketToServiceRecord(uuid);

                        if (_bluetoothSocket != null)
                        {
                            // TIMEOUT HANDLING: Use Task.WhenAny for connection timeout
                            var connectTask = Task.Run(() => _bluetoothSocket.Connect(), _connectionCancellation.Token);
                            var timeoutTask = Task.Delay(CONNECTION_TIMEOUT_MS, _connectionCancellation.Token);

                            var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                            if (completedTask == timeoutTask)
                            {
                                throw new TimeoutException($"Connection timeout after {CONNECTION_TIMEOUT_MS/1000} seconds");
                            }

                            await connectTask; // Re-await to get any exceptions

                            if (_bluetoothSocket.IsConnected)
                            {
                                _inputStream = _bluetoothSocket.InputStream;
                                _isConnected = true;

                                StatusChanged?.Invoke($"✅ HR5 Connected after {attempt} attempt{(attempt > 1 ? "s" : "")} - Ready to scan");
                                
                                // Reset retry delay on successful connection
                                currentRetryDelay = INITIAL_RETRY_DELAY_MS;
                                
                                // Start listening in background
                                _ = Task.Run(async () => await ListenForEidDataAsync(), _connectionCancellation.Token);
                                return; // Success!
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break; // User cancelled
                    }
                    catch (Exception ex)
                    {
                        StatusChanged?.Invoke($"⚠️ Attempt {attempt} failed: {ex.Message}");
                        
                        // Clean up failed connection
                        CleanupConnection();
                        
                        // RETRY MECHANISM: Increase delay for next attempt (exponential backoff)
                        currentRetryDelay = Math.Min(
                            (int)(currentRetryDelay * BACKOFF_MULTIPLIER), 
                            MAX_RETRY_DELAY_MS
                        );
                        
                        // Reset backoff periodically to avoid getting stuck at max delay
                        if (attempt % 10 == 0)
                        {
                            currentRetryDelay = INITIAL_RETRY_DELAY_MS;
                            StatusChanged?.Invoke($"🔄 Reset retry timing after {attempt} attempts");
                        }
                    }
                }

                if (!_isConnected && _connectionCancellation?.Token.IsCancellationRequested == true)
                {
                    StatusChanged?.Invoke("🚫 Connection cancelled");
                }
            }
            catch (OperationCanceledException)
            {
                StatusChanged?.Invoke("🚫 Connection cancelled");
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private async Task ListenForEidDataAsync()
        {
            var buffer = new byte[1024];
            var receivedData = new StringBuilder();

            try
            {
                while (_isConnected && _bluetoothSocket?.IsConnected == true && _inputStream != null)
                {
                    if (_connectionCancellation?.Token.IsCancellationRequested == true)
                        break;

                    try
                    {
                        var bytesRead = await _inputStream.ReadAsync(buffer, 0, buffer.Length, _connectionCancellation?.Token ?? CancellationToken.None);

                        if (bytesRead > 0)
                        {
                            var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                            receivedData.Append(data);

                            var completeData = receivedData.ToString();

                            if (completeData.Length >= 10)
                            {
                                var cleanData = new string(completeData.Where(c => char.IsLetterOrDigit(c)).ToArray());
                                if (cleanData.Length >= 10)
                                {
                                    EidDataReceived?.Invoke(cleanData);
                                    receivedData.Clear();
                                }
                            }

                            if (receivedData.Length > 1000)
                            {
                                receivedData.Clear();
                            }
                        }
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        StatusChanged?.Invoke($"⚠️ Scanning error: {ex.Message}");
                        break; // Exit listening loop on error
                    }

                    await Task.Delay(100, _connectionCancellation?.Token ?? CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"❌ Scanning error: {ex.Message}");
            }
            finally
            {
                // AUTOMATIC RECONNECTION: Connection dropped, try to reconnect
                if (_isConnected)
                {
                    _isConnected = false;
                    StatusChanged?.Invoke("🔌 HR5 Disconnected - Attempting reconnection...");
                    
                    // Auto-reconnect after disconnect (unless manually cancelled)
                    if (_shouldAutoReconnect && !_connectionCancellation?.Token.IsCancellationRequested == true)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(3000); // Brief pause before auto-reconnect
                            if (_shouldAutoReconnect && !_connectionCancellation?.Token.IsCancellationRequested == true)
                            {
                                await ConnectToReaderBluetoothAsync();
                            }
                        });
                    }
                }
            }
        }

        // RECOVERY LOGIC: Clean up resources properly
        private void CleanupConnection()
        {
            try
            {
                _isConnected = false;
                _bluetoothSocket?.Close();
                _inputStream?.Dispose();
                _outputStream?.Dispose();
                _bluetoothSocket?.Dispose();
                _bluetoothSocket = null;
                _inputStream = null;
                _outputStream = null;
            }
            catch (Exception)
            {
                // Ignore cleanup errors
            }
        }

        public void Disconnect()
        {
            _shouldAutoReconnect = false; // Disable auto-reconnect
            _connectionCancellation?.Cancel();
            CleanupConnection();
        }

        public void Dispose()
        {
            Disconnect();
            _connectionCancellation?.Dispose();
        }

        // Manual retry method (resets backoff timing)
        public async Task RetryConnectionAsync()
        {
            if (_isConnecting) return;
            
            Disconnect();
            await Task.Delay(1000); // Brief pause before retry
            await StartConnectionAsync();
        }
    }
}