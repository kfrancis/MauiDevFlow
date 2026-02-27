namespace SampleMauiApp;

public partial class NetworkTestPage : ContentPage
{
    private readonly IHttpClientFactory _httpClientFactory;

    public NetworkTestPage(IHttpClientFactory httpClientFactory)
    {
        InitializeComponent();
        _httpClientFactory = httpClientFactory;
    }

    private async void OnGetPosts(object? sender, EventArgs e)
    {
        await MakeRequest("GET", "https://jsonplaceholder.typicode.com/posts?_limit=5");
    }

    private async void OnGetUsers(object? sender, EventArgs e)
    {
        await MakeRequest("GET", "https://jsonplaceholder.typicode.com/users");
    }

    private async void OnCreatePost(object? sender, EventArgs e)
    {
        await MakeRequest("POST", "https://jsonplaceholder.typicode.com/posts",
            new StringContent(
                """{"title":"Test Post","body":"Hello from MauiDevFlow!","userId":1}""",
                System.Text.Encoding.UTF8, "application/json"));
    }

    private async void OnGet404(object? sender, EventArgs e)
    {
        await MakeRequest("GET", "https://jsonplaceholder.typicode.com/posts/99999");
    }

    private async void OnGetError(object? sender, EventArgs e)
    {
        await MakeRequest("GET", "https://this-host-does-not-exist.invalid/test");
    }

    private async Task MakeRequest(string method, string url, HttpContent? content = null)
    {
        StatusLabel.Text = $"⏳ {method} {url}...";
        ResultLabel.Text = "";

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (content != null) request.Content = content;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await client.SendAsync(request);
            sw.Stop();

            var body = await response.Content.ReadAsStringAsync();
            var preview = body.Length > 200 ? body[..200] + "..." : body;

            StatusLabel.Text = $"✅ {(int)response.StatusCode} {response.StatusCode} ({sw.ElapsedMilliseconds}ms)";
            ResultLabel.Text = preview;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"❌ Error: {ex.GetType().Name}";
            ResultLabel.Text = ex.Message;
        }
    }
}
