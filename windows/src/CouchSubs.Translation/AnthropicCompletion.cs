// SPDX-License-Identifier: AGPL-3.0-only

using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using CouchSubs.Core;

namespace CouchSubs.Translation;

/// <summary>Claude'a resmî Anthropic SDK'sıyla istek. Anahtar kullanıcının; yalnız şifreli depoda durur.</summary>
public sealed class AnthropicCompletion : IClaudeCompletion
{
    /// <summary>Ayarlardaki model seçenekleri: kalite (Opus) ya da hız ve düşük maliyet (Haiku).</summary>
    public const string QualityModel = "claude-opus-5-5";
    public const string FastModel = "claude-haiku-5-5";

    private readonly AnthropicClient _client;
    private readonly string _model;

    public AnthropicCompletion(string apiKey, string model)
    {
        _model = model;
        _client = MakeClient(apiKey);
    }

    private static AnthropicClient MakeClient(string apiKey) => new()
    {
        ApiKey = apiKey.Trim(),
        // Altyazı birkaç saniye sonra işe yaramaz: yeniden deneme yok, kısa zaman aşımı.
        MaxRetries = 0,
        Timeout = TimeSpan.FromSeconds(8),
    };

    public string? LastUsage { get; private set; }

    public Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken) =>
        SendAsync(system, user, null, cancellationToken);

    public Task<string> CompleteJsonAsync(string system, string user, string schema, CancellationToken cancellationToken) =>
        SendAsync(system, user, schema, cancellationToken);

    private async Task<string> SendAsync(string system, string user, string? schema, CancellationToken cancellationToken)
    {
        Message response;
        try
        {
            response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                // Uyarlanabilir düşünme de bu sınıra dahildir; çeviri kısa olsa da kesilmesin.
                MaxTokens = 2048,
                // Sistem talimatı her istekte aynı: önbellekten okunur (Haiku 5.5'te en az 512 token). Her satırda
                // yaklaşık 2000 token baştan işleniyordu; önbellekle yanıt daha erken başlar, istek de ucuzlar.
                System = new List<TextBlockParam>
                {
                    new() { Text = system, CacheControl = new CacheControlEphemeral() },
                },
                // Altyazı çevirisi basit ve gecikmeye duyarlı: düşük efor. Haiku'da düşünme kapatılır (daha hızlı ve
                // ucuz; Haiku 5.5 buna düşük eforda izin verir). Opus 5.5'te düşünme kapatılamaz, düşük eforla sınırlanır.
                // Dublajda yanıt verilen şemaya uyan JSON'dur (yapılandırılmış çıktı).
                OutputConfig = new OutputConfig
                {
                    Effort = Effort.Low,
                    Format = schema is null ? null : new JsonOutputFormat { Schema = SchemaOf(schema) },
                },
                Thinking = _model == FastModel ? new ThinkingConfigDisabled() : null,
                Messages = [new UserMessageParam { Content = user }],
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (Classify(error) is { } state)
        {
            throw new ClaudeException(state, error.Message);
        }

        var used = response.Usage;
        LastUsage = $"giriş {used.InputTokens}, önbellekten {used.CacheReadInputTokens ?? 0}, önbelleğe {used.CacheCreationInputTokens ?? 0}, çıktı {used.OutputTokens}";
        if (response.StopReason == "refusal") throw new ClaudeException(OnlineKeyState.Unreachable, ClaudeException.Refusal);
        return string.Concat(response.Content.Select(block => block.Value).OfType<TextBlock>().Select(text => text.Text));
    }

    /// <summary>
    /// Sessizlikte bağlantıyı ve önbelleği sıcak tutar: çıktısız (max_tokens 0) istek sistem talimatını önbellekte
    /// tazeler, bağlantı da açık kalır. Önbellek 5 dakikada siliniyor, bağlantı da boşta kapanıyordu; uzun sessizlikten
    /// sonraki ilk satır ~2 sn sürüyordu. Ayarlar gerçek istekle aynı olmalı (düşünme ve efor önbellek anahtarına girer).
    /// </summary>
    public async Task WarmAsync(string system, CancellationToken cancellationToken)
    {
        await _client.Messages.Create(new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 0,
            System = new List<TextBlockParam>
            {
                new() { Text = system, CacheControl = new CacheControlEphemeral() },
            },
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Thinking = _model == FastModel ? new ThinkingConfigDisabled() : null,
            Messages = [new UserMessageParam { Content = "." }],
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>JSON şema metni → SDK'nın beklediği sözlük.</summary>
    private static Dictionary<string, System.Text.Json.JsonElement> SchemaOf(string schema)
    {
        using var document = System.Text.Json.JsonDocument.Parse(schema);
        return document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
    }

    /// <summary>Ayarlardaki "Kaydet ve dene": anahtar geçerli mi, seçilen model kullanılabiliyor mu.</summary>
    public static async Task<OnlineKeyCheck> CheckAsync(string apiKey, string model, CancellationToken cancellationToken = default)
    {
        if (apiKey.Trim().Length == 0) return new(OnlineKeyState.Invalid);
        try
        {
            await MakeClient(apiKey).Models.Retrieve(model, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new(OnlineKeyState.Valid);
        }
        catch (Exception error) when (Classify(error) is { } state)
        {
            return new(state);
        }
    }

    /// <summary>SDK hatası → kullanıcıya gösterilecek durum; tanınmayan hatalar için null.</summary>
    internal static OnlineKeyState? Classify(Exception error) => error switch
    {
        AnthropicUnauthorizedException or AnthropicForbiddenException or AnthropicNotFoundException => OnlineKeyState.Invalid,
        AnthropicRateLimitException => OnlineKeyState.Unreachable,
        // 402 billing_error: hesapta kredi kalmadı.
        Anthropic4xxException e when $"{e.Type}".Contains("billing", StringComparison.OrdinalIgnoreCase) => OnlineKeyState.QuotaExceeded,
        Anthropic4xxException => OnlineKeyState.Invalid,
        Anthropic5xxException or AnthropicIOException or HttpRequestException or TaskCanceledException => OnlineKeyState.Unreachable,
        AnthropicApiException => OnlineKeyState.Unreachable,
        _ => null,
    };
}
