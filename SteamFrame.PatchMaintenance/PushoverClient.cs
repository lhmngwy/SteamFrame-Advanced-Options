using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SteamFrame.PatchMaintenance;

public sealed class PushoverClient(HttpClient http) : INotificationSender
{
    public async Task<DeliveryResult> ValidateAsync(PushoverCredentials credentials, string device, CancellationToken ct) =>
        await PostAsync("users/validate.json", credentials, device, null, DateTimeOffset.UtcNow, ct);

    public Task<DeliveryResult> SendAsync(PushoverCredentials credentials, string device, string message, DateTimeOffset at, CancellationToken ct) =>
        PostAsync("messages.json", credentials, device, message, at, ct);

    private async Task<DeliveryResult> PostAsync(string endpoint, PushoverCredentials credentials, string device, string? message, DateTimeOffset at, CancellationToken ct)
    {
        credentials.Validate();
        var fields = new Dictionary<string, string> { ["token"] = credentials.Token, ["user"] = credentials.User };
        if (device.Length > 0) fields["device"] = device;
        if (message != null)
        {
            fields["title"] = "SteamFrame driver patch maintenance";
            fields["message"] = message.Length > 1024 ? message[..1024] : message;
            fields["timestamp"] = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            fields["priority"] = "0";
        }
        try
        {
            using var content = new FormUrlEncodedContent(fields);
            using var response = await http.PostAsync("https://api.pushover.net/1/" + endpoint, content, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                DateTimeOffset? retry = null;
                if (response.Headers.TryGetValues("X-Limit-App-Reset", out var values) && long.TryParse(values.FirstOrDefault(), out var epoch)
                    && epoch > 0 && epoch < 253402300799) retry = DateTimeOffset.FromUnixTimeSeconds(epoch);
                return new(false, false, "Pushover quota exhausted", retry ?? DateTimeOffset.UtcNow.AddHours(24));
            }
            if ((int)response.StatusCode >= 500) return new(false, false, "Pushover temporarily unavailable");
            if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500) return new(false, true, "Pushover rejected the credentials or request; update notification settings");
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (response.IsSuccessStatusCode && body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number
                && status.TryGetInt32(out var code) && code == 1)
                return new(true, false, "Accepted by Pushover (device delivery is not verified)");
            return new(false, true, "Pushover rejected the request; validate notification settings");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        { return new(false, false, "Pushover connection failed; retry pending"); }
    }
}
