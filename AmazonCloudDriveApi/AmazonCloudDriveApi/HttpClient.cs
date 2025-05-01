// Модернизированная версия класса Azi.Tools.HttpClient с использованием System.Net.Http.HttpClient
// Интерфейсы и сигнатуры методов сохранены для совместимости

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Azi.Tools
{
    public interface IHttpWebResponse
    {
        Stream GetResponseStream();
        HttpStatusCode StatusCode { get; }
        string StatusDescription { get; }
        WebHeaderCollection Headers { get; }
    }
    internal class HttpClient
    {
        private const int RetryTimes = 3;
        private static readonly HttpClientHandler handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
        };
        private static readonly System.Net.Http.HttpClient client = new System.Net.Http.HttpClient(handler);

        private readonly Dictionary<int, WeakReference<Func<HttpStatusCode, Task<bool>>>> retryErrorProcessor = new();

        public HttpClient(Func<HttpWebRequest, Task> settingsSetter)
        {
            // settingsSetter не используется в HttpClient, оставлено для интерфейсной совместимости
        }

        public void AddRetryErrorProcessor(HttpStatusCode code, Func<HttpStatusCode, Task<bool>> func)
        {
            retryErrorProcessor[(int)code] = new WeakReference<Func<HttpStatusCode, Task<bool>>>(func);
        }

        public void AddRetryErrorProcessor(int code, Func<HttpStatusCode, Task<bool>> func)
        {
            retryErrorProcessor[code] = new WeakReference<Func<HttpStatusCode, Task<bool>>>(func);
        }

        public void RemoveRetryErrorProcessor(HttpStatusCode code) => retryErrorProcessor.Remove((int)code);
        public void RemoveRetryErrorProcessor(int code) => retryErrorProcessor.Remove(code);

        public async Task<T> GetJsonAsync<T>(string url)
        {
            return await Send<T>(HttpMethod.Get, url).ConfigureAwait(false);
        }

        public async Task<R> Post<P, R>(string url, P obj)
        {
            return await Send<P, R>(HttpMethod.Post, url, obj).ConfigureAwait(false);
        }

        public async Task<R> Patch<P, R>(string url, P obj)
        {
            var method = new HttpMethod("PATCH");
            return await Send<P, R>(method, url, obj).ConfigureAwait(false);
        }

        public async Task<R> PostForm<R>(string url, Dictionary<string, string> pars)
        {
            var content = new FormUrlEncodedContent(pars);
            var response = await client.PostAsync(url, content);
            return await ReadAsAsync<R>(response);
        }

        public async Task<R> Send<P, R>(HttpMethod method, string url, P obj)
        {
            var content = new StringContent(JsonConvert.SerializeObject(obj), Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(method, url) { Content = content };
            var response = await client.SendAsync(request);
            return await ReadAsAsync<R>(response);
        }

        public async Task<R> Send<R>(HttpMethod method, string url)
        {
            var request = new HttpRequestMessage(method, url);
            var response = await client.SendAsync(request);
            return await ReadAsAsync<R>(response);
        }

        public async Task<int> GetToBufferAsync(string url, byte[] buffer, int bufferIndex, long fileOffset, int length)
        {
            using var stream = new MemoryStream(buffer, bufferIndex, length);
            await GetToStreamAsync(url, stream, fileOffset, length).ConfigureAwait(false);
            return (int)stream.Position;
        }

        public async Task GetToStreamAsync(string url, Func<IHttpWebResponse, Task> streammer, long? fileOffset = null, long? length = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (fileOffset.HasValue && length.HasValue)
            {
                request.Headers.Range = new RangeHeaderValue(fileOffset.Value, fileOffset.Value + length.Value - 1);
            }
            else if (fileOffset.HasValue)
            {
                request.Headers.Range = new RangeHeaderValue(fileOffset.Value, null);
            }

            var responseMessage = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (!responseMessage.IsSuccessStatusCode)
            {
                await HandleBadResponse(responseMessage);
            }

            var stream = await responseMessage.Content.ReadAsStreamAsync();
            using var fakeResponse = new HttpWebResponseAdapter(responseMessage, stream);

            await streammer(fakeResponse);
        }

        public async Task GetToStreamAsync(string url, Stream destination, long? fileOffset = null, long? length = null, int bufferSize = 4096, Func<long, long> progress = null, CancellationToken cancellationToken = default)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (fileOffset.HasValue && length.HasValue)
            {
                request.Headers.Range = new RangeHeaderValue(fileOffset.Value, fileOffset.Value + length.Value - 1);
            }
            else if (fileOffset.HasValue)
            {
                request.Headers.Range = new RangeHeaderValue(fileOffset.Value, null);
            }

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                await HandleBadResponse(response);
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[bufferSize];
            long totalRead = 0;
            long nextProgress = 0;

            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;

                if (progress != null && totalRead >= nextProgress)
                {
                    nextProgress = progress(totalRead);
                }
            }

            if (progress != null && totalRead == 0)
            {
                progress(0);
            }
        }

        public async Task<R> SendFile<R>(HttpMethod method, string url, SendFileInfo file)
        {
            var content = new MultipartFormDataContent();

            if (file.Parameters != null)
            {
                foreach (var pair in file.Parameters)
                {
                    content.Add(new StringContent(pair.Value), pair.Key);
                }
            }

            using var fileStream = file.StreamOpener();
            var streamContent = new StreamContent(fileStream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(streamContent, file.FormName, file.FileName);

            var request = new HttpRequestMessage(method, url) { Content = content };
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, file.CancellationToken ?? CancellationToken.None);

            if (!response.IsSuccessStatusCode)
            {
                await HandleBadResponse(response);
            }

            var result = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<R>(result);
        }

        private async Task<R> ReadAsAsync<R>(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                await HandleBadResponse(response);
            }
            var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            return JsonConvert.DeserializeObject<R>(json);
        }

        private async Task HandleBadResponse(HttpResponseMessage response)
        {
            var status = response.StatusCode;
            if (retryErrorProcessor.TryGetValue((int)status, out var weakFunc))
            {
                if (weakFunc.TryGetTarget(out var func))
                {
                    var shouldRetry = await func(status);
                    if (shouldRetry) return;
                }
            }

            var content = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"HTTP {(int)status}: {content}");
        }
    }

    internal class HttpWebResponseAdapter : IHttpWebResponse, IDisposable
    {
        private readonly HttpResponseMessage message;
        private readonly Stream stream;

        public HttpWebResponseAdapter(HttpResponseMessage message, Stream stream)
        {
            this.message = message;
            this.stream = stream;
        }

        public Stream GetResponseStream() => stream;

        public HttpStatusCode StatusCode => message.StatusCode;

        public string StatusDescription => message.ReasonPhrase;

        public WebHeaderCollection Headers
        {
            get
            {
                var headers = new WebHeaderCollection();
                foreach (var header in message.Headers)
                {
                    headers[header.Key] = string.Join(", ", header.Value);
                }
                return headers;
            }
        }

        public void Dispose()
        {
            stream?.Dispose();
            message?.Dispose();
        }
    }
}
