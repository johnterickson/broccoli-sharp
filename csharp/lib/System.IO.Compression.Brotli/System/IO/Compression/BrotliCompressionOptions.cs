﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace BrotliBlockLib
{
    /// <summary>
    /// Provides compression options to be used with <see cref="BrotliStream"/>.
    /// </summary>
    public sealed class BrotliCompressionOptions
    {
        private int _quality = BrotliUtils.Quality_Default;

        /// <summary>
        /// Gets or sets the compression quality for a Brotli compression stream.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException" accessor="set">The value is less than 0 or greater than 11.</exception>
        /// <remarks>
        /// The higher the quality, the slower the compression. Range is from 0 to 11. The default value is 4.
        /// </remarks>
        public int Quality
        {
            get => _quality;
            set
            {
                NetstandardCompat.ThrowIfLessThan(value, 0, nameof(value));
                NetstandardCompat.ThrowIfGreaterThan(value, 11, nameof(value));

                _quality = value;
            }
        }

        public bool ByteAlign { get; set; }
        public bool Bare { get; set; }
        public bool Catable { get; set; }
        public bool Appendable { get; set; }
        public bool MagicNumber { get; set; }

        private int _windowBits = 22;
        public int WindowBits
        {
            get => _windowBits; set
            {
                NetstandardCompat.ThrowIfLessThan(value, 10, nameof(value));
                NetstandardCompat.ThrowIfGreaterThan(value, 24, nameof(value));

                _windowBits = value;
            }
        }
    }
}
