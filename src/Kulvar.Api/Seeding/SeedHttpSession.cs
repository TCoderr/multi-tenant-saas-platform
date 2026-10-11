using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kulvar.Api.Seeding;

/// <summary>
/// Demo seed'in API ile konuşan oturumu: tarayıcı gibi cookie ve CSRF token'ı taşır. Seed, uygulamanın kendi uçlarını kullanır;
/// veritabanına ayrıcalıklı yazma yapmaz, dolayısıyla tüm kurallar (RLS, roller, kota) seed verisi için de geçerlidir.
/// </summary>
public sealed class SeedHttpSession(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);
    private string? _csrf;

    /// <summary>İsteği gönderir. Hız sınırına (429) takılırsa sunucunun bildirdiği süre kadar bekleyip sınırlı sayıda yeniden dener.</summary>
    public async Task<(HttpStatusCode Status, JsonObject Body)> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default, params (string Name, string Value)[] headers)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (status, json, retryAfter) = await SendOnceAsync(method, path, body, ct, headers);
            if (status != HttpStatusCode.TooManyRequests || attempt >= 4)
            {
                return (status, json);
            }
            var wait = retryAfter is { } r && r > TimeSpan.Zero ? r : TimeSpan.FromSeconds(10);
            await Task.Delay(wait < TimeSpan.FromSeconds(65) ? wait + TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(65), ct);
        }
    }

    private async Task<(HttpStatusCode Status, JsonObject Body, TimeSpan? RetryAfter)> SendOnceAsync(HttpMethod method, string path, object? body, CancellationToken ct, (string Name, string Value)[] headers)
    {
        var safe = method == HttpMethod.Get;
        if (!safe && _csrf is null)
        {
            await RefreshCsrfAsync(ct);
        }
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }
        if (!safe)
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", _csrf);
        }
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (_cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        using var response = await http.SendAsync(request, ct);
        Absorb(response);
        var text = await response.Content.ReadAsStringAsync(ct);
        var json = string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text) as JsonObject ?? [];
        if (response.Headers.ETag is { } etag)
        {
            json["$etag"] = etag.Tag;
        }
        return (response.StatusCode, json, response.Headers.RetryAfter?.Delta);
    }

    public Task<(HttpStatusCode Status, JsonObject Body)> GetAsync(string path, CancellationToken ct = default) => SendAsync(HttpMethod.Get, path, null, ct);

    public Task<(HttpStatusCode Status, JsonObject Body)> PostAsync(string path, object? body = null, CancellationToken ct = default, params (string Name, string Value)[] headers) =>
        SendAsync(HttpMethod.Post, path, body, ct, headers);

    public async Task<bool> LoginAsync(string email, string password, CancellationToken ct)
    {
        var (status, _) = await PostAsync("/api/auth/login", new { email, password }, ct);
        if (status != HttpStatusCode.OK)
        {
            return false;
        }
        await RefreshCsrfAsync(ct); // Kimlik değişti.
        return true;
    }

    private async Task RefreshCsrfAsync(CancellationToken ct)
    {
        _csrf = null;
        var (status, body) = await GetAsync("/api/auth/csrf", ct);
        if (status != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"CSRF token'ı alınamadı ({(int)status}).");
        }
        _csrf = body["token"]!.GetValue<string>();
    }

    private void Absorb(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }
        foreach (var header in values)
        {
            var first = header.Split(';', 2)[0];
            var separator = first.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            var name = first[..separator].Trim();
            var value = first[(separator + 1)..];
            if (value.Length == 0 || header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
            {
                _cookies.Remove(name);
            }
            else
            {
                _cookies[name] = value;
            }
        }
    }
}
