using System.Text.Json;
using System.Text.Json.Serialization;
using KeepPassword.Core.Autofill;

namespace KeepPassword.Core.Messaging;

public static class NativeMessagingDefaults
{
    public const string HostName = "com.keeppassword.host";
    public const string SessionFileName = "autofill-session.json";
}

public sealed class AutofillResponse
{
    public string Type { get; init; } = "";

    public IReadOnlyList<AutofillCandidate>? Candidates { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string? Message { get; init; }

    public static AutofillResponse Locked() => new() { Type = "locked" };

    public static AutofillResponse NoMatch() => new() { Type = "noMatch" };

    public static AutofillResponse Cancelled() => new() { Type = "cancelled" };

    public static AutofillResponse Error(string message) => new() { Type = "error", Message = message };

    public static AutofillResponse CandidatesList(IReadOnlyList<AutofillCandidate> candidates) =>
        new() { Type = "candidates", Candidates = candidates };

    public static AutofillResponse Fill(string username, string password) =>
        new() { Type = "fill", Username = username, Password = password };
}

public static class AutofillProtocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static bool TryParseDiscover(string json, out AutofillRequest request)
    {
        request = new AutofillRequest();
        DiscoverDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<DiscoverDto>(json, Options);
        }
        catch (JsonException)
        {
            return false;
        }

        if (dto is null || !IsSupportedType(dto.Type))
        {
            return false;
        }

        request = new AutofillRequest
        {
            Operation = dto.Type!.ToLowerInvariant(),
            Origin = AutofillOrigin.Browser,
            Url = dto.Url,
            WindowTitle = dto.Title,
            ClientToken = dto.Token,
            EntryId = dto.EntryId,
            AccountId = dto.AccountId,
            ShortKey = dto.ShortKey
        };
        return true;
    }

    public static string Serialize(AutofillResponse response) =>
        JsonSerializer.Serialize(response, Options);

    private static bool IsSupportedType(string? type) =>
        string.Equals(type, "discover", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "discover-candidates", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "fill-candidate", StringComparison.OrdinalIgnoreCase);

    private sealed class DiscoverDto
    {
        public string? Type { get; set; }

        public string? Url { get; set; }

        public string? Title { get; set; }

        public string? Token { get; set; }

        public Guid? EntryId { get; set; }

        public Guid? AccountId { get; set; }

        public string? ShortKey { get; set; }
    }
}
