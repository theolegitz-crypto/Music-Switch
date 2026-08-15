using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaController.GameBar
{
    internal sealed class DesktopBridgeClient : IDisposable
    {
        private const string PipePath = @"\\.\pipe\LOCAL\MediaController.GameBar";
        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _loop;
        private IntPtr _handle = InvalidHandleValue;
        private bool _disposed;

        public event Action<string> MessageReceived;

        public void Start()
        {
            if (_disposed || _loop != null)
            {
                return;
            }

            _loop = Task.Run((Func<Task>)RunAsync);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            CloseCurrentHandle();
            _cts.Dispose();
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                var handle = CreateFile2(
                    PipePath,
                    GenericRead,
                    FileShareRead | FileShareWrite,
                    OpenExisting,
                    IntPtr.Zero);

                if (handle == IntPtr.Zero || handle == InvalidHandleValue)
                {
                    await DelayReconnectAsync();
                    continue;
                }

                _handle = handle;

                try
                {
                    ReadMessages(handle);
                }
                catch
                {
                    // The desktop app can restart independently of the widget.
                }
                finally
                {
                    CloseCurrentHandle();
                }

                await DelayReconnectAsync();
            }
        }

        private void ReadMessages(IntPtr handle)
        {
            var bytes = new byte[16 * 1024];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var decoder = Encoding.UTF8.GetDecoder();
            var pending = new StringBuilder();

            while (!_cts.IsCancellationRequested)
            {
                uint read;
                if (!ReadFile(handle, bytes, (uint)bytes.Length, out read, IntPtr.Zero) || read == 0)
                {
                    return;
                }

                var charCount = decoder.GetChars(bytes, 0, (int)read, chars, 0, false);
                pending.Append(chars, 0, charCount);

                while (true)
                {
                    var newline = IndexOfNewline(pending);
                    if (newline < 0)
                    {
                        break;
                    }

                    var message = pending.ToString(0, newline).TrimEnd('\r');
                    pending.Remove(0, newline + 1);

                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        var handler = MessageReceived;
                        if (handler != null)
                        {
                            handler(message);
                        }
                    }
                }
            }
        }

        private static int IndexOfNewline(StringBuilder text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private async Task DelayReconnectAsync()
        {
            try
            {
                await Task.Delay(600, _cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void CloseCurrentHandle()
        {
            var handle = Interlocked.Exchange(ref _handle, InvalidHandleValue);
            if (handle != IntPtr.Zero && handle != InvalidHandleValue)
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile2(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            uint dwCreationDisposition,
            IntPtr pCreateExParams);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(
            IntPtr hFile,
            [Out] byte[] lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
