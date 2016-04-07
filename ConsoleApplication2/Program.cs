// ----------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation.
// All rights reserved.
// THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
// KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.
// </copyright>
// ----------------------------------------------------------------------
// <summary>Program.cs</summary>
// ----------------------------------------------------------------------


using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/*
 * This is a code sample for translating a audio file using the speech translate API
*/
namespace CmdLineSpeechTranslate
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args == null || args.Length != 5)
            {
                Console.WriteLine("Usage: CmdLineSpeechTranslate.exe ClientId ClientSecret FilePath SrcLanguage TargetLanguage");
                Console.WriteLine("Example: CmdLineSpeechTranslate.exe ClientId ClientSecret helloworld.wav en-us es-es");
            }
            else
            {
                TranslateAudioFile(args[0], args[1], args[2], args[3], args[4]).Wait();
            }
            Console.WriteLine("Press Enter to continue: ");
            Console.ReadLine();
        }


        private static async Task TranslateAudioFile(string clientid, string clientsecret, string filePath, string from, string to)
        {
            var token = CancellationToken.None;
            // create the client
            using (var client = new SpeechTranslateFileClient(clientid, clientsecret))
            {
                // connect to the service
                await client.Connect(from, to, token);

                // start sending the file
                var sendTask = client.SendFile(File.ReadAllBytes(filePath), token);

                // start receiving
                foreach (var receiveTask in client.ReceiveTextMessages(token))
                {
                    var jsonResult = await receiveTask;
                    if (!String.IsNullOrWhiteSpace(jsonResult))
                    {
                        dynamic speechTranslateResult = JObject.Parse(jsonResult);
                        var startTime = TimeSpan.FromTicks(long.Parse((string)speechTranslateResult.audioTimeOffset));
                        var duration = TimeSpan.FromTicks(uint.Parse((string)speechTranslateResult.audioTimeSize));
                        var endTime = startTime + duration;

                        Console.WriteLine("[{0}-{1}]{2}", startTime, endTime, jsonResult);
                    }
                }

                await sendTask;
            }
        }
    }

    /// <summary>
    /// A wrapper around the ClientWebSocket to talk to the MTApi
    /// Expected File Format: PCM Wav 16bit, 16kHz, mono
    /// </summary>
    class SpeechTranslateFileClient : IDisposable
    {
        private const int PACKET_SIZE = 320 * 100; // 320 bytes = 10ms worth of audio, so we are sending 1 second of audio every 100ms (10x speed)
        private const int PACKET_RATE_IN_MS = 100;
        private const string AzureMarketPlaceUrl = "https://datamarket.accesscontrol.windows.net/v2/OAuth2-13";
        private const string AzureScope = "http://api.microsofttranslator.com";
        private const string SpeechTranslateUrl = @"wss://dev.microsofttranslator.com/speech/translate?from={0}&to={1}&features=TimingInfo&api-version=1.0";
        private static readonly Encoding UTF8 = new UTF8Encoding();

        private ClientWebSocket webSocket;
        private string clientId;
        private string clientSecret;

        /// <summary>
        /// Constructor for audio file translation
        /// </summary>
        /// <param name="clientId"></param>
        /// <param name="clientSecret"></param>
        public SpeechTranslateFileClient(string clientId, string clientSecret)
        {
            this.webSocket = new ClientWebSocket();
            this.clientId = clientId;
            this.clientSecret = clientSecret;

        }

        /// <summary>
        /// Connect to the server before sending audio file
        /// It will get the ADM credentials and add it to the header
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        public async Task Connect(string from, string to, CancellationToken token)
        {
            // get ADM token
            using (var httpClient = new HttpClient())
            {
                var response = await httpClient.PostAsync(AzureMarketPlaceUrl, new FormUrlEncodedContent(
                    new KeyValuePair<string, string>[] {
                        new KeyValuePair<string,string>("grant_type", "client_credentials"),
                        new KeyValuePair<string,string>("client_id", clientId),
                        new KeyValuePair<string,string>("client_secret", clientSecret),
                        new KeyValuePair<string,string>("scope", AzureScope),
                    }), token);

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();

                dynamic admAccessToken = JObject.Parse(json);

                var admToken = "Bearer " + admAccessToken.access_token;

                this.webSocket.Options.SetRequestHeader("Authorization", admToken);
            }

            var url = String.Format(SpeechTranslateUrl, from, to);

            await this.webSocket.ConnectAsync(new Uri(url), token);
        }

        /// <summary>
        /// Send audio file to server in chunks of PACKET_SIZE
        /// Then send some silence to signal the end of audio file
        /// Expected File Format: PCM Wav 16bit, 16kHz, mono
        /// </summary>
        /// <param name="bytes">raw bytes of audio file including header</param>
        /// <param name="token">Cancellation Token</param>
        /// <returns></returns>
        public async Task SendFile(byte[] bytes, CancellationToken token)
        {
            for (int i = 0; i < bytes.Length; i += PACKET_SIZE)
            {
                await this.webSocket.SendAsync(new ArraySegment<byte>(bytes, i, Math.Min(PACKET_SIZE, bytes.Length - i)), WebSocketMessageType.Binary, true, token);
                // throttle the system
                await Task.Delay(PACKET_RATE_IN_MS);
            }

            // send silence in the end to signal end of audio file
            for (int i = 0; i < 100 && !token.IsCancellationRequested; i++)
            {
                await Task.Delay(PACKET_RATE_IN_MS);
                await this.webSocket.SendAsync(new ArraySegment<byte>(new byte[PACKET_SIZE]), WebSocketMessageType.Binary, true, token);
            }

            await this.webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "end of file", token);
        }

        /// <summary>
        /// Start receiving result from the service
        /// </summary>
        /// <param name="token"></param>
        /// <returns>a Task<string> per message received</string></returns>
        public IEnumerable<Task<string>> ReceiveTextMessages(CancellationToken token)
        {
            while (this.webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var buffer = new ArraySegment<byte>(new byte[1024]);
                yield return this.webSocket.ReceiveAsync(buffer, token).ContinueWith(
                    task =>
                    {
                        var result = task.Result;
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var jsonOutput = UTF8.GetString(buffer.Array, 0, result.Count);
                            return jsonOutput;
                        }

                        return "";
                    });
            }
        }

        /// <summary>
        /// Dispose the websocket client object
        /// </summary>
        public void Dispose()
        {
            if (this.webSocket != null)
            {
                this.webSocket.Dispose();
                this.webSocket = null;
            }
        }
    }
}
