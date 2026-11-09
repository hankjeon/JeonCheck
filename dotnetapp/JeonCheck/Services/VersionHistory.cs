using System.Globalization;
using System.IO;
using System.Text.Json;

namespace JeonCheck.Services;

public sealed record VersionHistoryEntry(string Version, string? ReleaseDate, string[] Added, string[] Changed)
{
    public string ReleaseDateDisplay => ReleaseDate == null ? "출시일 미등록" : $"출시일: {ReleaseDate}";
}

public sealed record VersionHistoryResult(IReadOnlyList<VersionHistoryEntry> Entries, string Message);

public static class VersionHistory
{
    public static string DataFilePath => Path.Combine(AppContext.BaseDirectory, "data", "version_history.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class HistoryDocument
    {
        public List<VersionHistoryEntry>? Versions { get; set; }
    }

    // 캐시하지 않고 이력 창을 열 때마다 외부 JSON의 편집 내용을 반영한다.
    public static VersionHistoryResult Load(string? path = null)
    {
        path ??= DataFilePath;
        try
        {
            var document = JsonSerializer.Deserialize<HistoryDocument>(File.ReadAllText(path), JsonOptions);
            if (document?.Versions == null)
                throw new JsonException("versions 배열이 필요합니다.");
            foreach (var entry in document.Versions)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Version) || entry.Added == null || entry.Changed == null ||
                    entry.Added.Any(string.IsNullOrWhiteSpace) || entry.Changed.Any(string.IsNullOrWhiteSpace))
                    throw new JsonException("각 이력에는 version 문자열과 added·changed 문자열 배열이 필요합니다.");
                if (entry.ReleaseDate != null && !DateOnly.TryParseExact(entry.ReleaseDate, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw new JsonException($"{entry.Version}: releaseDate는 YYYY-MM-DD 형식 또는 null이어야 합니다.");
            }
            return new(document.Versions, document.Versions.Count == 0 ? "등록된 버전 이력이 없습니다." : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
        {
            return new(Array.Empty<VersionHistoryEntry>(), $"버전 이력을 불러오지 못했습니다.\n파일: {path}\n{ex.Message}");
        }
    }
}
