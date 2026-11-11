using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BirkNext.Api.Services.WorkspaceTransfer;

/// <summary>
/// Authenticated encryption for backups: AES-256-GCM (System.Security.Cryptography, no own primitive) in the STREAM construction
/// (Hoang, Reyhanitabar, Rogaway, Vizár 2015), so a backup of any size is encrypted and decrypted in bounded memory without a plaintext temporary copy.
/// <list type="bullet">
/// <item>File = magic "BNBACKUP" · version (1) · iterations (int32) · salt (16) · nonce prefix (7) · header length (int32) · header (UTF-8 JSON) · chunks.</item>
/// <item>Chunk = ciphertext length (int32) · ciphertext · tag (16). Nonce = prefix (7) ‖ counter (uint32, big-endian) ‖ final flag (1).</item>
/// <item>Associated data of every chunk = everything before the first chunk, so a changed header, salt or iteration count fails authentication.</item>
/// <item>The final flag makes truncation detectable; reordering or dropping a chunk changes the counter and fails the tag.</item>
/// <item>Key = PBKDF2-HMAC-SHA256(passphrase, salt, iterations) → 32 bytes. A wrong passphrase fails on the first chunk: it is not distinguishable from
/// tampering, by design.</item>
/// </list>
/// Losing the passphrase loses the backup; nothing in BirkNext can recover it.
/// </summary>
public static class TransferCrypto
{
    public static readonly byte[] Magic = "BNBACKUP"u8.ToArray();
    public const byte Version = 1;
    public const int ChunkSize = 1 << 20;
    public const int DefaultIterations = 600_000;
    public const int MinIterations = 100_000;
    public const int MaxIterations = 10_000_000;
    public const int MaxHeaderBytes = 64 * 1024;
    public const int MinPassphraseLength = 12;
    private const int TagSize = 16;
    private const int SaltSize = 16;
    private const int PrefixSize = 7;

    public static string? PassphraseProblem(string? passphrase) =>
        string.IsNullOrEmpty(passphrase) ? "A passphrase is required: full backups are always encrypted."
        : passphrase.Length < MinPassphraseLength ? $"The passphrase must be at least {MinPassphraseLength} characters." : null;

    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Writes the file preamble and returns a write-only stream that encrypts into <paramref name="output"/>. Dispose it to write the final chunk.</summary>
    public static Stream CreateEncryptingStream(Stream output, string passphrase, string headerJson, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var prefix = RandomNumberGenerator.GetBytes(PrefixSize);
        var header = Encoding.UTF8.GetBytes(headerJson);
        if (header.Length > MaxHeaderBytes) throw new InvalidOperationException("The backup header is too large.");
        using var preamble = new MemoryStream();
        preamble.Write(Magic);
        preamble.WriteByte(Version);
        Span<byte> int32 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(int32, iterations); preamble.Write(int32);
        preamble.Write(salt);
        preamble.Write(prefix);
        BinaryPrimitives.WriteInt32BigEndian(int32, header.Length); preamble.Write(int32);
        preamble.Write(header);
        var aad = preamble.ToArray();
        output.Write(aad);
        return new EncryptingStream(output, DeriveKey(passphrase, salt, iterations), prefix, aad);
    }

    /// <summary>The plain header of a backup file (not authenticated until a chunk is decrypted).</summary>
    public sealed record Preamble(int Iterations, byte[] Salt, byte[] Prefix, string HeaderJson, byte[] AssociatedData);

