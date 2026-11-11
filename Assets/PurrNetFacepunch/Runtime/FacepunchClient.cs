#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

#if STEAMWORKS_NET
#define STEAMWORKS_NET_PACKAGE
#endif

#if PURRNET_FACEPUNCH && !STEAMWORKS_NET_PACKAGE && !DISABLESTEAMWORKS
using System;
using System.Collections;
using System.Runtime.InteropServices;
using PurrNet.Logging;
using PurrNet.Transports;
using Steamworks;
using Steamworks.Data;
using FacepunchConnectionState = Steamworks.ConnectionState;
using PurrConnectionState = PurrNet.Transports.ConnectionState;

namespace PurrNet.Steam
{
    /// <summary>
    /// SteamClient for projects that use Facepunch.Steamworks instead of Steamworks.NET.
    /// </summary>
    internal sealed class FacepunchClient : IConnectionManager
    {
        const int MAX_MESSAGES = 256;

        static byte[] buffer = new byte[1024];

        private ConnectionManager _connection;
        private SteamSendQueue _sendQueue;
        private bool _sendLimitWarningLogged;
        private bool _pendingDisconnect;

        public event Action<ByteData> onDataReceived;
        public event Action<PurrConnectionState> onConnectionState;

        public DisconnectReason disconnectReason { get; private set; } = DisconnectReason.ClientRequest;

        private PurrConnectionState _state = PurrConnectionState.Disconnected;

        public PurrConnectionState connectionState
        {
            get => _state;
            set
            {
                if (_state == value)
                    return;

                _state = value;
                onConnectionState?.Invoke(_state);
            }
        }

        public int GetRoundTripTime()
        {
            if (_connection == null || connectionState != PurrConnectionState.Connected)
                return -1;

            return _connection.Connection.QuickStatus().Ping;
        }

        public SteamSendStatistics sendStatistics => _sendQueue?.statistics ?? default;

        // Facepunch.Steamworks picks the client or game server interface itself, so dedicated changes nothing here.
        internal IEnumerator Connect(string address, ushort port, bool dedicated, SteamSendSettings sendSettings)
        {
            yield return null;
            Open(() => SteamNetworkingSockets.ConnectNormal(NetAddress.From(address, port), this), sendSettings);
        }

        internal IEnumerator ConnectP2P(string steamId, bool dedicated, SteamSendSettings sendSettings)
        {
            yield return null;
            if (ulong.TryParse(steamId, out var id) == false)
            {
                if (steamId is "localhost" or "127.0.0.1")
                {
                    id = Steamworks.SteamClient.SteamId;
                }
                else
                {
                    PurrLogger.LogError("Invalid Steam ID provided as address to connect");
                    yield break;
                }
            }

            Open(() => SteamNetworkingSockets.ConnectRelay(id, 0, this), sendSettings);
        }

        private void Open(Func<ConnectionManager> connect, SteamSendSettings sendSettings)
        {
            disconnectReason = DisconnectReason.ClientRequest;
            _pendingDisconnect = false;
            _sendLimitWarningLogged = false;
            _sendQueue = null;

            try
            {
                FacepunchConnectionStatus.Apply(sendSettings);
                _connection = connect();
            }
            catch (Exception e)
            {
                // Facepunch.Steamworks throws when Steam is not initialized or hands back an invalid connection.
                _connection = null;
                connectionState = PurrConnectionState.Disconnecting;
                connectionState = PurrConnectionState.Disconnected;
                PurrLogger.LogError($"Failed to connect to host: {e.Message}");
                return;
            }

            connectionState = PurrConnectionState.Connecting;
        }

        void IConnectionManager.OnConnecting(ConnectionInfo info)
        {
            connectionState = PurrConnectionState.Connecting;
        }

        void IConnectionManager.OnConnected(ConnectionInfo info)
        {
            connectionState = PurrConnectionState.Connected;
        }

