using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace HxPushApp.Helpers
{
    /// <summary>
    /// 应用级 HTTP 请求帮助类。复用同一个 HttpClient，统一处理 JSON 和错误响应。
    /// </summary>
    public sealed class HttpClientHelper
    {
        private static readonly Lazy<HttpClientHelper> LazyInstance =
            new(() => new HttpClientHelper());

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient httpClient;

        private HttpClientHelper()
        {
            httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public static HttpClientHelper Instance => LazyInstance.Value;

        /// <summary>
        /// 发送 GET 请求并返回响应文本。
        /// </summary>
        public async Task<string> GetStringAsync(
            string requestUri,
            CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await SendAsync(request, cancellationToken);
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        /// <summary>
        /// 发送 GET 请求并将 JSON 响应反序列化为指定类型。
        /// </summary>
        public Task<TResponse> GetJsonAsync<TResponse>(
            string requestUri,
            CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            return SendJsonAndDisposeRequestAsync<TResponse>(request, cancellationToken);
        }

        /// <summary>
        /// 以 JSON 发送 POST 请求，并将 JSON 响应反序列化为指定类型。
        /// </summary>
        public Task<TResponse> PostJsonAsync<TRequest, TResponse>(
            string requestUri,
            TRequest body,
            CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(body, options: JsonOptions)
            };

            return SendJsonAndDisposeRequestAsync<TResponse>(request, cancellationToken);
        }

        /// <summary>
        /// 发送自定义请求并返回响应。调用方负责释放返回的 HttpResponseMessage。
        /// 可通过 HttpRequestMessage 添加 AppKey、Authorization 等请求头。
        /// </summary>
        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken = default)
        {
            var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var statusCode = response.StatusCode;
            var reasonPhrase = response.ReasonPhrase;
            response.Dispose();

            throw new HttpRequestException(
                $"HTTP 请求失败：{(int)statusCode} {reasonPhrase}。{errorContent}",
                inner: null,
                statusCode);
        }

        /// <summary>
        /// 发送自定义请求并将 JSON 响应反序列化为指定类型。
        /// 调用完成后会自动释放请求和响应对象。
        /// </summary>
        public Task<TResponse> SendJsonAsync<TResponse>(
            HttpRequestMessage request,
            CancellationToken cancellationToken = default)
        {
            return SendJsonAndDisposeRequestAsync<TResponse>(request, cancellationToken);
        }

        private async Task<TResponse> SendJsonAndDisposeRequestAsync<TResponse>(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using (request)
            using (var response = await SendAsync(request, cancellationToken))
            {
                var result = await response.Content.ReadFromJsonAsync<TResponse>(
                    JsonOptions,
                    cancellationToken);

                return result ?? throw new JsonException("HTTP 响应 JSON 为空或无法转换为目标类型。");
            }
        }
    }
}
