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

    public async Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
    {
        Message response;
        try
        {
            response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _model,
                // Uyarlanabilir düşünme de bu sınıra dahildir; çeviri kısa olsa da kesilmesin.
                MaxTokens = 2048,
                System = system,
                // Altyazı çevirisi basit ve gecikmeye duyarlı: düşük efor. Haiku'da düşünme kapatılır (daha hızlı ve
                // ucuz; Haiku 5.5 buna düşük eforda izin verir). Opus 5.5'te düşünme kapatılamaz, düşük eforla sınırlanır.
                OutputConfig = new OutputConfig { Effort = Effort.Low },
                Thinking = _model == FastModel ? new ThinkingConfigDisabled() : null,
                Messages = [new UserMessageParam { Content = user }],
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (Classify(error) is { } state)
        {
            throw new ClaudeException(state, error.Message);
        }

        if (response.StopReason == "refusal") throw new ClaudeException(OnlineKeyState.Unreachable, ClaudeException.Refusal);
        return string.Concat(response.Content.Select(block => block.Value).OfType<TextBlock>().Select(text => text.Text));
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
