using System.Diagnostics;
namespace BirkNext.Api.Services.FrontendPassiveSecurity;

public sealed record ZapProcessResult(int ExitCode, string Output, string Error, bool TimedOut = false, bool Cancelled = false);
public interface IZapProcessRunner { Task<ZapProcessResult> RunAsync(string file, IReadOnlyList<string> args, int timeoutMs, CancellationToken cancellationToken); }

public sealed class ZapProcessRunner : IZapProcessRunner
{
    public async Task<ZapProcessResult> RunAsync(string file, IReadOnlyList<string> args, int timeoutMs, CancellationToken ct)
    {
        using var p = new Process { StartInfo = new(file) { RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false, CreateNoWindow=true } };
        foreach (var arg in args) p.StartInfo.ArgumentList.Add(arg);
        try
        {
            p.Start(); var output=p.StandardOutput.ReadToEndAsync(); var error=p.StandardError.ReadToEndAsync();
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(timeoutMs);
            await p.WaitForExitAsync(timeout.Token); return new(p.ExitCode,await output,await error);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            return new(-1,"","Process terminated.",TimedOut:!ct.IsCancellationRequested,Cancelled:ct.IsCancellationRequested);
        }
        catch (Exception ex) { try { if (!p.HasExited) p.Kill(true); } catch { } return new(-1,"",ex.Message); }
    }
}
