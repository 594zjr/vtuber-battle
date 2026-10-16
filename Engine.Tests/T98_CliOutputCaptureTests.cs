using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

public sealed class T98_CliOutputCaptureTests
{
    const int WaitMilliseconds = 5000;
    const string Prompt = "选择行动（q 退出）> ";

    [Theory]
    [InlineData(1024, "short")]
    [InlineData(1024, "utf8_block")]
    [InlineData(1024, "ascii_then_utf8_block")]
    [InlineData(4096, "unix_short_read")]
    public async Task AvailableOutputIncludesTheWholePromptBeforeThePipeCloses(int readerBufferSize, string scenario)
    {
        string payload = Payload(scenario);
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        using var stream = new OpenEndedPayloadStream(bytes);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false, bufferSize: readerBufferSize, leaveOpen: true);
        var destination = new StringBuilder();
        Task capture = StartActualCapture(reader, destination);

        try
        {
            // 读者已经取完全部字节并再次向仍开着的管道读取，快照不依赖调度延迟。
            Assert.True(stream.AtOpenEnd.Wait(WaitMilliseconds), "输出读取器未在 5 秒内到达仍未关闭的管道尾部。");
            Assert.False(stream.EofReleased);
            Assert.False(capture.IsCompleted);
            string visible;
            lock (destination) visible = destination.ToString();
            Assert.Equal(payload, visible);
        }
        finally
        {
            // 即使旧实现扣住尾部而断言失败，也必须放行真实读取线程并等它退出。
            stream.ReleaseEof();
            await capture.WaitAsync(TimeSpan.FromMilliseconds(WaitMilliseconds));
        }
    }

    static string Payload(string scenario)
    {
        if (scenario == "short") return Prompt;
        if (scenario == "unix_short_read")
        {
            string payload = new string('中', 1100) + Prompt;
            Assert.InRange(Encoding.UTF8.GetByteCount(payload), 1, 4095);
            Assert.InRange(payload.Length, 1025, 2047);
            return payload;
        }

        int remainingBytes = 1024 - Encoding.UTF8.GetByteCount(Prompt);
        string block = new string('中', remainingBytes / 3) + new string('a', remainingBytes % 3) + Prompt;
        Assert.Equal(1024, Encoding.UTF8.GetByteCount(block));
        Assert.InRange(block.Length, 1, 1023);
        if (scenario == "utf8_block") return block;
        if (scenario == "ascii_then_utf8_block")
        {
            string payload = new string('a', 1024) + block;
            Assert.Equal(2048, Encoding.UTF8.GetByteCount(payload));
            Assert.InRange(payload.Length, 1025, 2047);
            return payload;
        }
        throw new ArgumentException("未知输出捕获用例：" + scenario, nameof(scenario));
    }

    static Task StartActualCapture(StreamReader reader, StringBuilder destination)
    {
        var processType = typeof(T98_CliTerminationTests).GetNestedType("CliProcess", BindingFlags.NonPublic);
        Assert.NotNull(processType);
        var read = processType.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(StreamReader), typeof(StringBuilder) }, null);
        Assert.NotNull(read);
        return Assert.IsAssignableFrom<Task>(read.Invoke(null, new object[] { reader, destination }));
    }

    sealed class OpenEndedPayloadStream : Stream
    {
        readonly byte[] payload;
        readonly ManualResetEventSlim releaseEof = new ManualResetEventSlim();
        int position;
        bool disposed;

        public readonly ManualResetEventSlim AtOpenEnd = new ManualResetEventSlim();
        public bool EofReleased { get { return releaseEof.IsSet; } }

        public OpenEndedPayloadStream(byte[] payload) { this.payload = payload; }
        public void ReleaseEof() { releaseEof.Set(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            if (position < payload.Length)
            {
                int available = Math.Min(count, payload.Length - position);
                Buffer.BlockCopy(payload, position, buffer, offset, available);
                position += available;
                return available;
            }

            AtOpenEnd.Set();
            if (!releaseEof.Wait(WaitMilliseconds))
                throw new TimeoutException("测试未在 5 秒内释放管道 EOF。");
            return 0;
        }

        public override bool CanRead { get { return !disposed; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                releaseEof.Set();
                AtOpenEnd.Dispose();
                releaseEof.Dispose();
                disposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
