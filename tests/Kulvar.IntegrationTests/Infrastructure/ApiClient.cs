using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kulvar.IntegrationTests.Infrastructure;

/// <summary>
/// Tarayıcı gibi davranan test istemcisi: cookie kavanozunu kendisi yönetir (kopyalanabilir) ve güvensiz isteklerde CSRF başlığını ekler.
/// </summary>
public sealed class ApiClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Dictionary<string, string> Cookies { get; private set; } = new(StringComparer.Ordinal);
    public string? CsrfToken { get; set; }
    public bool AutoCsrf { get; set; } = true;
    public HttpClient Http => http;

    /// <summary>Aynı cookie'leri taşıyan bağımsız istemci ("kopyalanmış cookie" ve "ikinci sekme" senaryoları).</summary>
    public ApiClient Clone() => new(http) { Cookies = new Dictionary<string, string>(Cookies, StringComparer.Ordinal), CsrfToken = CsrfToken };

    public Task<HttpResponseMessage> GetAsync(string path, params (string Name, string Value)[] headers) =>
        SendAsync(HttpMethod.Get, path, null, headers);

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null, params (string Name, string Value)[] headers) =>
        SendAsync(HttpMethod.Post, path, body, headers);

    public Task<HttpResponseMessage> PutAsync(string path, object? body, params (string Name, string Value)[] headers) =>
        SendAsync(HttpMethod.Put, path, body, headers);

    public Task<HttpResponseMessage> PatchAsync(string path, object? body, params (string Name, string Value)[] headers) =>
        SendAsync(HttpMethod.Patch, path, body, headers);

    public Task<HttpResponseMessage> DeleteAsync(string path, params (string Name, string Value)[] headers) =>
        SendAsync(HttpMethod.Delete, path, null, headers);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, params (string Name, string Value)[] headers)
    {
        var safe = method == HttpMethod.Get || method == HttpMethod.Head;
        if (!safe && AutoCsrf && CsrfToken is null)
        {
            await RefreshCsrfAsync();
        }

        using var request = new HttpRequestMessage(method, path);
        if (body is string raw)
        {
            request.Content = new StringContent(raw, Encoding.UTF8, "application/json");
        }
        else if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), new MediaTypeHeaderValue("application/json"), Json);
        }
        if (!safe && CsrfToken is not null)
        {
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", CsrfToken);
        }
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
        if (Cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        var response = await http.SendAsync(request);
        Absorb(response);
        return response;
    }

    public async Task RefreshCsrfAsync()
    {
        CsrfToken = null;
        using var response = await SendAsync(HttpMethod.Get, "/api/auth/csrf", null);
        response.EnsureSuccessStatusCode();
        CsrfToken = (await response.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();
    }

    public async Task<HttpResponseMessage> LoginAsync(string email, string password)
    {
        var response = await PostAsync("/api/auth/login", new { email, password });
        if (response.IsSuccessStatusCode)
        {
            await RefreshCsrfAsync(); // Kimlik değişti: token yeniden alınır.
        }
        return response;
    }

    public async Task<HttpResponseMessage> LogoutAsync()
    {
        var response = await PostAsync("/api/auth/logout");
        CsrfToken = null;
        return response;
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
            var expired = header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase) || value.Length == 0;
            if (expired)
            {
                Cookies.Remove(name);
            }
            else
            {
                Cookies[name] = value;
            }
        }
    }
}

public static class HttpResponseExtensions
{
    /// <summary>Beklenmeyen yanıtta test çıktısına eklenecek sunucu tanılaması (son hata logları). Ortam fixture'ı ayarlar.</summary>
    public static Func<string>? Diagnostics { get; set; }

    private static string Describe(HttpStatusCode expected, HttpResponseMessage response, string text) =>
        $"Beklenen {(int)expected}, gelen {(int)response.StatusCode}: {text}" + ((int)response.StatusCode >= 500 && Diagnostics is not null ? $"\n--- sunucu logu ---\n{Diagnostics()}" : string.Empty);

    public static async Task<JsonObject> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text)?.AsObject() ?? throw new InvalidOperationException($"JSON nesnesi bekleniyordu: {text}");
    }

    public static async Task<JsonObject> ShouldBeAsync(this Task<HttpResponseMessage> pending, HttpStatusCode expected)
    {
        var response = await pending;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, Describe(expected, response, text));
        return string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>ProblemDetails yanıtını durum kodu ve <c>code</c> alanıyla doğrular.</summary>
    public static async Task<JsonObject> ShouldBeProblemAsync(this Task<HttpResponseMessage> pending, HttpStatusCode expected, string code)
    {
        var response = await pending;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"[{code}] " + Describe(expected, response, text));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(code, body["code"]?.GetValue<string>());
        return body;
    }

    public static string ETag(this HttpResponseMessage response) =>
        response.Headers.ETag?.Tag ?? throw new InvalidOperationException("ETag başlığı yok.");

    public static Guid Guid(this JsonNode? node) => System.Guid.Parse(node!.GetValue<string>());
    public static string Str(this JsonNode? node) => node!.GetValue<string>();
    public static int Int(this JsonNode? node) => node!.GetValue<int>();
}
