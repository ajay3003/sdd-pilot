using Path = System.IO.Path;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

/// <summary>An uploaded archive read into memory, or why it could not be read. The client file name is metadata only.</summary>
public sealed record SourceArchiveUploadRead(byte[]? Bytes, string? FileName, int StatusCode, SourceArchiveValidationFailure? Failure)
{
    public bool IsRead => Bytes is not null && Failure is null;
}

/// <summary>
/// Reads one multipart ZIP upload into memory for Source Analysis and Project Import. The bytes come only from the uploaded stream
/// (<see cref="IFormFile.OpenReadStream"/>), bounded by <see cref="IqrSourceArchiveReader.MaxArchiveBytes"/> while reading; the client's
/// file name or path is never opened. Nothing is written to disk.
/// </summary>
public static class SourceArchiveUpload
{
    public const long RequestLimit = IqrSourceArchiveReader.MaxArchiveBytes + 64 * 1024;

    public static async Task<SourceArchiveUploadRead> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType) return Fail(StatusCodes.Status400BadRequest,
            new("UPLOAD_MULTIPART_REQUIRED", "upload", "Upload one ZIP file using multipart form data."));
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (InvalidDataException)
        {
            var tooLarge = request.ContentLength > RequestLimit;
            var failure = tooLarge
                ? new SourceArchiveValidationFailure("ARCHIVE_TOO_LARGE", "upload", "Upload exceeds the 50 MB compressed archive limit.", Actual: request.ContentLength, Limit: IqrSourceArchiveReader.MaxArchiveBytes)
                : new SourceArchiveValidationFailure("UPLOAD_INVALID_FORM", "upload", "The ZIP upload could not be read. Choose the file again and retry.");
            return Fail(tooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest, failure);
        }
        if (form.Files.Count != 1) return Fail(StatusCodes.Status400BadRequest,
            new("UPLOAD_FILE_COUNT_INVALID", "upload", "Choose exactly one ZIP archive to upload."));
        var file = form.Files[0];
        if (file.Length == 0) return Fail(StatusCodes.Status400BadRequest,
            new("ARCHIVE_EMPTY_UPLOAD", "upload", "The uploaded file is empty."));
        if (file.Length > IqrSourceArchiveReader.MaxArchiveBytes) return Fail(StatusCodes.Status413PayloadTooLarge,
            new("ARCHIVE_TOO_LARGE", "upload", "Upload exceeds the 50 MB compressed archive limit.", Actual: file.Length, Limit: IqrSourceArchiveReader.MaxArchiveBytes));
        using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > IqrSourceArchiveReader.MaxArchiveBytes) return Fail(StatusCodes.Status413PayloadTooLarge,
                new("ARCHIVE_TOO_LARGE", "upload", "Upload exceeds the 50 MB compressed archive limit.", Actual: buffer.Length + count, Limit: IqrSourceArchiveReader.MaxArchiveBytes));
            await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
        }
        // The client file name is display metadata: only its last segment is kept, never a client path.
        var name = Path.GetFileName((file.FileName ?? "").Replace('\\', '/'));
        return new(buffer.ToArray(), name, StatusCodes.Status200OK, null);
    }

    private static SourceArchiveUploadRead Fail(int status, SourceArchiveValidationFailure failure) => new(null, null, status, failure);
}
