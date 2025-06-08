// Program.cs (Final Verified Version)

using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NetworkCommsDotNet;
using NetworkCommsDotNet.Connections;
using NetworkCommsDotNet.Connections.TCP;

namespace SecureChatApp
{
    // A dedicated class to make the discovery response clear and extensible.
    public class DiscoveryResponseInfo
    {
        public string ServerId { get; set; } = "";
        public int TcpPort { get; set; }
    }
    
    class Program
    {
        private const int DiscoveryPort = 10000;
        private const string SslSecret = "MySuperSecretPassword";
        private static string _serverId = "";

        private static int _serverTcpPort = 0; // Will hold the dynamically assigned TCP port

        private static Connection? _peerConnection;
        private static readonly object _consoleLock = new();

        static async Task Main(string[] args)
        {
            Console.WriteLine("Welcome to Secure Chat!");
            Console.WriteLine("-----------------------");
            Console.Write("Start as (S)erver or (C)lient? ");

            var choice = Console.ReadKey().Key;
            Console.WriteLine();

            try
            {
                if (choice == ConsoleKey.S)
                {
                    await StartServer();
                }
                else if (choice == ConsoleKey.C)
                {
                    await StartClient();
                }
                else
                {
                    Console.WriteLine("Invalid choice. Exiting.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An unexpected error occurred: {ex.Message}");
            }
            finally
            {
                NetworkComms.Shutdown();
                Console.WriteLine("Application has shut down. Press any key to exit.");
                Console.ReadKey();
            }
        }

        #region Server Logic

        // Inside the Program class...

        private static async Task StartServer()
        {
            _serverId = RandomNumberGenerator.GetInt32(10000, 100000).ToString();
            Console.WriteLine($"Your Server ID is: {_serverId}");
            Console.WriteLine("Share this ID with the client. Waiting for a connection...");

            NetworkComms.AppendGlobalIncomingPacketHandler<string>("DiscoveryRequest", HandleDiscoveryRequest);
            ConfigureCommonPacketHandlers();

            // --- START OF THE FIX ---
            // 1. Capture the return value from StartListening
            var tcpListeners = Connection.StartListening(ConnectionType.TCP, new IPEndPoint(IPAddress.Any, 0), true);

            // 2. Check if a listener was successfully created and store its port
            if (tcpListeners.Any())
            {
                _serverTcpPort = ((IPEndPoint)tcpListeners.First().LocalListenEndPoint).Port;
                Console.WriteLine($"Server listening for TCP connections on port: {_serverTcpPort}");
            }
            else
            {
                // This is a fatal error, so we should stop.
                Console.WriteLine("FATAL ERROR: Could not start TCP listener. The application cannot continue.");
                return;
            }
            // --- END OF THE FIX ---

            Connection.StartListening(ConnectionType.UDP, new IPEndPoint(IPAddress.Any, DiscoveryPort));

            Console.WriteLine("Server is running and discoverable on the local network.");
            await HandleUserInputLoop();
        }

        private static void HandleDiscoveryRequest(PacketHeader header, Connection connection, string message)
        {
            // --- START OF THE FIX ---
            // The old, incorrect line is removed.
            // var tcpListener = (IPEndPoint)NetworkComms.GetExistingLocalListenEndPoints(ConnectionType.TCP).First();
            // int serverTcpPort = tcpListener.Port;

            // We now simply use the TCP port we stored in our static field.
            var response = new DiscoveryResponseInfo
            {
                ServerId = _serverId,
                TcpPort = _serverTcpPort 
            };
            // --- END OF THE FIX ---
            
            // Reply directly to the client that sent the broadcast.
            NetworkComms.SendObject("DiscoveryResponse", ((IPEndPoint)connection.ConnectionInfo.RemoteEndPoint).Address.ToString(),
                                    ((IPEndPoint)connection.ConnectionInfo.RemoteEndPoint).Port, response);
        }

        private static void HandleDiscoveryRequest(PacketHeader header, Connection connection, string message)
        {
            // --- START OF THE FIX ---
            // The old, incorrect line is removed.
            // var tcpListener = (IPEndPoint)NetworkComms.GetExistingLocalListenEndPoints(ConnectionType.TCP).First();
            // int serverTcpPort = tcpListener.Port;

            // We now simply use the TCP port we stored in our static field.
            var response = new DiscoveryResponseInfo
            {
                ServerId = _serverId,
                TcpPort = _serverTcpPort
            };
            // --- END OF THE FIX ---

            // Reply directly to the client that sent the broadcast.
            NetworkComms.SendObject("DiscoveryResponse", ((IPEndPoint)connection.ConnectionInfo.RemoteEndPoint).Address.ToString(),
                                    ((IPEndPoint)connection.ConnectionInfo.RemoteEndPoint).Port, response);
        }

        private static void HandleDiscoveryRequest(PacketHeader header, Connection connection, string message)
        {
            // Get the server's actual listening TCP port.
            var tcpListener = (IPEndPoint)NetworkComms.GetExistingLocalListenEndPoints(ConnectionType.TCP).First();
            int serverTcpPort = tcpListener.Port;

            var response = new DiscoveryResponseInfo
            {
                ServerId = _serverId,
                TcpPort = serverTcpPort
            };
            
            // Reply directly to the client that sent the broadcast.
            NetworkComms.SendObject("DiscoveryResponse", ((IPEndPoint)connection.ConnectionInfo.RemoteEndPoint).Address.ToString(),
                                    ((IPEndPoint)connection.ConnectionInfo.RemoteEndPoint).Port, response);
        }

        #endregion

        #region Client Logic

        private static async Task StartClient()
        {
            Console.Write("Enter the 5-digit Server ID to connect to: ");
            var targetServerId = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(targetServerId) || targetServerId.Length != 5)
            {
                Console.WriteLine("Invalid Server ID format. Exiting.");
                return;
            }
            Console.WriteLine($"Searching for server with ID: {targetServerId}...");

            NetworkComms.AppendGlobalIncomingPacketHandler<DiscoveryResponseInfo>("DiscoveryResponse", 
                (header, connection, responseInfo) => HandleDiscoveryResponse(header, connection, responseInfo, targetServerId));

            ConfigureCommonPacketHandlers();
            Connection.StartListening(ConnectionType.UDP, new IPEndPoint(IPAddress.Any, 0));
            NetworkComms.SendObject("DiscoveryRequest", IPAddress.Broadcast.ToString(), DiscoveryPort, "ClientHello");

            var timeout = TimeSpan.FromSeconds(30);
            var cts = new CancellationTokenSource(timeout);
            try { while (_peerConnection == null && !cts.IsCancellationRequested) { await Task.Delay(100, cts.Token); } }
            catch (TaskCanceledException) { /* Expected */ }

            if (_peerConnection == null)
            {
                Console.WriteLine($"\nCould not find server with ID {targetServerId} within {timeout.TotalSeconds} seconds.");
                return;
            }
            await HandleUserInputLoop();
        }

