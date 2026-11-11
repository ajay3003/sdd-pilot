using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Services.WorkspaceTransfer;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.WorkspaceTransfer;

/// <summary>Encryption (AES-256-GCM STREAM) and the bounded transfer archive: every refusal path is asserted, not only the happy path.</summary>
public sealed class TransferCryptoAndArchiveTests
{
    private const string Pass = "correct horse battery staple";
    private const int FastIterations = TransferCrypto.MinIterations;

    private static byte[] Encrypt(byte[] payload, string header = "{\"format\":\"birknext-backup\"}", string passphrase = Pass)
    {
        using var output = new MemoryStream();
        using (var s = TransferCrypto.CreateEncryptingStream(output, passphrase, header, FastIterations)) s.Write(payload);
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] file, string passphrase = Pass)
    {
        using var input = new MemoryStream(file);
        var preamble = TransferCrypto.ReadPreamble(input);
        using var plain = TransferCrypto.CreateDecryptingStream(input, preamble, passphrase);
        using var result = new MemoryStream();
        plain.CopyTo(result);
        return result.ToArray();
    }

    private static int PreambleLength(byte[] file)
    {
        using var input = new MemoryStream(file);
        TransferCrypto.ReadPreamble(input);
        return (int)input.Position;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(TransferCrypto.ChunkSize)]
    [InlineData(TransferCrypto.ChunkSize * 2 + 17)]
    public void Round_trips_any_size_including_exact_chunk_multiples(int size)
    {
        var payload = RandomNumberGenerator.GetBytes(size);
        Decrypt(Encrypt(payload)).Should().Equal(payload);
    }

    [Fact]
    public void The_file_holds_no_plaintext_and_two_encryptions_differ()
    {
        var payload = Encoding.UTF8.GetBytes(new string('A', 4096) + "secret-marker");
        var a = Encrypt(payload);
        var b = Encrypt(payload);
        Encoding.UTF8.GetString(a).Should().NotContain("secret-marker");
        a.Should().NotEqual(b, "a random salt and nonce prefix are drawn per backup");
    }

    [Fact]
    public void A_wrong_passphrase_fails_authentication() =>
        FluentActions.Invoking(() => Decrypt(Encrypt([1, 2, 3]), "a different passphrase")).Should().Throw<TransferAuthenticationException>().WithMessage("*wrong passphrase*");

    [Fact]
    public void A_changed_header_fails_authentication()
    {
        var file = Encrypt([1, 2, 3], header: "{\"note\":\"aaaa\"}");
        var i = Encoding.UTF8.GetString(file).IndexOf("aaaa", StringComparison.Ordinal);
        file[i] = (byte)'b';
        FluentActions.Invoking(() => Decrypt(file)).Should().Throw<TransferAuthenticationException>();
    }

    [Fact]
    public void A_flipped_ciphertext_byte_fails_authentication()
    {
        var file = Encrypt(RandomNumberGenerator.GetBytes(1000));
        file[PreambleLength(file) + 4 + 10] ^= 0x01;
        FluentActions.Invoking(() => Decrypt(file)).Should().Throw<TransferAuthenticationException>();
    }

    [Fact]
    public void Truncation_reordering_and_appended_data_are_detected()
    {
        var payload = RandomNumberGenerator.GetBytes(TransferCrypto.ChunkSize * 2 + 5);
        var file = Encrypt(payload);
        var start = PreambleLength(file);
        var chunk = 4 + TransferCrypto.ChunkSize + 16;

        // Drop the final chunk: every remaining chunk authenticates as a middle chunk, but the final one never arrives.
        FluentActions.Invoking(() => Decrypt(file[..(start + 2 * chunk)])).Should().Throw<TransferAuthenticationException>().WithMessage("*truncated*");
        // Swap the first two chunks: the counter is part of the nonce.
        var swapped = (byte[])file.Clone();
        Array.Copy(file, start + chunk, swapped, start, chunk);
        Array.Copy(file, start, swapped, start + chunk, chunk);
        FluentActions.Invoking(() => Decrypt(swapped)).Should().Throw<TransferAuthenticationException>();
        // Anything after the final chunk.
        FluentActions.Invoking(() => Decrypt([.. file, 0x00])).Should().Throw<TransferAuthenticationException>().WithMessage("*after the final chunk*");
    }

    [Fact]
    public void Unsupported_format_and_bad_parameters_are_refused_before_any_decryption()
    {
        var file = Encrypt([1]);
        var other = (byte[])file.Clone(); other[0] = (byte)'X';
        FluentActions.Invoking(() => Decrypt(other)).Should().Throw<TransferFormatException>().Which.Code.Should().Be("NOT_A_BACKUP");
        var future = (byte[])file.Clone(); future[TransferCrypto.Magic.Length] = 2;
        FluentActions.Invoking(() => Decrypt(future)).Should().Throw<TransferFormatException>().Which.Code.Should().Be("UNSUPPORTED_VERSION");
        var weak = (byte[])file.Clone(); BinaryPrimitives.WriteInt32BigEndian(weak.AsSpan(TransferCrypto.Magic.Length + 1), 10);
        FluentActions.Invoking(() => Decrypt(weak)).Should().Throw<TransferFormatException>().Which.Code.Should().Be("BAD_HEADER");
        FluentActions.Invoking(() => Decrypt(file[..5])).Should().Throw<TransferFormatException>().Which.Code.Should().Be("TRUNCATED");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("short", false)]
    [InlineData("twelve chars", true)]
    public void Passphrases_have_a_minimum_length(string? passphrase, bool accepted) =>
        (TransferCrypto.PassphraseProblem(passphrase) is null).Should().Be(accepted);

    // ───────────── archive ─────────────

    private static readonly TransferArchiveLimits Limits = new(50, 4 << 20, 16 << 20, 100, ["manifest.json", "files/", "integrity/"]);

    private static async Task<MemoryStream> WrittenArchive(params (string Path, byte[] Content)[] entries)
    {
        var output = new MemoryStream();
        using (var w = new TransferArchiveWriter(output))
        {
            foreach (var (path, content) in entries) await w.AddAsync(path, s => s.WriteAsync(content).AsTask());
            await w.SealAsync(TransferJson.Options);
        }
        output.Position = 0;
        return output;
    }

    private static MemoryStream RawZip(Action<ZipArchive> build)
    {
        var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true)) build(zip);
        output.Position = 0;
        return output;
    }

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(content);
    }

    [Fact]
    public async Task Written_archives_verify_and_every_entry_is_hashed()
    {
        using var stream = await WrittenArchive(("manifest.json", "{}"u8.ToArray()), ("files/a/b.json", "[1]"u8.ToArray()));
        using var reader = TransferArchiveReader.Open(stream, Limits);
        (await reader.VerifyHashesAsync(TransferJson.Options, default)).Should().BeEmpty();
        reader.Paths.Should().BeEquivalentTo("manifest.json", "files/a/b.json", TransferArchiveReader.HashesPath);
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("files/../../escape.json")]
    [InlineData("/files/rooted.json")]
    [InlineData("files\\windows.json")]
    [InlineData("C:/files/drive.json")]
    [InlineData("files//empty-segment.json")]
    [InlineData("files/./dot.json")]
    public void Unsafe_paths_are_refused(string name)
    {
        using var stream = RawZip(z => Add(z, name, [1]));
        FluentActions.Invoking(() => TransferArchiveReader.Open(stream, Limits)).Should().Throw<TransferFormatException>().Which.Code.Should().Be("BAD_PATH");
    }

    [Fact]
    public void Unexpected_duplicate_symlink_and_oversized_entries_are_refused()
    {
        using (var s = RawZip(z => Add(z, "scripts/run.ps1", [1])))
            FluentActions.Invoking(() => TransferArchiveReader.Open(s, Limits)).Should().Throw<TransferFormatException>().Which.Code.Should().Be("UNEXPECTED_ENTRY");
        using (var s = RawZip(z => { Add(z, "files/A.json", [1]); Add(z, "files/a.json", [2]); }))
            FluentActions.Invoking(() => TransferArchiveReader.Open(s, Limits)).Should().Throw<TransferFormatException>().Which.Code.Should().Be("DUPLICATE_ENTRY");
        using (var s = RawZip(z => { var e = z.CreateEntry("files/link"); e.ExternalAttributes = unchecked((int)(0xA1FFu << 16)); using var w = e.Open(); w.Write("target"u8); }))
            FluentActions.Invoking(() => TransferArchiveReader.Open(s, Limits)).Should().Throw<TransferFormatException>().Which.Code.Should().Be("SYMLINK_ENTRY");
        using (var s = RawZip(z => Add(z, "files/big.bin", RandomNumberGenerator.GetBytes(5 << 20))))
            FluentActions.Invoking(() => TransferArchiveReader.Open(s, Limits)).Should().Throw<TransferFormatException>().Which.Code.Should().Be("ENTRY_TOO_LARGE");
        using (var s = RawZip(z => { for (var i = 0; i < 51; i++) Add(z, $"files/{i}.json", [1]); }))
            FluentActions.Invoking(() => TransferArchiveReader.Open(s, Limits)).Should().Throw<TransferFormatException>().Which.Code.Should().Be("TOO_MANY_ENTRIES");
    }

    [Fact]
    public void A_zip_bomb_is_refused_by_its_compression_ratio()
    {
        using var s = RawZip(z => Add(z, "files/zeros.bin", new byte[3 << 20]));
        FluentActions.Invoking(() => TransferArchiveReader.Open(s, Limits)).Should().Throw<TransferFormatException>().Which.Code.Should().Be("COMPRESSION_RATIO");
    }

    [Fact]
    public async Task Tampered_unlisted_and_missing_entries_fail_the_hash_check()
    {
        using var original = await WrittenArchive(("manifest.json", "{\"a\":1}"u8.ToArray()));
        var hashes = Encoding.UTF8.GetString(await ReadEntry(original, TransferArchiveReader.HashesPath));

        using (var tampered = RawZip(z => { Add(z, "manifest.json", "{\"a\":2}"u8.ToArray()); Add(z, TransferArchiveReader.HashesPath, Encoding.UTF8.GetBytes(hashes)); }))
        using (var r = TransferArchiveReader.Open(tampered, Limits))
            (await r.VerifyHashesAsync(TransferJson.Options, default)).Should().ContainSingle().Which.Should().Contain("does not match");

        using (var extra = RawZip(z => { Add(z, "manifest.json", "{\"a\":1}"u8.ToArray()); Add(z, "files/smuggled.json", [1]); Add(z, TransferArchiveReader.HashesPath, Encoding.UTF8.GetBytes(hashes)); }))
        using (var r = TransferArchiveReader.Open(extra, Limits))
            (await r.VerifyHashesAsync(TransferJson.Options, default)).Should().ContainSingle().Which.Should().Contain("not listed");

        using (var none = RawZip(z => Add(z, "manifest.json", [1])))
        using (var r = TransferArchiveReader.Open(none, Limits))
            (await r.VerifyHashesAsync(TransferJson.Options, default)).Should().ContainSingle().Which.Should().Contain("missing");
    }

    [Fact]
    public void A_non_zip_is_refused()
    {
        using var s = new MemoryStream("not a zip"u8.ToArray());
        FluentActions.Invoking(() => TransferArchiveReader.Open(s, Limits)).Should().Throw<TransferFormatException>().Which.Code.Should().Be("NOT_A_ZIP");
    }

    private static async Task<byte[]> ReadEntry(Stream zip, string name)
    {
        zip.Position = 0;
        using var a = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        await using var s = a.GetEntry(name)!.Open();
        using var m = new MemoryStream();
        await s.CopyToAsync(m);
        zip.Position = 0;
        return m.ToArray();
    }
}
