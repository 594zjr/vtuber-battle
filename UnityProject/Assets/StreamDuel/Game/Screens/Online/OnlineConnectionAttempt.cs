using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    internal sealed class OnlineConnectionAttempt : IDisposable
    {
        readonly object gate = new object();
        TcpHost listener;
        TcpTransport result;
        bool cancelled, completed;
        readonly bool ownsListener;
        string error, status;
        int listeningPort;

        public int ListeningPort { get { lock (gate) return listeningPort; } }

        public OnlineConnectionAttempt(bool host, string address, int port, TcpHost existingListener = null)
        {
            listener = existingListener;
            ownsListener = existingListener == null;
            status = host ? "正在创建房间……" : "正在连接对方……";
            Task.Run(() => Run(host, address, port, existingListener));
        }

        public TcpHost TakeListener()
        {
            lock (gate)
            {
                if (!completed || !ownsListener) return null;
                var value = listener;
                listener = null;
                return value;
            }
        }

        public bool TakeResult(out TcpTransport transport, out string failure, out string notice)
        {
            lock (gate)
            {
                transport = result;
                result = null;
                failure = error;
                notice = status;
                return completed;
            }
        }

        void Run(bool host, string address, int port, TcpHost existingListener)
        {
            TcpHost opened = null;
            TcpTransport connected = null;
            bool published = false;
            try
            {
                if (host)
                {
                    int actualPort = port;
                    opened = existingListener ?? TcpTransport.Listen(port, out actualPort);
                    lock (gate)
                    {
                        if (cancelled) return;
                        listener = opened;
                        listeningPort = actualPort;
                    }
                    string addresses = LocalAddresses();
                    lock (gate) status = "房间已开放，等待对方加入。\n本机局域网 IP：" + addresses
                        + "\n端口：" + actualPort + "；同机测试可使用 127.0.0.1。";
                    while (true)
                    {
                        lock (gate) if (cancelled) return;
                        connected = opened.Accept(200);
                        if (connected != null) break;
                    }
                }
                else connected = TcpTransport.Connect(address, port, 5000);
                lock (gate)
                {
                    if (cancelled) return;
                    result = connected;
                    connected = null;
                    published = true;
                    completed = true;
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    if (!cancelled)
                    {
                        error = ex is TimeoutException ? "连接超时，请检查地址、端口和对方是否已开房。"
                            : host ? "开房失败，请检查端口是否被占用，以及系统是否允许局域网连接。"
                            : "连接失败，请检查地址、端口和对方是否已开房。";
                        completed = true;
                    }
                }
            }
            finally
            {
                connected?.Dispose();
                // 房主监听端口由整场会话持有，首次接入后仍须接受原会话重连。
                if (!published && ownsListener)
                {
                    opened?.Dispose();
                    lock (gate) if (ReferenceEquals(listener, opened)) listener = null;
                }
            }
        }

        static string LocalAddresses()
        {
            try
            {
                var addresses = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                    .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                    .Select(item => item.Address)
                    .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    .Select(ip => ip.ToString()).Distinct().OrderBy(ip => ip, StringComparer.Ordinal).ToArray();
                return addresses.Length > 0 ? string.Join(" / ", addresses) : "未检测到可用的局域网 IPv4 地址";
            }
            catch (Exception) { return "暂时无法读取，请在系统网络设置中查看"; }
        }

        public void Dispose()
        {
            TcpHost stop;
            TcpTransport close;
            lock (gate)
            {
                if (cancelled) return;
                cancelled = true;
                stop = ownsListener ? listener : null;
                listener = null;
                close = result;
                result = null;
            }
            stop?.Dispose();
            // 取消连接时不等待后台任务；晚到的连接由任务自行关闭。
            if (close != null) Task.Run(() => close.Dispose());
        }
    }
}