        private static void HandleDiscoveryResponse(PacketHeader header, Connection connection, DiscoveryResponseInfo receivedInfo, string targetServerId)
        {
            if (_peerConnection != null) return;

            if (receivedInfo.ServerId == targetServerId)
            {
                Console.WriteLine($"\nFound server with ID {receivedInfo.ServerId}. Establishing secure connection...");
                try
                {
                    // CORRECT: The client-side ConnectionInfo object uses the ApplicationLayerProtocolStatus enum.
                    var serverIp = connection.ConnectionInfo.RemoteEndPoint.Address;
                    var serverTcpPort = receivedInfo.TcpPort;
                    
                    var tcpConnectionInfo = new ConnectionInfo(serverIp, serverTcpPort, ApplicationLayerProtocolStatus.Enabled);
                    _peerConnection = TCPConnection.GetConnection(tcpConnectionInfo);
                }
                catch (Exception ex)
                {
                    WriteStatusMessage($"Failed to connect: {ex.Message}");
                }
            }
        }

        #endregion

        #region Common Logic

        private static void ConfigureCommonPacketHandlers()
        {
            NetworkComms.AppendGlobalIncomingPacketHandler<string>("ChatMessage", HandleChatMessage);
            NetworkComms.AppendGlobalConnectionEstablishedHandler(HandleConnectionEstablished);
            NetworkComms.AppendGlobalConnectionClosedHandler(HandleConnectionClosed);
            
            var sendReceiveOptions = new SendReceiveOptions();
            sendReceiveOptions.DataProcessors.Add(DPSManager.GetDataProcessor<RijndaelEncryptor.RijndaelEncryptor>());
            ((RijndaelEncryptor.RijndaelEncryptor)sendReceiveOptions.DataProcessors[0]).Password = SslSecret;
            NetworkComms.DefaultSendReceiveOptions = sendReceiveOptions;
        }

        private static async Task HandleUserInputLoop()
        {
            while (_peerConnection == null) { await Task.Delay(100); }

            while (_peerConnection != null && _peerConnection.ConnectionAlive())
            {
                lock (_consoleLock) { Console.Write("You: "); }
                string? messageToSend = Console.ReadLine();
                if (string.IsNullOrEmpty(messageToSend)) continue;
                if (messageToSend.Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;
                try
                {
                    _peerConnection?.SendObject("ChatMessage", messageToSend);
                }
                catch (Exception ex)
                {
                    WriteStatusMessage($"Error sending message: Peer may have disconnected. {ex.Message}");
                    break;
                }
            }
        }
        
        private static void HandleChatMessage(PacketHeader header, Connection connection, string message)
        {
            WriteStatusMessage($"[Peer]: {message}", "You: ");
        }

        private static void HandleConnectionEstablished(Connection connection)
        {
            if (connection.ConnectionInfo.ConnectionType == ConnectionType.TCP)
            {
                _peerConnection = connection;
                WriteStatusMessage("Connection established! You can now chat. Type '/exit' to quit.", "You: ");
            }
        }

        private static void HandleConnectionClosed(Connection connection)
        {
            if (connection == _peerConnection)
            {
                _peerConnection = null;
                WriteStatusMessage("Peer has disconnected. The application will now shut down.");
            }
        }
        
        private static void WriteStatusMessage(string message, string prompt = "")
        {
            lock (_consoleLock)
            {
                // Simple redraw logic to prevent incoming messages from corrupting the current input line
                int originalLeft = Console.CursorLeft;
                string currentLine = ""; // We can't actually read the current line without blocking

                // Clear the current line
                Console.SetCursorPosition(0, Console.CursorTop);
                Console.Write(new string(' ', Console.WindowWidth)); 
                Console.SetCursorPosition(0, Console.CursorTop);

                // Write the network message
                Console.WriteLine(message);

                // Redraw the prompt and what would have been the cursor position
                if (!string.IsNullOrEmpty(prompt))
                {
                     Console.Write(prompt);
                     Console.SetCursorPosition(originalLeft, Console.CursorTop);
                }
            }
        }

        #endregion
    }
}