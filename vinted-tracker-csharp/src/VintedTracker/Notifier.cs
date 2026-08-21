using System.Text;
using System.Text.Json;

namespace VintedTracker;

/// <summary>
/// Powiadomienia o okazjach: zawsze konsola, a przy ustawionych zmiennych
/// środowiskowych także Discord webhook (<c>DISCORD_WEBHOOK_URL</c>)
/// i Telegram (<c>TELEGRAM_BOT_TOKEN</c> + <c>TELEGRAM_CHAT_ID</c>).
/// </summary>
public sealed class Notifier
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string? _discordWebhook = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
    private readonly string? _telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
    private readonly string? _telegramChatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");

    public static string FormatDeal(Listing listing, string gameTitle, DealVerdict verdict)
    {
        var label = verdict.Tier == DealTier.Strong ? "MOCNA OKAZJA" : "Okazja";
        var sb = new StringBuilder();
        sb.AppendLine($"🎮 {label} [{gameTitle}] (score {verdict.Score:0.#}): {listing.Title}");
        sb.AppendLine($"   {listing.Price:0.00} {listing.Currency} — {listing.Url}");
        foreach (var reason in verdict.Reasons)
            sb.AppendLine($"   • {reason}");
        return sb.ToString().TrimEnd();
    }

    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        Console.WriteLine(message);
        if (_discordWebhook is not null)
            await PostJsonAsync(_discordWebhook, new { content = Truncate(message, 1990) }, "Discord", ct);
        if (_telegramToken is not null && _telegramChatId is not null)
            await PostJsonAsync(
                $"https://api.telegram.org/bot{_telegramToken}/sendMessage",
                new { chat_id = _telegramChatId, text = Truncate(message, 4000) },
                "Telegram", ct);
    }

    private async Task PostJsonAsync(string url, object payload, string channel, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"[warn] {channel} nie zadziałał: {e.Message}");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
