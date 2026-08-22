using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace VintedTracker;

/// <summary>Werdykt AI po obejrzeniu zdjęcia oferty.</summary>
public sealed record VisionVerdict(bool IsMatch, bool Complete, string Note);

/// <summary>
/// Weryfikacja ofert po zdjęciach (Claude API) — domyka to, czego filtr
/// tekstowy nie widzi: samo pudełko na zdjęciu, zła platforma na okładce,
/// merch bez słowa-klucza w żadnym języku. Wywoływana tylko w punkcie
/// decyzji (kandydat na alert, kandydat "najtańszej teraz"), więc to
/// kilkadziesiąt tanich zapytań dziennie, nie firehose.
///
/// Opcjonalna: włącza ją obecność zmiennej ANTHROPIC_API_KEY. Fail-open —
/// błąd API nigdy nie blokuje trackera, najwyżej wraca zachowanie bez AI.
/// </summary>
public sealed class VisionVerifier
{
    private readonly AnthropicClient? _client;
    private readonly string _model;

    public VisionVerifier(string model)
    {
        _model = model;
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        _client = string.IsNullOrWhiteSpace(key) ? null : new AnthropicClient { ApiKey = key };
    }

    public bool Enabled => _client is not null;

    public async Task<VisionVerdict?> VerifyAsync(
        string gameTitle, string listingTitle, string? photoUrl, CancellationToken ct)
    {
        if (_client is null || string.IsNullOrEmpty(photoUrl))
            return null;
        try
        {
            var response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                MaxTokens = 300,
                Messages =
                [
                    new()
                    {
                        Role = Role.User,
                        Content = new List<ContentBlockParam>
                        {
                            new ImageBlockParam { Source = new UrlImageSource { Url = photoUrl } },
                            new TextBlockParam { Text = BuildPrompt(gameTitle, listingTitle) },
                        },
                    },
                ],
            }, cancellationToken: ct);

            var text = string.Concat(response.Content
                .Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            var verdict = ParseVerdict(text);
            if (verdict is null)
                Log.Warn($"Vision: nieparsowalna odpowiedź dla \"{listingTitle}\": {text}");
            return verdict;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Vision nie zadziałał dla \"{listingTitle}\": {e.Message}");
            return null;
        }
    }

    internal static string BuildPrompt(string gameTitle, string listingTitle) =>
        $$"""
        Weryfikujesz ofertę z Vinted na podstawie zdjęcia.
        Szukana gra: "{{gameTitle}}" — fizyczne wydanie na Nintendo Switch.
        Tytuł ogłoszenia: "{{listingTitle}}"

        Oceń po zdjęciu:
        - is_match: czy zdjęcie pokazuje fizyczny egzemplarz TEJ gry na WŁAŚCIWĄ
          platformę (nie merch, nie samo puste pudełko, nie karty, nie inną grę,
          nie wydanie na inną konsolę, nie wersję z japońską okładką)?
        - complete: czy wygląda na kompletny zestaw (pudełko z grą)?
        - note: krótkie uzasadnienie po polsku (maks. 12 słów).

        Odpowiedz WYŁĄCZNIE JSON-em: {"is_match": bool, "complete": bool, "note": "..."}
        """;

    internal static VisionVerdict? ParseVerdict(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            return new VisionVerdict(
                IsMatch: root.TryGetProperty("is_match", out var m) && m.GetBoolean(),
                Complete: root.TryGetProperty("complete", out var c) && c.GetBoolean(),
                Note: root.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "");
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
