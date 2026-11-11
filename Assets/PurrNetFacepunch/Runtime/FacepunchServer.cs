#if !(UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define DISABLESTEAMWORKS
#endif

#if STEAMWORKS_NET
#define STEAMWORKS_NET_PACKAGE
#endif

#if PURRNET_FACEPUNCH && !STEAMWORKS_NET_PACKAGE && !DISABLESTEAMWORKS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PurrNet.Logging;
using PurrNet.Transports;
using Steamworks;
using Steamworks.Data;
using FacepunchConnection = Steamworks.Data.Connection;
using FacepunchConnectionState = Steamworks.ConnectionState;

namespace PurrNet.Steam
{
    /// <summary>
    /// SteamServer for projects that use Facepunch.Steamworks instead of Steamworks.NET.
    /// </summary>
    internal sealed class FacepunchServer : ISocketManager
    {
        const int MAX_MESSAGES = 256;

        static byte[] buffer = new byte[1024];

        private SocketManager _socket;

        private readonly List<FacepunchConnection> _connections = new List<FacepunchConnection>();
        private readonly Dictionary<int, FacepunchConnection> _connectionById =
            new Dictionary<int, FacepunchConnection>();
        private readonly Dictionary<FacepunchConnection, int> _idByConnection =
            new Dictionary<FacepunchConnection, int>();
        private readonly Dictionary<int, ulong> _steamIdByConnection = new Dictionary<int, ulong>();
        private readonly Dictionary<FacepunchConnection, SteamSendQueue> _sendQueues =
            new Dictionary<FacepunchConnection, SteamSendQueue>();
        private readonly List<FacepunchConnection> _flushConnections = new List<FacepunchConnection>();
        private readonly Queue<(int id, DisconnectReason reason)> _pendingDisconnects =
            new Queue<(int id, DisconnectReason reason)>();
        private readonly HashSet<FacepunchConnection> _sendLimitWarnings = new HashSet<FacepunchConnection>();

        private Exception _receiveFailure;
        private int _nextConnectionId;

        public event Action<int> onRemoteConnected;
        public event Action<int, DisconnectReason> onRemoteDisconnectedWithReason;
        public event Action<int, ByteData> onDataReceived;

        public bool listening => _socket != null;

        // Facepunch.Steamworks picks the client or game server interface itself, so dedicated changes nothing here.
        internal void Listen(ushort port, bool dedicated, SteamSendSettings sendSettings)
        {
            Open(() => SteamNetworkingSockets.CreateNormalSocket(NetAddress.AnyIp(port), this), sendSettings);
        }

        internal void ListenP2P(bool dedicated, SteamSendSettings sendSettings)
        {
            Open(() => SteamNetworkingSockets.CreateRelaySocket(0, this), sendSettings);
        }

        private void Open(Func<SocketManager> create, SteamSendSettings sendSettings)
        {
            try
            {
                FacepunchConnectionStatus.Apply(sendSettings);
                _socket = create();
            }
            catch (Exception e)
            {
                // Facepunch.Steamworks throws when Steam is not initialized or hands back an invalid socket.
                _socket = null;
                PurrLogger.LogError($"Failed to create listen socket: {e.Message}");
            }
        }

        void ISocketManager.OnConnecting(FacepunchConnection connection, ConnectionInfo info)
        {
            if (_socket == null)
                return;

            var res = connection.Accept();
            if (res != Result.OK)
                PurrLogger.LogError($"Failed to accept connection: {res}");
        }

        void ISocketManager.OnConnected(FacepunchConnection connection, ConnectionInfo info)
        {
            if (_socket == null)
                return;

            int id = _nextConnectionId++;
            _connections.Add(connection);
            _connectionById.Add(id, connection);
            _idByConnection.Add(connection, id);
            _steamIdByConnection.Add(id, info.Identity.SteamId);

            onRemoteConnected?.Invoke(id);
        }

        void ISocketManager.OnDisconnected(FacepunchConnection connection, ConnectionInfo info)
        {
            if (_socket == null)
                return;

            CloseConnection(connection, why: info.State == FacepunchConnectionState.ClosedByPeer
                ? DisconnectReason.ClientRequest
                : DisconnectReason.Timeout);
        }

