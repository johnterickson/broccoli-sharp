// BitArray 

using BrotliBlockLib;
using System.IO.Compression;
using BrotliCompressionOptions = BrotliBlockLib.BrotliCompressionOptions;

static class BrotliBlockApp
{
    private static void DisplayUsage()
    {
        Console.WriteLine("BrotliBlock [-w window_bits] [-q quality] [-c (compress)]");
        Console.WriteLine("[--position [First|Middle|Last] [-b (bare)]");
        Console.WriteLine("[-o output_path (stdout defaut, use '{}' for block index)]");
        Console.WriteLine("[--block-size block_size] [--buffer-size buffer_size]");
        Console.WriteLine("input_paths or --");
    }

    public static void Main(string[] argsArray)
    {
        byte window_bits = 22;
        uint quality = 11;
        bool compress = false;
        uint block_size = 0;
        uint buffer_size = 81920;
        BlockPosition? blockPosition = null;

        var args = new Queue<string>(argsArray);

        Queue<Stream> inputs = new();
        string? output_path = null;

        while (args.Count > 0 && args.Peek().StartsWith('-') && args.Peek() != "--")
        {
            string arg = args.Dequeue();
            switch (arg)
            {
                case "-w":
                    window_bits = byte.Parse(args.Dequeue());
                    break;
                case "-q":
                    quality = uint.Parse(args.Dequeue());
                    break;
                case "-o":
                    output_path = args.Dequeue();
                    break;
                case "-c":
                    compress = true;
                    break;
                case "-b":
                    blockPosition = BlockPosition.Middle;
                    break;
                case "-p":
                case "--position":
                    blockPosition = Enum.Parse<BlockPosition>(args.Dequeue());
                    break;
                case "--block-size":
                    block_size = uint.Parse(args.Dequeue());
                    break;
                case "--buffer-size":
                    buffer_size = uint.Parse(args.Dequeue());
                    break;
                case "-?":
                case "--help":
                    DisplayUsage();
                    return;
                default:
                    DisplayUsage();
                    throw new ArgumentException($"unknown arg '{arg}'.");
            }
        }

        while (args.Count > 0)
        {
            string arg = args.Dequeue();
            if (arg == "--")
            {
                inputs.Enqueue(Console.OpenStandardInput());
            }
            else if (arg.Contains('*'))
            {
                string? dir = Path.GetDirectoryName(arg);
                if (string.IsNullOrEmpty(dir))
                {
                    dir = Environment.CurrentDirectory;
                }
                foreach (string file in Directory.GetFiles(
                    dir,
                    Path.GetFileName(arg) ?? arg))
                {
                    Console.Error.WriteLine($"Processing {file}...");
                    inputs.Enqueue(File.OpenRead(file));
                }
            }
            else
            {
                inputs.Enqueue(File.OpenRead(arg));
            }
        }

        if (inputs.Count == 0)
        {
            inputs.Enqueue(Console.OpenStandardInput());
        }

        output_path ??= "--";

        if (compress)
        {
            if (block_size > 0 && output_path == "--")
            {
                throw new ArgumentException("Specify output path pattern when using block size.");
            }

            uint index = 0;
            int bytes_read_in_block = 0;
            byte[] buffer = new byte[buffer_size];
            (Stream raw, BrotliBlockStream brotli)? compressed = null;
            Stream current_input = inputs.Dequeue();

            bool bare = blockPosition.HasValue;

            while (true)
            {
                if (compressed == null)
                {
                    Stream output;
                    if (block_size > 0) {
                        output = File.OpenWrite(output_path.Replace("{}", index.ToString("D3")));
                    }
                    else
                    {
                        output = output_path == "--" ? Console.OpenStandardOutput() : File.OpenWrite(output_path);
                    }

                    if (blockPosition == BlockPosition.First || blockPosition == BlockPosition.Single)
                    {
                        output.Write(BrotliBlockStream.GetStartBlock((byte)window_bits));
                    }

                    compressed = (output, new BrotliBlockStream(output, new BrotliCompressionOptions()
                    {
                        Quality = (int)quality,
                        WindowBits = (int)window_bits,
                        Catable = bare,
                        ByteAlign = bare,
                        MagicNumber = bare,
                    }, leaveOpen: true));
                }

                int bytes_to_read;
                if (block_size > 0)
                {
                    bytes_to_read = (int)Math.Min(buffer.Length, block_size - bytes_read_in_block);
                }
                else
                {
                    bytes_to_read = buffer.Length;
                }
                int bytes_read = current_input.Read(buffer, 0, Math.Min(buffer.Length, bytes_to_read));
                compressed.Value.brotli.Write(buffer, 0, bytes_read);
                bytes_read_in_block += bytes_read;
                
                if (block_size > 0 && bytes_read_in_block == block_size)
                {
                    compressed.Value.brotli.Dispose();
                    compressed.Value.raw.Dispose();
                    compressed = null;
                    bytes_read_in_block = 0;
                    index++;
                    continue;
                }

                if (bytes_read == 0)
                {
                    current_input.Dispose();
                    if (inputs.Count == 0)
                    {
                        compressed.Value.brotli.Dispose();

                        if (blockPosition == BlockPosition.Last)
                        {
                            compressed.Value.raw.Write(BrotliBlockStream.EndBlock);
                        }
                        
                        compressed.Value.raw.Dispose();

                        break;
                    }
                    current_input = inputs.Dequeue();
                }
            }
        }
        else
        {
            if (block_size > 0)
            {
                throw new ArgumentException("Block size not supported in decompression mode.");
            }
            
            using Stream output = output_path == "--" ? Console.OpenStandardOutput() : File.OpenWrite(output_path);
            using ConcatenatedStream input_stream = new(inputs);
            using Stream decompressed = blockPosition.HasValue
                ? BrotliBlockStream.CreateBlockDecompressionStream(input_stream, blockPosition.Value, window_size: window_bits)
                : new BrotliStream(input_stream, CompressionMode.Decompress);
            decompressed.CopyTo(output);
        }
    }

    class ConcatenatedStream : Stream
    {
        private readonly Queue<Stream> streams;

        public ConcatenatedStream(Queue<Stream> streams)
        {
            this.streams = streams;
        }

        public override bool CanRead => true;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalBytesRead = 0;

            while (count > 0 && streams.Count > 0)
            {
                int bytesRead = streams.Peek().Read(buffer, offset, count);
                if (bytesRead == 0)
                {
                    streams.Dequeue().Dispose();
                    continue;
                }

                totalBytesRead += bytesRead;
                offset += bytesRead;
                count -= bytesRead;
            }

            return totalBytesRead;
        }

        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override void Flush() => throw new NotImplementedException();
        public override long Length => throw new NotImplementedException();

        public override long Position
        {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
        public override void SetLength(long value) => throw new NotImplementedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
    }
}