// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;

namespace BrotliBlockLib
{
    /// <summary>Provides methods and properties used to compress and decompress streams by using the Brotli data format specification.</summary>
    public sealed partial class BrotliBlockStream : Stream
    {
        private const int DefaultInternalBufferSize = (1 << 16) - 16; //65520;
        private Stream _stream;
        private byte[] _buffer;
        private readonly bool _leaveOpen;
        private readonly CompressionMode _mode;

        public static readonly byte[] EndBlock = new byte[1] { 0x03 };

        private static readonly Lazy<byte[]>[] StartBlocks = Enumerable.Range(0, BrotliUtils.WindowBits_Max + 1)
            .Select((int window_bits) => new Lazy<byte[]>(() => CreateStartBlock((byte)window_bits), LazyThreadSafetyMode.PublicationOnly))
            .ToArray();

        private static byte[] CreateStartBlock(byte window_size)
        {
            using var compressed = new MemoryStream();
            using (var s0 = new BrotliBlockStream(compressed, new BrotliCompressionOptions()
            {
                WindowBits = window_size,
                Appendable = true,
                ByteAlign = true,
                Bare = true,
                MagicNumber = false,
            },
            leaveOpen: true))
            {
                // empty

            }
            return compressed.ToArray();
        }

        public static byte[] GetStartBlock(uint window_size) => StartBlocks[window_size].Value;

        public static BrotliBlockStream CreateBlockDecompressionStream(Stream stream, BlockPosition position, bool leaveOpen = false, uint window_size = 22) =>
            new BrotliBlockStream(stream, position, leaveOpen, window_size);

        private BrotliBlockStream(Stream stream, BlockPosition position, bool leaveOpen = false, uint window_size = 22) : this (stream, CompressionMode.Decompress, leaveOpen)
        {
            _window_bits = window_size;
            if (position == BlockPosition.Middle || position == BlockPosition.Last)
            {
                _needs_start_block = true;
            }

            if (position == BlockPosition.First || position == BlockPosition.Middle)
            {
                _needs_end_block = true;
            }
        }

        /// <summary>Initializes a new instance of the <see cref="System.IO.Compression.BrotliStream" /> class by using the specified stream and compression mode.</summary>
        /// <param name="stream">The stream to which compressed data is written or from which data to decompress is read.</param>
        /// <param name="mode">One of the enumeration values that indicates whether to compress data to the stream or decompress data from the stream.</param>
        public BrotliBlockStream(Stream stream, CompressionMode mode) : this(stream, mode, leaveOpen: false) { }

        /// <summary>Initializes a new instance of the <see cref="System.IO.Compression.BrotliStream" /> class by using the specified stream and compression mode, and optionally leaves the stream open.</summary>
        /// <param name="stream">The stream to which compressed data is written or from which data to decompress is read.</param>
        /// <param name="mode">One of the enumeration values that indicates whether to compress data to the stream or decompress data from the stream.</param>
        /// <param name="leaveOpen"><see langword="true" /> to leave the stream open after the <see cref="System.IO.Compression.BrotliStream" /> object is disposed; otherwise, <see langword="false" />.</param>
        public BrotliBlockStream(Stream stream, CompressionMode mode, bool leaveOpen)
        {
            NetstandardCompat.ThrowIfNull(stream, nameof(stream));

            switch (mode)
            {
                case CompressionMode.Compress:
                    if (!stream.CanWrite)
                    {
                        throw new ArgumentException("SR.Stream_FalseCanWrite", nameof(stream));
                    }

                    _encoder.SetQuality(BrotliUtils.Quality_Default);
                    _encoder.SetWindow(BrotliUtils.WindowBits_Default);
                    break;

                case CompressionMode.Decompress:
                    if (!stream.CanRead)
                    {
                        throw new ArgumentException("SR.Stream_FalseCanRead", nameof(stream));
                    }
                    break;

                default:
                    throw new ArgumentException("SR.ArgumentOutOfRange_Enum", nameof(mode));
            }

            _mode = mode;
            _stream = stream;
            _leaveOpen = leaveOpen;
            _buffer = ArrayPool<byte>.Shared.Rent(DefaultInternalBufferSize);
        }

        private void EnsureNotDisposed()
        {
            if (_stream is null)
            {
                throw new ObjectDisposedException(nameof(_stream));
            }
        }

        /// <summary>Releases the unmanaged resources used by the <see cref="System.IO.Compression.BrotliStream" /> and optionally releases the managed resources.</summary>
        /// <param name="disposing"><see langword="true" /> to release both managed and unmanaged resources; <see langword="false" /> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && _stream != null)
                {
                    if (_mode == CompressionMode.Compress)
                    {
                        WriteCore(ReadOnlySpan<byte>.Empty, isFinalBlock: true);
                    }

                    if (!_leaveOpen)
                    {
                        _stream.Dispose();
                    }
                }
            }
            finally
            {
                ReleaseStateForDispose();
                base.Dispose(disposing);
            }
        }

