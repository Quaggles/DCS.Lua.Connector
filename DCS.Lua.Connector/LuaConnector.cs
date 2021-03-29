using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace DCS.Lua.Connector {
    public class LuaConnector : IDisposable {
        private readonly UdpClient udpClient;
        private readonly IPEndPoint targetEndpoint;
        private readonly IPEndPoint listenEndpoint;
        public TimeSpan Timeout;
        private int messageId = 0;
        private readonly CancellationTokenSource cts = new();

        public readonly Dictionary<int, MessageResponse> ResponseBuffer = new();
        public event EventHandler<MessageResponse> OnResponseReceived; 

        public LuaConnector(IPEndPoint targetEndpoint) {
            this.targetEndpoint = targetEndpoint;
            listenEndpoint = new IPEndPoint(IPAddress.Any, targetEndpoint.Port + 1);
            udpClient = new UdpClient(listenEndpoint);
            Timeout = new TimeSpan(500);
            Task.Run(() => ReceiveLoopAsync(cts.Token).ConfigureAwait(false));
        }
        
        public LuaConnector(IPAddress ipAddress, int port) : this(new IPEndPoint(ipAddress, port)) {
        }

        public LuaConnector(string ipAddress, int port) : this(new IPEndPoint(IPAddress.Parse(ipAddress), port)) {
        }

        readonly JsonSerializerOptions serializeOptions = new() {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Converters = {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            }
        };

        public enum MessageType {
            Ping,
            Command,
            LuaResult,
        }
        
        public enum LuaEnvironment {
            GUI,
            Mission,
            Export,
        }

        [Serializable]
        public struct MessageTransmit {
            public int Id { get; set; }
            public string Code { get; set; }
            public LuaEnvironment LuaEnv { get; set; }
            public MessageType Type { get; set; }
            
            public MessageTransmit(int id, MessageType type) {
                Id = id;
                Type = type;
                LuaEnv = LuaEnvironment.GUI;
                Code = "";
            }

            public MessageTransmit(int id, MessageType type, LuaEnvironment luaEnv, string code) {
                Id = id;
                Type = type;
                LuaEnv = luaEnv;
                Code = code;
            }
        }
        public enum ResponseStatus {
            Success,
            RuntimeError,
            SyntaxError,
            EncodeResponseError,
            DostringError,
        }
        
        [Serializable]
        public struct MessageResponse {
            public int Id { get; set; }
            public MessageType Type { get; set; }
            public ResponseStatus Status { get; set; }
            public string Result { get; set; }

            public override string ToString() {
                return $"Id: {Id}, Type: {Type}, Status: {Status}, Result: {Result}";
            }
        }
        
        /// <summary>
        /// Send a message to DCS
        /// </summary>
        /// <param name="messageTransmit"></param>
        /// <returns></returns>
        public async Task<int> SendAsync(MessageTransmit messageTransmit) {
            var json = JsonSerializer.Serialize(messageTransmit, serializeOptions);
            var packet = Encoding.UTF8.GetBytes(json);
            await udpClient.SendAsync(packet, packet.Length, targetEndpoint);
            return messageTransmit.Id;
        }
        
        /// <summary>
        /// Pings DCS to see if it's ready to start receiving commands
        /// </summary>
        /// <param name="timeout"></param>
        /// <returns></returns>
        public async Task<bool> PingAsync(TimeSpan timeout = default) {
            var message = new MessageTransmit(messageId++, MessageType.Ping);
            await SendAsync(message);
            try {
                var returnMessage = await GetResponseAsync(message.Id, timeout);
                return returnMessage.Type == MessageType.Ping && returnMessage.Status == ResponseStatus.Success && returnMessage.Result == "pong";
            } catch (TimeoutException) {
                return false;
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancellationToken = default) {
            while (!cancellationToken.IsCancellationRequested) {
                var messageString = "<Empty>";
                try {
                    cancellationToken.ThrowIfCancellationRequested();
                    var messageResponse = await udpClient.ReceiveAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    messageString = Encoding.UTF8.GetString(messageResponse.Buffer);
                    var message = JsonSerializer.Deserialize<MessageResponse>(messageString, serializeOptions);
                    ResponseBuffer.TryAdd(message.Id, message);
                    OnResponseReceived?.Invoke(this, message);
                } catch (SocketException ex) {
                    // This error happens when the target doesn't exist but that's why we have a timeout
                    if (ex.ErrorCode != 10054) throw;
                } catch (JsonException ex) {
                    Console.WriteLine($"Could not deserialize message: {messageString} Error: {ex}");
                } catch (ObjectDisposedException) {
                    if (!cancellationToken.IsCancellationRequested) throw;
                    // Ignore, we're closing
                } catch (Exception ex) {
                    Console.WriteLine($"Receive error: {ex}");
                }
            }
        }

        /// <summary>
        /// Waits for a response from DCS with a matching id
        /// </summary>
        /// <param name="id"></param>
        /// <param name="timeout"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="TimeoutException"></exception>
        private async Task<MessageResponse> GetResponseAsync(int id, TimeSpan timeout = default, CancellationToken cancellationToken = default) {
            if (timeout == default) timeout = Timeout;
            var tcs = new TaskCompletionSource<MessageResponse>();
            void OnOnResponseReceived(object sender, MessageResponse e) {
                if (e.Id == id) {
                    ResponseBuffer.Remove(e.Id);
                    tcs.SetResult(e);
                }
            }

            OnResponseReceived += OnOnResponseReceived;
            try {
                var firstReturn = await Task.WhenAny(tcs.Task, Task.Delay(timeout, cancellationToken));
                if (firstReturn != tcs.Task) throw new TimeoutException();
                return await tcs.Task;
            } finally {
                OnResponseReceived -= OnOnResponseReceived;
            }
        }
        
        /// <summary>
        /// Sends a message to DCS then waits for the response
        /// </summary>
        /// <param name="messageTransmit"></param>
        /// <returns></returns>
        /// <exception cref="InvalidDataException"></exception>
        public async Task<MessageResponse> SendReceiveAsync(MessageTransmit messageTransmit) {
            var response = await GetResponseAsync(await SendAsync(messageTransmit)).ConfigureAwait(false);
            if (messageTransmit.Type == MessageType.Command && response.Type != MessageType.LuaResult) throw new InvalidDataException($"Expected response of type {MessageType.LuaResult}");
            return response;
        }

        /// <summary>
        /// Sends a command to DCS then waits for the response
        /// </summary>
        /// <param name="command"></param>
        /// <param name="environment"></param>
        /// <returns></returns>
        public async Task<MessageResponse> SendReceiveCommandAsync(string command, LuaEnvironment environment = LuaEnvironment.GUI) => await SendReceiveAsync(new MessageTransmit(messageId++, MessageType.Command, environment, command));

        public void Dispose() {
            try {
                cts.Cancel();
                udpClient?.Dispose();
            } catch (ObjectDisposedException) {
            }
        }
    }
}