    public static Preamble ReadPreamble(Stream input)
    {
        var magic = ReadExactly(input, Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new TransferFormatException("NOT_A_BACKUP", "This is not a BirkNext backup file.");
        var version = ReadExactly(input, 1)[0];
        if (version != Version) throw new TransferFormatException("UNSUPPORTED_VERSION", $"Backup encryption format {version} is not supported by this build (supports {Version}).");
        var iterations = BinaryPrimitives.ReadInt32BigEndian(ReadExactly(input, 4));
        if (iterations is < MinIterations or > MaxIterations) throw new TransferFormatException("BAD_HEADER", "The key-derivation parameters are outside the accepted range.");
        var salt = ReadExactly(input, SaltSize);
        var prefix = ReadExactly(input, PrefixSize);
        var length = BinaryPrimitives.ReadInt32BigEndian(ReadExactly(input, 4));
        if (length is <= 0 or > MaxHeaderBytes) throw new TransferFormatException("BAD_HEADER", "The backup header length is invalid.");
        var header = ReadExactly(input, length);
        using var aad = new MemoryStream();
        aad.Write(Magic); aad.WriteByte(version);
        var it = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(it, iterations); aad.Write(it);
        aad.Write(salt); aad.Write(prefix);
        var len = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(len, length); aad.Write(len);
        aad.Write(header);
        return new Preamble(iterations, salt, prefix, Encoding.UTF8.GetString(header), aad.ToArray());
    }

    /// <summary>A read-only stream of the decrypted payload; throws <see cref="TransferAuthenticationException"/> on any authentication failure or truncation.</summary>
    public static Stream CreateDecryptingStream(Stream input, Preamble preamble, string passphrase) =>
        new DecryptingStream(input, DeriveKey(passphrase, preamble.Salt, preamble.Iterations), preamble.Prefix, preamble.AssociatedData);

    private static byte[] Nonce(byte[] prefix, uint counter, bool final)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(PrefixSize, 4), counter);
        nonce[11] = final ? (byte)1 : (byte)0;
        return nonce;
    }

    private static byte[] ReadExactly(Stream input, int count)
    {
        var buffer = new byte[count];
        try { input.ReadExactly(buffer); }
        catch (EndOfStreamException) { throw new TransferFormatException("TRUNCATED", "The backup file ends early."); }
        return buffer;
    }

    private sealed class EncryptingStream(Stream output, byte[] key, byte[] prefix, byte[] aad) : Stream
    {
        private readonly AesGcm _aes = new(key, TagSize);
        private readonly byte[] _buffer = new byte[ChunkSize];
        private int _filled;
        private uint _counter;
        private bool _closed;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                // A full buffer is flushed only when more data follows, so the last chunk is always the one written by Dispose (final flag).
                if (_filled == ChunkSize) EmitChunk(final: false);
                var take = Math.Min(ChunkSize - _filled, data.Length);
                data[..take].CopyTo(_buffer.AsSpan(_filled));
                _filled += take;
                data = data[take..];
            }
        }

        private void EmitChunk(bool final)
        {
            if (_counter == uint.MaxValue) throw new InvalidOperationException("The backup is too large for one encryption stream.");
            var cipher = new byte[_filled];
            var tag = new byte[TagSize];
            _aes.Encrypt(Nonce(prefix, _counter++, final), _buffer.AsSpan(0, _filled), cipher, tag, aad);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, _filled);
            output.Write(length); output.Write(cipher); output.Write(tag);
            CryptographicOperations.ZeroMemory(_buffer.AsSpan(0, _filled));
            _filled = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                EmitChunk(final: true);
                output.Flush();
                _aes.Dispose();
                CryptographicOperations.ZeroMemory(key);
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_closed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class DecryptingStream(Stream input, byte[] key, byte[] prefix, byte[] aad) : Stream
    {
        private readonly AesGcm _aes = new(key, TagSize);
        private byte[] _plain = [];
        private int _offset;
        private uint _counter;
        private bool _finalSeen;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> destination)
        {
            while (_offset == _plain.Length)
            {
                if (_finalSeen)
                {
                    if (input.ReadByte() != -1) throw new TransferAuthenticationException("Data found after the final chunk: the backup was altered.");
                    return 0;
                }
                NextChunk();
            }
            var take = Math.Min(destination.Length, _plain.Length - _offset);
            _plain.AsSpan(_offset, take).CopyTo(destination);
            _offset += take;
            return take;
        }

        private void NextChunk()
        {
            Span<byte> lengthBytes = stackalloc byte[4];
            var read = input.ReadAtLeast(lengthBytes, 4, throwOnEndOfStream: false);
            if (read == 0) throw new TransferAuthenticationException("The backup ends before its final chunk: it is truncated.");
            if (read < 4) throw new TransferAuthenticationException("The backup ends inside a chunk header.");
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length is < 0 or > ChunkSize) throw new TransferAuthenticationException("A chunk length is invalid: the backup was altered.");
            var cipher = new byte[length];
            var tag = new byte[TagSize];
            try { input.ReadExactly(cipher); input.ReadExactly(tag); }
            catch (EndOfStreamException) { throw new TransferAuthenticationException("The backup ends inside a chunk."); }
            var plain = new byte[length];
            // Try as a middle chunk, then as the final one: exactly one of them authenticates.
            if (TryDecrypt(cipher, tag, plain, final: false)) { }
            else if (TryDecrypt(cipher, tag, plain, final: true)) _finalSeen = true;
            else throw new TransferAuthenticationException("Decryption failed: wrong passphrase, or the backup was altered.");
            _counter++;
            _plain = plain;
            _offset = 0;
        }

        private bool TryDecrypt(byte[] cipher, byte[] tag, byte[] plain, bool final)
        {
            try { _aes.Decrypt(Nonce(prefix, _counter, final), cipher, tag, plain, aad); return true; }
            catch (AuthenticationTagMismatchException) { return false; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _aes.Dispose(); CryptographicOperations.ZeroMemory(key); }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>A malformed, unsupported or truncated transfer file. <see cref="Code"/> is stable for the UI and tests.</summary>
public sealed class TransferFormatException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Authentication failed: wrong passphrase or tampering (deliberately indistinguishable).</summary>
public sealed class TransferAuthenticationException(string message) : Exception(message);