#if NET5_0_OR_GREATER
        /// <summary>Asynchronously releases the unmanaged resources used by the <see cref="System.IO.Compression.BrotliStream" />.</summary>
        /// <returns>A task that represents the asynchronous dispose operation.</returns>
        /// <remarks><para>This method lets you perform a resource-intensive dispose operation without blocking the main thread. This performance consideration is particularly important in apps where a time-consuming stream operation can block the UI thread and make your app appear as if it is not working. The async methods are used in conjunction with the <see langword="async" /> and <see langword="await" /> keywords in Visual Basic and C#.</para>
        /// <para>This method disposes the Brotli stream by writing any changes to the backing store and closing the stream to release resources.</para>
        /// <para>Calling <see cref="System.IO.Compression.BrotliStream.DisposeAsync" /> allows the resources used by the <see cref="System.IO.Compression.BrotliStream" /> to be reallocated for other purposes. For more information, see [Cleaning Up Unmanaged Resources](/dotnet/standard/garbage-collection/unmanaged).</para></remarks>
        public override async ValueTask DisposeAsync()
        {
            try
            {
                if (_stream != null)
                {
                    if (_mode == CompressionMode.Compress)
                    {
                        await WriteAsyncMemoryCore(ReadOnlyMemory<byte>.Empty, CancellationToken.None, isFinalBlock: true).ConfigureAwait(false);
                    }

                    if (!_leaveOpen)
                    {
                        await _stream.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                ReleaseStateForDispose();
            }
        }
#endif

        private void ReleaseStateForDispose()
        {
            _stream = null!;
            _encoder.Dispose();
            _decoder.Dispose();

            byte[] buffer = _buffer;
            if (buffer != null)
            {
                _buffer = null!;
                if (_activeAsyncOperation != 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        /// <summary>Gets a reference to the underlying stream.</summary>
        /// <value>A stream object that represents the underlying stream.</value>
        /// <exception cref="System.ObjectDisposedException">The underlying stream is closed.</exception>
        public Stream BaseStream => _stream;
        /// <summary>Gets a value indicating whether the stream supports reading while decompressing a file.</summary>
        /// <value><see langword="true" /> if the <see cref="System.IO.Compression.CompressionMode" /> value is <see langword="Decompress," /> and the underlying stream supports reading and is not closed; otherwise, <see langword="false" />.</value>
        public override bool CanRead => _mode == CompressionMode.Decompress && _stream != null && _stream.CanRead;
        /// <summary>Gets a value indicating whether the stream supports writing.</summary>
        /// <value><see langword="true" /> if the <see cref="System.IO.Compression.CompressionMode" /> value is <see langword="Compress" />, and the underlying stream supports writing and is not closed; otherwise, <see langword="false" />.</value>
        public override bool CanWrite => _mode == CompressionMode.Compress && _stream != null && _stream.CanWrite;
        /// <summary>Gets a value indicating whether the stream supports seeking.</summary>
        /// <value><see langword="false" /> in all cases.</value>
        public override bool CanSeek => false;
        /// <summary>This property is not supported and always throws a <see cref="System.NotSupportedException" />.</summary>
        /// <param name="offset">The location in the stream.</param>
        /// <param name="origin">One of the <see cref="System.IO.SeekOrigin" /> values.</param>
        /// <returns>A long value.</returns>
        /// <exception cref="System.NotSupportedException">This property is not supported on this stream.</exception>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <summary>This property is not supported and always throws a <see cref="System.NotSupportedException" />.</summary>
        /// <value>A long value.</value>
        /// <exception cref="System.NotSupportedException">This property is not supported on this stream.</exception>
        public override long Length => throw new NotSupportedException();
        /// <summary>This property is not supported and always throws a <see cref="System.NotSupportedException" />.</summary>
        /// <value>A long value.</value>
        /// <exception cref="System.NotSupportedException">This property is not supported on this stream.</exception>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        /// <summary>This property is not supported and always throws a <see cref="System.NotSupportedException" />.</summary>
        /// <param name="value">The length of the stream.</param>
        public override void SetLength(long value) => throw new NotSupportedException();

        private volatile int _activeAsyncOperation;

        private void EnsureNoActiveAsyncOperation()
        {
            if (_activeAsyncOperation != 0)
            {
                ThrowInvalidBeginCall();
            }
        }

        private void AsyncOperationStarting()
        {
            if (Interlocked.Exchange(ref _activeAsyncOperation, 1) != 0)
            {
                ThrowInvalidBeginCall();
            }
        }

        private void AsyncOperationCompleting()
        {
            Debug.Assert(_activeAsyncOperation != 0);
            _activeAsyncOperation = 0;
        }

        private static void ThrowInvalidBeginCall() =>
            throw new InvalidOperationException("SR.InvalidBeginCall");
    }
}
