using BirkNext.Api.Services.TestData;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>
/// The identities file is replaced atomically (write a temporary file, then move it over). On Windows that replace fails with "Access to the path is
/// denied" while another process holds the file without delete sharing — typically antivirus scanning a file just written (seen once in a full test
/// run). The hold lasts milliseconds, so the store retries briefly; a lasting hold still fails loudly and nothing is lost.
/// </summary>
public sealed class TestDataIdentityStoreReplaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-td-replace-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string FilePath => Path.Combine(_dir, "identities.json");

    [Fact]
    public async Task A_save_succeeds_when_another_process_briefly_holds_the_file()
    {
        var store = new TestDataIdentityStore(_dir);
        store.Clear(); // creates the file
        var hold = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read); // no delete sharing: the replace is refused while held
        var released = Task.Run(async () => { await Task.Delay(60); await hold.DisposeAsync(); });

        var save = () => store.Clear();

        save.Should().NotThrow("the replace is retried while the brief hold lasts");
        await released;
        File.Exists(FilePath + ".tmp").Should().BeFalse("the temporary file became the identities file");
    }

    [Fact]
    public void A_lasting_hold_still_fails_loudly()
    {
        var store = new TestDataIdentityStore(_dir);
        store.Clear();
        using var hold = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var save = () => store.Clear();

        save.Should().Throw<Exception>().Where(e => e is UnauthorizedAccessException || e is IOException, "a write that cannot be saved is never reported as saved");
    }
}