        void ISocketManager.OnMessage(FacepunchConnection connection, NetIdentity identity, IntPtr data, int size,
            long messageNum, long recvTime, int channel)
        {
            // Facepunch.Steamworks only releases every message of a batch when no handler throws.
            if (_receiveFailure != null || !_idByConnection.TryGetValue(connection, out var connId))
                return;

            try
            {
                if (buffer.Length < size)
                    Array.Resize(ref buffer, size);
                Marshal.Copy(data, buffer, 0, size);
                onDataReceived?.Invoke(connId, new ByteData(buffer, 0, size));
            }
            catch (Exception e)
            {
                _receiveFailure = e;
            }
        }

        public void SendMessages()
        {
            // Pumping a failed queue removes its connection from the live list.
            _flushConnections.Clear();
            _flushConnections.AddRange(_connections);
            for (var i = 0; i < _flushConnections.Count; i++)
            {
                var conn = _flushConnections[i];
                if (_idByConnection.ContainsKey(conn))
                    FlushNativeConnection(conn);
            }
        }

        public void ReceiveMessages()
        {
            while (_pendingDisconnects.Count > 0)
            {
                var disconnected = _pendingDisconnects.Dequeue();
                NotifyDisconnected(disconnected.id, disconnected.reason);
            }

            if (_socket == null)
                return;

            _socket.Receive(MAX_MESSAGES);

            if (_receiveFailure != null)
            {
                var failure = _receiveFailure;
                _receiveFailure = null;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        public void Kick(int id)
        {
            if (!_connectionById.TryGetValue(id, out var conn))
                return;

            CloseConnection(conn, null, true, DisconnectReason.ServerRequest);
        }

        public int GetRoundTripTime(int connectionId)
        {
            if (!_connectionById.TryGetValue(connectionId, out var conn))
                return -1;

            return conn.QuickStatus().Ping;
        }

        public ulong GetSteamID(int connectionId)
        {
            return _steamIdByConnection.TryGetValue(connectionId, out var steamID) ? steamID : 0;
        }

        public SteamSendStatistics GetSendStatistics(int connectionId)
        {
            if (_connectionById.TryGetValue(connectionId, out var conn) &&
                _sendQueues.TryGetValue(conn, out var queue))
                return queue.statistics;
            return default;
        }

        public void FlushConnection(int connId)
        {
            if (!_connectionById.TryGetValue(connId, out var conn))
                return;

            FlushNativeConnection(conn);
        }

        internal bool TrySendToConnection(int connId, ByteData data, Channel channel)
        {
            if (!_connectionById.TryGetValue(connId, out var conn))
                return false;

            string failure;
            try
            {
                if (!_sendQueues.TryGetValue(conn, out var queue))
                {
                    queue = new SteamSendQueue(
                        (ptr, length, flags) => conn.SendMessage(ptr, (int)length, (SendType)flags),
                        () => UnityEngine.Time.realtimeSinceStartupAsDouble);
                    _sendQueues.Add(conn, queue);
                }
                bool healthy = queue.Send(data, channel, out var accepted);
                if (queue.statistics.lastSendResult == (int)Result.LimitExceeded && _sendLimitWarnings.Add(conn))
                {
                    try
                    {
                        PurrLogger.LogWarning($"Steam send buffer limit reached for server connection {connId} ({conn}). " +
                            "Reliable messages retry in order; unreliable sends may be refused. " +
                            "Check outgoing traffic and configured Steam send limits. This warning is logged once per connection.");
                    }
                    catch { }
                }
                if (healthy)
                    return accepted;
                failure = queue.failureReason;
            }
            catch (Exception e)
            {
                failure = $"Steam send failed: {e.Message}";
            }
            FailSend(conn, failure);
            return false;
        }

        private void FlushNativeConnection(FacepunchConnection conn)
        {
            string failure;
            try
            {
                if (_sendQueues.TryGetValue(conn, out var queue) && !queue.Pump())
                {
                    failure = queue.failureReason;
                }
                else
                {
                    conn.Flush();
                    return;
                }
            }
            catch (Exception e)
            {
                failure = $"Steam send failed: {e.Message}";
            }
            FailSend(conn, failure);
        }

        private void FailSend(FacepunchConnection conn, string reason)
        {
            var status = FacepunchConnectionStatus.Describe(conn);
            if (_sendQueues.TryGetValue(conn, out var queue) && queue.peerClosed)
                PurrLogger.Log($"Disconnecting Steam connection {conn}: {reason} ({status})");
            else
                PurrLogger.LogError($"Disconnecting Steam connection {conn}: {reason} ({status})");
            // A failed send does not say who closed the connection.
            CloseConnection(conn, reason, true, DisconnectReason.Timeout);
        }

        private void CloseConnection(FacepunchConnection conn, string reason = null, bool deferNotification = false,
            DisconnectReason why = DisconnectReason.ServerRequest)
        {
            try
            {
                FacepunchConnectionStatus.Close(conn, reason);
                _socket?.Connecting.Remove(conn);
                _socket?.Connected.Remove(conn);
            }
            catch
            {
                // The native subsystem may already be shutting down. Still release managed state.
            }
            finally
            {
                // Steam does not post a local state callback when we explicitly close a handle.
                RemoveConnection(conn, deferNotification, why);
            }
        }

        private void RemoveConnection(FacepunchConnection connection, bool deferNotification, DisconnectReason why)
        {
            _sendLimitWarnings.Remove(connection);
            if (_sendQueues.Remove(connection, out var queue))
                queue.Clear();
            if (_connections.Remove(connection) && _idByConnection.Remove(connection, out var _id))
            {
                _connectionById.Remove(_id);
                if (deferNotification)
                    _pendingDisconnects.Enqueue((_id, why));
                else
                    NotifyDisconnected(_id, why);
            }
        }

        private void NotifyDisconnected(int connectionId, DisconnectReason why)
        {
            try
            {
                // Disconnect handlers may still need the Steam identity for cleanup.
                onRemoteDisconnectedWithReason?.Invoke(connectionId, why);
            }
            finally
            {
                _steamIdByConnection.Remove(connectionId);
            }
        }

        public void Stop()
        {
            for (var o = 0; o < _connections.Count; o++)
            {
                try
                {
                    FacepunchConnectionStatus.Close(_connections[o], null);
                }
                catch
                {
                    // ignored
                }
            }

            _connections.Clear();
            _connectionById.Clear();
            _idByConnection.Clear();
            _steamIdByConnection.Clear();
            foreach (var queue in _sendQueues.Values)
                queue.Clear();
            _sendQueues.Clear();
            _pendingDisconnects.Clear();
            _sendLimitWarnings.Clear();

            var socket = _socket;
            _socket = null;
            if (socket == null)
                return;

            try
            {
                socket.Close();
            }
            catch
            {
                // ignored
            }
        }
    }

    internal static class FacepunchConnectionStatus
    {
        // Facepunch.Steamworks creates sockets and connections without options, so these apply to every connection.
        internal static void Apply(SteamSendSettings sendSettings)
        {
            SteamNetworkingUtils.SendRateMin = sendSettings.rateBytesPerSecond;
            SteamNetworkingUtils.SendRateMax = sendSettings.rateBytesPerSecond;
            SteamNetworkingUtils.SendBufferSize = sendSettings.bufferSizeBytes;
        }

        internal static void Close(FacepunchConnection conn, string reason)
        {
            if (reason == null)
                conn.Close();
            else conn.Close(false, 0, reason);
        }

        internal static string Describe(FacepunchConnection conn)
        {
            try
            {
                var status = conn.QuickStatus();
                return $"ping {status.Ping} ms, quality local {status.ConnectionQualityLocal:0.##} remote {status.ConnectionQualityRemote:0.##}, " +
                       $"out {status.OutBytesPerSec / 1024f:0.#} KiB/s, in {status.InBytesPerSec / 1024f:0.#} KiB/s, " +
                       $"pending reliable {status.PendingReliable} B, unacked reliable {status.SentUnackedReliable} B";
            }
            catch (Exception e)
            {
                return $"status unavailable ({e.Message})";
            }
        }
    }
}
#endif