        void IConnectionManager.OnDisconnected(ConnectionInfo info)
        {
            Disconnect(why: info.State == FacepunchConnectionState.ClosedByPeer
                ? DisconnectReason.ServerRequest
                : DisconnectReason.Timeout);
        }

        void IConnectionManager.OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
        {
            if (buffer.Length < size)
                Array.Resize(ref buffer, size);
            Marshal.Copy(data, buffer, 0, size);
            onDataReceived?.Invoke(new ByteData(buffer, 0, size));
        }

        internal bool TrySend(ByteData data, Channel channel)
        {
            if (_connection == null)
                return false;

            string failure;
            try
            {
                var connection = _connection.Connection;
                _sendQueue ??= new SteamSendQueue(
                    (ptr, length, flags) => connection.SendMessage(ptr, (int)length, (SendType)flags),
                    () => UnityEngine.Time.realtimeSinceStartupAsDouble);
                bool healthy = _sendQueue.Send(data, channel, out var accepted);
                if (!_sendLimitWarningLogged && _sendQueue.statistics.lastSendResult == (int)Result.LimitExceeded)
                {
                    _sendLimitWarningLogged = true;
                    try
                    {
                        PurrLogger.LogWarning($"Steam send buffer limit reached for client connection {connection}. " +
                            "Reliable messages retry in order; unreliable sends may be refused. " +
                            "Check outgoing traffic and configured Steam send limits. This warning is logged once per connection.");
                    }
                    catch { }
                }
                if (healthy)
                    return accepted;
                failure = _sendQueue.failureReason;
            }
            catch (Exception e)
            {
                failure = $"Steam send failed: {e.Message}";
            }
            FailSend(failure);
            return false;
        }

        public void SendMessages()
        {
            if (_connection == null)
                return;
            string failure;
            try
            {
                if (_sendQueue != null && !_sendQueue.Pump())
                {
                    failure = _sendQueue.failureReason;
                }
                else
                {
                    _connection.Connection.Flush();
                    return;
                }
            }
            catch (Exception e)
            {
                failure = $"Steam send failed: {e.Message}";
            }
            FailSend(failure);
        }

        public void ReceiveMessages()
        {
            if (_pendingDisconnect)
                CompleteDisconnect();
            _connection?.Receive(MAX_MESSAGES);
        }

        private void FailSend(string reason)
        {
            var status = FacepunchConnectionStatus.Describe(_connection.Connection);
            if (_sendQueue != null && _sendQueue.peerClosed)
                PurrLogger.Log($"Disconnecting Steam client: {reason} ({status})");
            else
                PurrLogger.LogError($"Disconnecting Steam client: {reason} ({status})");
            // A failed send does not say who closed the connection.
            Disconnect(reason, true, DisconnectReason.Timeout);
        }

        void Disconnect(string reason = null, bool deferNotification = false,
            DisconnectReason why = DisconnectReason.ClientRequest)
        {
            _sendQueue?.Clear();
            if (_connection != null)
            {
                // Only the call that closes the live handle decides the reason; a later Stop() keeps it.
                disconnectReason = why;
                var connection = _connection;
                _connection = null;
                // Late callbacks and messages for the closed connection are dropped.
                connection.Interface = null;
                try
                {
                    FacepunchConnectionStatus.Close(connection.Connection, reason);
                }
                catch
                {
                    // ignored
                }

                _pendingDisconnect = true;
            }
            // Defer callbacks until receive/update so send callers can finish iterating players.
            if (_pendingDisconnect && !deferNotification)
                CompleteDisconnect();
        }

        private void CompleteDisconnect()
        {
            _pendingDisconnect = false;
            if (connectionState != PurrConnectionState.Disconnected)
                connectionState = PurrConnectionState.Disconnecting;
            connectionState = PurrConnectionState.Disconnected;
        }

        public void Stop()
        {
            Disconnect();
        }
    }
}
#endif
