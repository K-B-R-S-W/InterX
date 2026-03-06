using System.Text;
using System.Text.Json;
using Newtonsoft.Json;

namespace MeetingAssistant;

/// <summary>
/// Service to interact with Cerebras AI API for AI responses
/// </summary>
public class GeminiApiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _apiUrl;
    private readonly string _model;
    private readonly string _systemPrompt;

    public event EventHandler<string>? TokenReceived;
    public event EventHandler<string>? ResponseCompleted;
    public event EventHandler<string>? ErrorOccurred;

    public GeminiApiService(string apiKey, string apiUrl, string model, string systemPrompt)
    {
        _apiKey = apiKey;
        _apiUrl = apiUrl;
        _model = model;
        _systemPrompt = systemPrompt;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    /// <summary>
    /// Send a question to Cerebras AI and stream the response
    /// </summary>
    public async Task<bool> SendQuestionAsync(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return false;

        Console.WriteLine($"[AI] Sending question: {question}");

        try
        {
            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = _systemPrompt },
                    new { role = "user", content = question }
                },
                temperature = 0,    // Greedy decoding: fastest inference, most accurate for Q&A
                max_tokens = 100,
                stream = true
            };

            var json = JsonConvert.SerializeObject(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl)
            {
                Content = content
            };

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            Console.WriteLine($"[AI] Response status: {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[AI] API Error: {response.StatusCode} - {errorContent}");
                ErrorOccurred?.Invoke(this, $"API Error: {response.StatusCode}");
                return false;
            }

            await ProcessStreamAsync(response);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Exception: {ex.Message}");
            ErrorOccurred?.Invoke(this, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Process the streaming response from Cerebras (OpenAI-compatible format)
    /// </summary>
    private async Task ProcessStreamAsync(HttpResponseMessage response)
    {
        var fullResponse = new StringBuilder();
        int lineCount = 0;
        int tokenCount = 0;

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            lineCount++;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                continue;

            var jsonData = line.Substring(6); // Remove "data: " prefix

            if (jsonData == "[DONE]")
            {
                Console.WriteLine("[AI] Stream completed with [DONE]");
                break;
            }

            try
            {
                using var doc = JsonDocument.Parse(jsonData);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0)
                {
                    var delta = choices[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var contentEl)
                        && contentEl.ValueKind == JsonValueKind.String)
                    {
                        var content = contentEl.GetString();
                        if (!string.IsNullOrEmpty(content))
                        {
                            tokenCount++;
                            fullResponse.Append(content);
                            TokenReceived?.Invoke(this, content);
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                Console.WriteLine($"[AI] JSON parsing error: {ex.Message}");
            }
        }

        Console.WriteLine($"[AI] Stream processing complete. Lines: {lineCount}, Tokens: {tokenCount}");
        var completeResponse = fullResponse.ToString();
        Console.WriteLine($"[AI] Complete response ({completeResponse.Length} chars): {completeResponse.Substring(0, Math.Min(100, completeResponse.Length))}...");
        ResponseCompleted?.Invoke(this, completeResponse);
    }

    /// <summary>
    /// Test the API connection
    /// </summary>
    public async Task<bool> TestConnectionAsync()
    {
        Console.WriteLine("[AI] Testing API connection...");
        
        try
        {
            var testQuestion = "Say 'OK' if you can hear me.";
            return await SendQuestionAsync(testQuestion);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI] Connection test failed: {ex.Message}");
            return false;
        }
    }
}
