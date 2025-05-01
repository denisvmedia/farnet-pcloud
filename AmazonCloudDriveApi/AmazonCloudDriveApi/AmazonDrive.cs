// <copyright file="AmazonDrive.cs" company="Rambalac">
// Copyright (c) Rambalac. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Net;
using System.Net.Cache;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Azi.Amazon.CloudDrive.JsonObjects;
using Azi.Tools;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Azi.Amazon.CloudDrive
{
    /// <summary>
    /// Root class for Amazon Cloud Drive API
    /// </summary>
    public sealed partial class AmazonDrive : IAmazonAccount, IAmazonFiles, IAmazonNodes, IAmazonDrive
    {
        private const string LoginUrlBase = "https://my.pcloud.com/oauth2/authorize";
        private const string TokenUrl = "https://api.pcloud.com/oauth2_token";
        private const string ApiUrl = "https://api.pcloud.com";
        private static readonly TimeSpan GeneralExpiration = TimeSpan.FromMinutes(5);

        private static readonly byte[] DefaultCloseTabResponse = Encoding.UTF8.GetBytes("<SCRIPT>alert(\"window.close\");</SCRIPT>You can close this tab");

        private static readonly string DefaultOpenAuthResponse = "<SCRIPT>var win=window.open('{0}', '_blank');var id=setInterval(function(){{if (win.closed||win.location.href.indexOf('localhost')>=0){{clearInterval(id);/*win.close(); window.close();*/}}}}, 500);</SCRIPT>start";

        private static RequestCachePolicy standartCache = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);

        private readonly Azi.Tools.HttpClient http;
        // private readonly System.Net.Http.HttpClient http;

        private string clientId;
        private string clientSecret;
        private AuthToken token;

        private WeakReference<ITokenUpdateListener> weakOnTokenUpdate = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="AmazonDrive"/> class.
        /// </summary>
        /// <param name="clientId">Your Application ClientID. From Amazon Developers Console.</param>
        /// <param name="clientSecret">Your Application Secret. From Amazon Developers Console.</param>
        public AmazonDrive(string clientId, string clientSecret, AuthToken token = null)
        {
            this.clientSecret = clientSecret;
            this.clientId = clientId;
            this.token = token;
            http = new Azi.Tools.HttpClient(SettingsSetter);
        }

        /// <inheritdoc/>
        public int ListenerPortStart { get; set; } = 45674;

        /// <inheritdoc/>
        public IAmazonAccount Account => this;

        /// <inheritdoc/>
        public IAmazonFiles Files => this;

        /// <inheritdoc/>
        public IAmazonNodes Nodes => this;

        /// <inheritdoc/>
        public ITokenUpdateListener OnTokenUpdate
        {
            set
            {
                weakOnTokenUpdate = new WeakReference<ITokenUpdateListener>(value);
            }
        }

        /// <inheritdoc/>
        public byte[] CloseTabResponse { get; set; } = DefaultCloseTabResponse;

        /// <inheritdoc/>
        public async Task<bool> AuthenticationByCode2(string code, string redirectUrl)
        {
            var form = new Dictionary<string, string>
                                {
                                    { "code", code },
                                    { "client_id", clientId },
                                    { "client_secret", clientSecret },
                                    { "redirect_uri", redirectUrl }
                                };
            token = await http.PostForm<AuthToken>(TokenUrl, form).ConfigureAwait(false);
            if (token != null)
            {
                CallOnTokenUpdate(token.access_token);

                await Account.GetEndpoint().ConfigureAwait(false);

                return true;
            }

            return false;
        }

        public async Task<bool> AuthenticationByCode(string code, string redirectUrl)
        {
            var form = new Dictionary<string, string>
            {
                { "code", code },
                { "client_id", clientId },
                { "client_secret", clientSecret },
                { "redirect_uri", redirectUrl },
                { "grant_type", "authorization_code" }
            };

            using var httpClient = new System.Net.Http.HttpClient();

            using var content = new FormUrlEncodedContent(form);
            var response = await httpClient.PostAsync(TokenUrl, content).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine("Token request failed: " + response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            try
            {
                token = JsonSerializer.Deserialize<AuthToken>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to deserialize token: " + ex);
                return false;
            }

            if (token != null)
            {
                CallOnTokenUpdate(token.access_token);

                await Account.GetEndpoint().ConfigureAwait(false);

                return true;
            }

            return false;
        }

        /// <inheritdoc/>
        public string BuildLoginUrl(string redirectUrl)
        {
            Contract.Assert(redirectUrl != null);

            return $"{LoginUrlBase}?client_id={clientId}&response_type=code&redirect_uri={redirectUrl}";
        }

        public async Task<bool> AuthenticationByExternalBrowserOld3(
            TimeSpan timeout,
            CancellationToken? cancelToken = null,
            string unformatedRedirectUrl = "http://localhost:{0}/signin/",
            Func<int, int, int> portSelector = null)
        {
            string redirectUrl;
            using (var redirectListener = CreateListener(unformatedRedirectUrl, out redirectUrl, portSelector))
            {
                redirectListener.Start();

                var loginUrl = BuildLoginUrl(redirectUrl);

                // Только открываем redirect URL — он отдаст JS, который сам откроет loginUrl
                Process.Start(new ProcessStartInfo
                {
                    FileName = redirectUrl,
                    UseShellExecute = true
                });

                for (var times = 0; times < 2; times++)
                {
                    var contextTask = redirectListener.GetContextAsync();
                    var timeoutTask = cancelToken != null
                        ? Task.Delay(timeout, cancelToken.Value)
                        : Task.Delay(timeout);

                    var completed = await Task.WhenAny(contextTask, timeoutTask).ConfigureAwait(false);

                    if (completed == timeoutTask)
                    {
                        if (timeoutTask.IsCanceled)
                            return false;

                        throw new TimeoutException("No redirection detected");
                    }

                    var context = await contextTask.ConfigureAwait(false);
                    var uri = context.Request.Url;

                    if (uri == null)
                    {
                        await SendResponse(context.Response, DefaultCloseTabResponse).ConfigureAwait(false);
                        continue;
                    }

                    if (uri.Query.Contains("code=") || uri.Fragment.Contains("access_token") || uri.Query.Contains("error="))
                    {
                        await SendResponse(context.Response, DefaultCloseTabResponse).ConfigureAwait(false);
                        await ProcessRedirect(context, redirectUrl).ConfigureAwait(false);
                        return true;
                    }
                    else
                    {
                        var html = string.Format(DefaultOpenAuthResponse, loginUrl);
                        var bytes = Encoding.UTF8.GetBytes(html);
                        await SendResponse(context.Response, bytes).ConfigureAwait(false);
                        continue;
                    }
                }
            }

            return token != null;
        }

        public async Task<bool> AuthenticationByExternalBrowser(
            TimeSpan timeout,
            CancellationToken? cancelToken = null,
            string unformatedRedirectUrl = "http://localhost:{0}/signin/",
            Func<int, int, int> portSelector = null)
        {
            string redirectUrl;
            using (var redirectListener = CreateListener(unformatedRedirectUrl, out redirectUrl, portSelector))
            {
                redirectListener.Start();

                var loginurl = BuildLoginUrl(redirectUrl);

                var psi = new ProcessStartInfo
                {
                    FileName = redirectUrl,
                    UseShellExecute = true
                };

                using (var tabProcess = Process.Start(psi))
                {
                    for (var times = 0; times < 2; times++)
                    {
                        var task = redirectListener.GetContextAsync();
                        var timeoutTask = (cancelToken != null) ? Task.Delay(timeout, cancelToken.Value) : Task.Delay(timeout);
                        var anytask = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
                        if (anytask == task)
                        {
                            var context = await task.ConfigureAwait(false);
                            if (times == 0)
                            {
                                var loginResponse = Encoding.UTF8.GetBytes(string.Format(DefaultOpenAuthResponse, loginurl));
                                await SendResponse(context.Response, loginResponse).ConfigureAwait(false);
                            }
                            else
                            {
                                await ProcessRedirect(context, redirectUrl).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            if (timeoutTask.IsCanceled)
                            {
                                return false;
                            }

                            throw new TimeoutException("No redirection detected");
                        }
                    }
                }
            }

            return token != null;
        }

        /// <inheritdoc/>
        public async Task<bool> AuthenticationByExternalBrowserOld(TimeSpan timeout, CancellationToken? cancelToken = null, string unformatedRedirectUrl = "http://localhost:{0}/signin/", Func<int, int, int> portSelector = null)
        {
            string redirectUrl;
            using (var redirectListener = CreateListener(unformatedRedirectUrl, out redirectUrl, portSelector))
            {
                redirectListener.Start();
                var loginurl = BuildLoginUrl(redirectUrl);
                using (var tabProcess = Process.Start(redirectUrl))
                {
                    for (var times = 0; times < 2; times++)
                    {
                        var task = redirectListener.GetContextAsync();
                        var timeoutTask = (cancelToken != null) ? Task.Delay(timeout, cancelToken.Value) : Task.Delay(timeout);
                        var anytask = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);
                        if (anytask == task)
                        {
                            var context = await task.ConfigureAwait(false);
                            if (times == 0)
                            {
                                var loginResponse = Encoding.UTF8.GetBytes(string.Format(DefaultOpenAuthResponse, loginurl));
                                await SendResponse(context.Response, loginResponse).ConfigureAwait(false);
                            }
                            else
                            {
                                await ProcessRedirect(context, redirectUrl).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            if (timeoutTask.IsCanceled)
                            {
                                return false;
                            }

                            throw new TimeoutException("No redirection detected");
                        }
                    }
                }
            }

            return token != null;
        }

        private void CallOnTokenUpdate(string access_token)
        {
            ITokenUpdateListener action;
            if (weakOnTokenUpdate != null && weakOnTokenUpdate.TryGetTarget(out action))
            {
                action?.OnTokenUpdated(access_token);
            }
        }

        private HttpListener CreateListener(string redirectUrl, out string realRedirectUrl, Func<int, int, int> portSelector = null)
        {
            var listener = new HttpListener();
            int port = 0;
            int time = 0;
            while (true)
            {
                try
                {
                    port = (portSelector ?? DefaultPortSelector).Invoke(port, time++);
                    realRedirectUrl = string.Format(CultureInfo.InvariantCulture, redirectUrl, port);
                    listener.Prefixes.Add(realRedirectUrl);
                    return listener;
                }
                catch (HttpListenerException)
                {
                    // Skip, try another port
                }
                catch (InvalidOperationException)
                {
                    listener.Close();
                    throw;
                }
            }
        }

        private int DefaultPortSelector(int lastPort, int time)
        {
            if (time == 0)
            {
                return ListenerPortStart;
            }

            if (time > 2)
            {
                throw new InvalidOperationException("Cannot select port for redirect url");
            }

            return lastPort + 1;
        }

        private async Task<string> GetContentUrl() => (await Account.GetEndpoint().ConfigureAwait(false)).contentUrl;

        private async Task<string> GetMetadataUrl() => (await Account.GetEndpoint().ConfigureAwait(false)).metadataUrl;

        private async Task ProcessRedirect(HttpListenerContext context, string redirectUrl)
        {
            var error = HttpUtility.ParseQueryString(context.Request.Url.Query).Get("error_description");

            if (error != null)
            {
                throw new InvalidOperationException(error);
            }


            var code = HttpUtility.ParseQueryString(context.Request.Url.Query).Get("code");
            //if (code == null)
            //{
            //    Console.WriteLine(context.Request.Url.Query);
            //    Thread.Sleep(10_000);
            //}

            await SendResponse(context.Response, CloseTabResponse).ConfigureAwait(false);

            await AuthenticationByCode(code, redirectUrl).ConfigureAwait(false);
        }

        private async Task SendResponse(HttpListenerResponse response, byte[] body)
        {
            response.StatusCode = 200;
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            response.OutputStream.Close();
        }
    
        private async Task SettingsSetter(HttpWebRequest client)
        {
            // if (token != null && !updatingToken)
            // {
            //    client.Headers.Add("Authorization", "Bearer " + await GetToken().ConfigureAwait(false));
            // }

            client.CachePolicy = standartCache;
            client.UserAgent = "AZIACDDokanNet/" + GetType().Assembly.ImageRuntimeVersion;

            client.Timeout = 15000;

            client.AllowReadStreamBuffering = false;
            client.AllowWriteStreamBuffering = true;
            client.AutomaticDecompression = DecompressionMethods.GZip;
            client.PreAuthenticate = true;
            client.UseDefaultCredentials = true;
        }

        private string BuildMethodUrl(string method)
        {
            return $"{ApiUrl}/{method}?access_token={token.access_token}";
        }
    }
}
