using System;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;

namespace StreamDuel.Core
{
    public sealed class LanBeaconSender : IDisposable
    {
        readonly object gate = new object();
        readonly byte[] payload;
        readonly IPEndPoint destination = new IPEndPoint(IPAddress.Broadcast, LanBeacon.Port);
        UdpClient client;
        bool disposed, sentFirst;
        double elapsed;

        public string Error { get; private set; }

        public LanBeaconSender(LanRoom room)
        {
            string text = LanBeacon.Encode(room);
            if (text == null)
            {
                Error = "房间信息无效或超过 512 字节，无法广播局域网房间。";
                return;
            }
            try
            {
                payload = new UTF8Encoding(false, true).GetBytes(text);
                if (payload.Length > 512)
                {
                    Error = "房间广播超过 512 字节，无法发送。";
                    return;
                }
                client = new UdpClient(AddressFamily.InterNetwork);
                client.EnableBroadcast = true;
                client.Client.Blocking = false;
            }
            catch (Exception ex) when (ex is SocketException || ex is SecurityException
                || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException)
            {
                Fail("无法建立局域网广播，请检查网络是否可用及程序是否具有网络权限。");
            }
        }

        public void Tick(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds), "广播推进时间必须为有限的非负秒数。");
            lock (gate)
            {
                if (disposed || Error != null) return;
                if (!sentFirst)
                {
                    sentFirst = true;
                    SendBeacon();
                    return;
                }
                // 长时间未调用时只发送当前广播，不补发旧包，也不将大时间值相加。
                if (seconds >= 1 || elapsed >= 1 - seconds)
                {
                    elapsed = 0;
                    SendBeacon();
                }
                else elapsed += seconds;
            }
        }

        void SendBeacon()
        {
            try
            {
                if (client.Send(payload, payload.Length, destination) != payload.Length)
                    Fail("局域网房间广播未能完整发送。");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
            {
                // 非阻塞发送暂时繁忙时等待下一次广播，不阻塞对战输入。
            }
            catch (Exception ex) when (ex is SocketException || ex is SecurityException
                || ex is UnauthorizedAccessException || ex is ObjectDisposedException || ex is NotSupportedException)
            {
                Fail("局域网房间广播失败，请检查网络是否可用及广播权限。");
            }
        }

        void Fail(string message)
        {
            Error = message;
            CloseClient();
        }

        void CloseClient()
        {
            try { client?.Dispose(); }
            catch (Exception) { }
            client = null;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                CloseClient();
            }
        }
    }

    public sealed class LanBeaconListener : IDisposable
    {
        const int MaxPacketsPerPoll = 64;
        readonly object gate = new object();
        readonly Encoding encoding = new UTF8Encoding(false, true);
        UdpClient client;
        bool disposed;

        public LanRoomList RoomList { get; }
        public string Error { get; private set; }

        public LanBeaconListener(LanRoomList rooms = null)
        {
            RoomList = rooms ?? new LanRoomList();
            try
            {
                client = new UdpClient(AddressFamily.InterNetwork);
                client.Client.ExclusiveAddressUse = true;
                client.Client.Blocking = false;
                client.Client.Bind(new IPEndPoint(IPAddress.Any, LanBeacon.Port));
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                Fail("局域网发现端口 47777 已被占用，请关闭其他房间列表监听后重试。");
            }
            catch (Exception ex) when (ex is SocketException || ex is SecurityException
                || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is ArgumentException)
            {
                Fail("无法监听局域网发现端口 47777，请检查端口是否被占用、网络是否可用及程序权限。");
            }
        }

        public void Poll(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0)
                throw new ArgumentOutOfRangeException(nameof(now), "监听时间必须为有限的非负秒数。");
            lock (gate)
            {
                if (disposed || Error != null) return;
                // 每次处理数量有上限，持续到来的无效广播也不能饿死调用方。
                for (int count = 0; count < MaxPacketsPerPoll; count++)
                {
                    byte[] bytes;
                    var source = new IPEndPoint(IPAddress.Any, 0);
                    try
                    {
                        if (!client.Client.Poll(0, SelectMode.SelectRead)) return;
                        bytes = client.Receive(ref source);
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
                    {
                        return;
                    }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize
                        || ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        continue;
                    }
                    catch (Exception ex) when (ex is SocketException || ex is SecurityException
                        || ex is UnauthorizedAccessException || ex is ObjectDisposedException || ex is NotSupportedException)
                    {
                        Fail("接收局域网房间广播失败，请检查网络状态及程序权限。");
                        return;
                    }
                    if (bytes.Length == 0 || bytes.Length > 512) continue;
                    string text;
                    try { text = encoding.GetString(bytes); }
                    catch (DecoderFallbackException) { continue; }
                    RoomList.Receive(text, source.Address.ToString(), now);
                }
            }
        }

        void Fail(string message)
        {
            Error = message;
            CloseClient();
        }

        void CloseClient()
        {
            try { client?.Dispose(); }
            catch (Exception) { }
            client = null;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                CloseClient();
            }
        }
    }
}
