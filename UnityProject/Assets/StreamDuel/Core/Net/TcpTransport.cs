using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreamDuel.Core
{
    internal sealed class TcpDisconnectedException : IOException
    {
        public TcpDisconnectedException(Exception inner = null) : base("对方已断开。", inner) { }
    }

    public sealed class TcpTransport : INetTransport, IDisposable
    {
        readonly TcpClient client;
        readonly NetworkStream stream;
        readonly Thread readerThread;
        readonly object stateGate = new object();
        readonly object sendGate = new object();
        readonly Queue<string> incoming = new Queue<string>();
        readonly Encoding encoding = new UTF8Encoding(false, true);
        Exception receiveFailure;
        bool sendingCompleted;
        bool disposed;

        public bool PeerClosed
        {
            get { lock (stateGate) return receiveFailure is TcpDisconnectedException; }
        }

        internal TcpTransport(TcpClient client)
        {
            this.client = client;
            try
            {
                client.NoDelay = true;
                stream = client.GetStream();
                stream.WriteTimeout = 5000;
                readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "StreamDuel TCP 接收" };
                readerThread.Start();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public static TcpHost Listen(int port, out int actualPort)
        {
            actualPort = 0;
            if (port < 0 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "监听端口必须是 0～65535。");
            TcpListener listener = null;
            try
            {
                if (Socket.OSSupportsIPv6)
                {
                    try
                    {
                        listener = new TcpListener(IPAddress.IPv6Any, port);
                        listener.Server.DualMode = true;
                        listener.Start();
                    }
                    catch (Exception ex) when (ex is SocketException || ex is NotSupportedException)
                    {
                        listener?.Stop();
                        listener = null;
                    }
                }
                if (listener == null)
                {
                    listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();
                }
                actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                return new TcpHost(listener);
            }
            catch (Exception ex) when (ex is SocketException || ex is InvalidOperationException)
            {
                listener?.Stop();
                throw new IOException("无法监听指定的 TCP 端口。", ex);
            }
        }

        public static TcpTransport Connect(string host, int port, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("连接地址不能为空。", nameof(host));
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(nameof(port), "连接端口必须是 1～65535。");
            if (timeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs), "连接超时不能为负数。");
            TcpClient client = null;
            Task connecting = null;
            try
            {
                client = CreateClient();
                connecting = client.ConnectAsync(host, port);
                if (!connecting.Wait(timeoutMs))
                    throw new TimeoutException("连接超时，未能连接到对方。");
                connecting.GetAwaiter().GetResult();
                return new TcpTransport(client);
            }
            catch (Exception ex)
            {
                client?.Dispose();
                if (connecting != null)
                {
                    // 超时关闭套接字后，连接任务仍可能稍后报错，需要观察该结果。
                    connecting.ContinueWith(task => { var ignored = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                if (ex is TimeoutException) throw;
                throw new IOException("无法连接到对方，请检查地址、端口和对方是否已开始监听。", ex);
            }
        }

        static TcpClient CreateClient()
        {
            if (Socket.OSSupportsIPv6)
            {
                TcpClient dualClient = null;
                try
                {
                    dualClient = new TcpClient(AddressFamily.InterNetworkV6);
                    dualClient.Client.DualMode = true;
                    return dualClient;
                }
                catch (Exception ex) when (ex is SocketException || ex is NotSupportedException)
                {
                    dualClient?.Dispose();
                }
            }
            return new TcpClient(AddressFamily.InterNetwork);
        }

        public void Send(string msg)
        {
            if (msg == null) throw new ArgumentNullException(nameof(msg), "消息不能为空。");
            if (msg.IndexOf('\n') >= 0 || msg.IndexOf('\r') >= 0)
                throw new ArgumentException("一条消息不能包含原始换行符。", nameof(msg));
            byte[] bytes;
            try { bytes = encoding.GetBytes(msg + "\n"); }
            catch (EncoderFallbackException ex) { throw new ArgumentException("消息包含无效的 Unicode 字符。", nameof(msg), ex); }

            lock (sendGate)
            {
                lock (stateGate)
                {
                    ThrowIfDisposed();
                    if (sendingCompleted) throw new InvalidOperationException("发送端已经关闭，不能再发送消息。");
                    // 对方停止发送后仍可能等着本端报告最终哈希错误。
                    if (receiveFailure != null && !(receiveFailure is TcpDisconnectedException)) throw receiveFailure;
                }
                try { stream.Write(bytes, 0, bytes.Length); }
                catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
                {
                    var failure = new IOException("对方已断开或发送失败。", ex);
                    RecordFailure(failure);
                    client.Dispose();
                    throw failure;
                }
            }
        }

        public void CompleteSending()
        {
            lock (sendGate)
            {
                lock (stateGate)
                {
                    ThrowIfDisposed();
                    if (sendingCompleted) return;
                    if (receiveFailure != null && !(receiveFailure is TcpDisconnectedException)) throw receiveFailure;
                }
                try
                {
                    // 与 Send 共用锁，确保最后一条完整消息先于 TCP 的发送结束标记。
                    client.Client.Shutdown(SocketShutdown.Send);
                    lock (stateGate) sendingCompleted = true;
                }
                catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException)
                {
                    var failure = new IOException("对方已断开，无法正常结束发送。", ex);
                    RecordFailure(failure);
                    client.Dispose();
                    throw failure;
                }
            }
        }

        public bool TryReceive(out string msg)
        {
            lock (stateGate)
            {
                ThrowIfDisposed();
                // 最后的哈希或报错可能与断线一起到达，必须先交付所有完整消息。
                if (incoming.Count > 0)
                {
                    msg = incoming.Dequeue();
                    return true;
                }
                msg = null;
                if (receiveFailure != null) throw receiveFailure;
                return false;
            }
        }

        void ReadLoop()
        {
            bool cleanEof = false;
            try
            {
                using (var reader = new StreamReader(stream, encoding, false, 4096, true))
                {
                    var pending = new StringBuilder();
                    int next;
                    // 只等待下一字符，避免填满调用方缓冲区时拖住已完整到达的一行。
                    while ((next = reader.Read()) >= 0)
                    {
                        if (next != '\n') { pending.Append((char)next); continue; }
                        if (pending.Length > 0 && pending[pending.Length - 1] == '\r') pending.Length--;
                        string message = pending.ToString();
                        pending.Clear();
                        lock (stateGate)
                        {
                            if (disposed) return;
                            incoming.Enqueue(message);
                        }
                    }
                    cleanEof = pending.Length == 0;
                    RecordFailure(cleanEof
                        ? (Exception)new TcpDisconnectedException()
                        : new InvalidDataException("对方已断开，收到的最后一条消息缺少换行结束符。"));
                }
            }
            catch (DecoderFallbackException ex)
            {
                RecordFailure(new InvalidDataException("收到的消息不是有效的 UTF-8 文本。", ex));
            }
            catch (Exception ex)
            {
                RecordFailure(ex is IOException || ex is SocketException || ex is ObjectDisposedException
                    ? (Exception)new IOException("对方已断开，接收连接异常。", ex)
                    : new IOException("接收 TCP 消息失败。", ex));
            }
            finally
            {
                // 正常 EOF 只结束接收方向，本端仍可发送错误，直到调用者关闭发送端。
                if (!cleanEof) client.Dispose();
            }
        }

        void RecordFailure(Exception failure)
        {
            lock (stateGate)
            {
                // 读到 EOF 后仍可能写失败，异常不能被终局的正常半关闭分支吞掉。
                if (!disposed && (receiveFailure == null
                    || receiveFailure is TcpDisconnectedException && !(failure is TcpDisconnectedException)))
                    receiveFailure = failure;
            }
        }

        void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(TcpTransport), "连接已经关闭。");
        }

        public void Dispose()
        {
            lock (stateGate)
            {
                if (disposed) return;
                disposed = true;
                incoming.Clear();
            }
            // 先关闭套接字解除阻塞读取，避免等待线程时无法退出。
            client.Dispose();
            if (Thread.CurrentThread != readerThread) readerThread.Join(1000);
        }
    }

    public sealed class TcpHost : IDisposable
    {
        readonly TcpListener listener;
        readonly object stateGate = new object();
        readonly object acceptGate = new object();
        Task<TcpClient> pendingAccept;
        bool disposed;

        internal TcpHost(TcpListener listener) { this.listener = listener; }

        public TcpTransport Accept(int timeoutMs)
        {
            if (timeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs), "等待连接超时不能为负数。");
            lock (acceptGate)
            {
                Task<TcpClient> accepting;
                lock (stateGate)
                {
                    ThrowIfDisposed();
                    // 超时不丢弃接收任务，否则后来到达的连接会被无人领取的任务占用。
                    if (pendingAccept == null) pendingAccept = listener.AcceptTcpClientAsync();
                    accepting = pendingAccept;
                }
                TcpClient client;
                try
                {
                    if (!accepting.Wait(timeoutMs)) return null;
                    client = accepting.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    lock (stateGate)
                    {
                        if (pendingAccept == accepting) pendingAccept = null;
                        ThrowIfDisposed();
                    }
                    throw new IOException("等待对方连接失败。", ex);
                }
                lock (stateGate)
                {
                    if (pendingAccept == accepting) pendingAccept = null;
                    if (disposed)
                    {
                        client.Dispose();
                        ThrowIfDisposed();
                    }
                }
                return new TcpTransport(client);
            }
        }

        void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(TcpHost), "监听器已经关闭。");
        }

        public void Dispose()
        {
            Task<TcpClient> abandoned;
            lock (stateGate)
            {
                if (disposed) return;
                disposed = true;
                abandoned = pendingAccept;
                pendingAccept = null;
            }
            listener.Stop();
            if (abandoned != null)
            {
                abandoned.ContinueWith(task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                    else if (task.IsFaulted) { var ignored = task.Exception; }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}
